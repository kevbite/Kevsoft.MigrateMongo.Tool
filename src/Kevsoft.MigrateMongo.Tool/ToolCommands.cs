using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Kevsoft.MigrateMongo.Cli;
using Kevsoft.MigrateMongo.Config;
using Kevsoft.MigrateMongo.Protocol;
using Kevsoft.MigrateMongo.Tool.Tunnel;

namespace Kevsoft.MigrateMongo.Tool;

internal sealed class ToolCommands(string workingDirectory)
{
    private ConsoleRenderer _out = new(Console.Out, !Console.IsOutputRedirected);

    // Progress messages go to stderr in --json mode so stdout stays machine readable.
    private ConsoleRenderer _progress = new(Console.Out, !Console.IsOutputRedirected);

    public async Task<int> RunAsync(CliInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Json)
        {
            _progress = new ConsoleRenderer(Console.Error, false);
            _out = _progress;
        }

        try
        {
            return invocation.Command switch
            {
                "version" => Version(invocation),
                "init" => await InitAsync(invocation, cancellationToken),
                "create" => Create(invocation),
                _ => await RunInRunnerAsync(invocation, cancellationToken),
            };
        }
        catch (ConfigException e)
        {
            return Fail(invocation, ExitCodes.Failure, e.Message);
        }
        catch (ToolException e)
        {
            return Fail(invocation, e.ExitCode, e.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Fail(invocation, ExitCodes.Failure, "Cancelled.");
        }
    }

    private int Version(CliInvocation invocation)
    {
        if (invocation.Json)
        {
            WriteJsonResult(new CommandResult { ExitCode = 0, Message = ToolInfo.Description });
        }
        else
        {
            Console.Out.WriteLine(ToolInfo.Description);
        }

        return ExitCodes.Success;
    }

    private async Task<int> InitAsync(CliInvocation invocation, CancellationToken cancellationToken)
    {
        var configPath = Path.GetFullPath(invocation.ConfigFile ?? ConfigLoader.DefaultFileName, workingDirectory);
        if (File.Exists(configPath))
        {
            throw new ToolException(ExitCodes.Failure, $"{Relative(configPath)} already exists.");
        }

        var projectDirectory = Path.GetFullPath(invocation.Project ?? "Migrations", workingDirectory);
        var projectName = Path.GetFileName(projectDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var created = new List<string>();

        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        var existingProject = Directory.Exists(projectDirectory) ? Directory.GetFiles(projectDirectory, "*.csproj").FirstOrDefault() : null;
        if (existingProject is null)
        {
            Directory.CreateDirectory(Path.Combine(projectDirectory, "Migrations"));
            var projectFile = Path.Combine(projectDirectory, projectName + ".csproj");
            var targetFramework = await DetectTargetFrameworkAsync(cancellationToken);
            await File.WriteAllTextAsync(projectFile, Scaffolding.ProjectFile(targetFramework, ToolInfo.Version, ToolInfo.DefaultDriverVersion), cancellationToken);
            created.Add(projectFile);

            var programFile = Path.Combine(projectDirectory, "Program.cs");
            if (!File.Exists(programFile))
            {
                await File.WriteAllTextAsync(programFile, Scaffolding.ProgramFile, cancellationToken);
                created.Add(programFile);
            }
        }
        else
        {
            _progress.Muted($"Using existing project {Relative(existingProject)}; add a reference to Kevsoft.MigrateMongo.Runner if it doesn't have one.");
        }

        var databaseName = projectName.Split('.')[0].ToLowerInvariant();
        var projectRelative = Path.GetRelativePath(Path.GetDirectoryName(configPath)!, projectDirectory).Replace('\\', '/');
        await File.WriteAllTextAsync(configPath, Scaffolding.ConfigFile(projectRelative, databaseName), cancellationToken);
        created.Insert(0, configPath);

        foreach (var file in created)
        {
            _out.Info($"Created {Relative(file)}");
        }

        var next = "Next: set MONGODB_URL, then run `migrate-mongo create <Name>` and `migrate-mongo status`.";
        if (invocation.Json)
        {
            WriteJsonResult(new CommandResult { ExitCode = 0, Message = next });
        }
        else
        {
            _out.Info(next);
        }

        return ExitCodes.Success;
    }

    private int Create(CliInvocation invocation)
    {
        var className = Scaffolding.ToClassName(invocation.Name ?? "")
                        ?? throw new ToolException(ExitCodes.Failure, $"'{invocation.Name}' isn't a valid migration name; use something like AddEmailIndexToUsers.");
        var (configPath, config) = LoadConfig(invocation, projectOnly: true);
        var projectFile = ResolveProjectFile(configPath, config);
        var migrationsDirectory = Path.Combine(Path.GetDirectoryName(projectFile)!, "Migrations");
        Directory.CreateDirectory(migrationsDirectory);

        // Ids must be unique, so step forward a second if one was created in the same second.
        var time = DateTime.UtcNow;
        string id;
        while (true)
        {
            id = Scaffolding.NewId(time);
            if (Directory.GetFiles(migrationsDirectory, $"M{id}_*.cs").Length == 0)
            {
                break;
            }

            time = time.AddSeconds(1);
        }

        var path = Path.Combine(migrationsDirectory, $"M{id}_{className}.cs");
        var rootNamespace = Scaffolding.ToNamespace(Path.GetFileNameWithoutExtension(projectFile));
        File.WriteAllText(path, Scaffolding.MigrationFile(rootNamespace + ".Migrations", id, className, Scaffolding.ToKebabCase(className)));

        if (invocation.Json)
        {
            WriteJsonResult(new CommandResult { ExitCode = 0, Message = path, Applied = [id] });
        }
        else
        {
            _out.Info($"Created {Relative(path)}");
        }

        return ExitCodes.Success;
    }

    private async Task<int> RunInRunnerAsync(CliInvocation invocation, CancellationToken cancellationToken)
    {
        var (configPath, config) = LoadConfig(invocation);
        var configDirectory = Path.GetDirectoryName(configPath)!;

        string runnerPath;
        if (invocation.Runner is not null)
        {
            runnerPath = Path.GetFullPath(invocation.Runner, workingDirectory);
            if (!File.Exists(runnerPath))
            {
                throw new ToolException(ExitCodes.Failure, $"Runner '{invocation.Runner}' was not found.");
            }
        }
        else
        {
            var projectFile = ResolveProjectFile(configPath, config);
            if (!invocation.NoBuild)
            {
                await BuildAsync(projectFile, configDirectory, cancellationToken);
            }

            runnerPath = await GetTargetPathAsync(projectFile, configDirectory, cancellationToken);
        }

        var (fileName, prefix) = runnerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? (ProcessRunner.DotnetHost, new[] { runnerPath })
            : (runnerPath, Array.Empty<string>());

        var handshake = await HandshakeAsync(fileName, prefix, configDirectory, cancellationToken);
        var protocol = handshake.Protocol.Intersect(ProtocolVersions.Supported).DefaultIfEmpty(0).Max();
        if (protocol == 0)
        {
            var supported = string.Join(", ", ProtocolVersions.Supported);
            var theirs = string.Join(", ", handshake.Protocol);
            var advice = handshake.Protocol.DefaultIfEmpty(0).Min() > ProtocolVersions.Supported.Max()
                ? "Update the tool: dotnet tool update Kevsoft.MigrateMongo.Tool"
                : "Update the Kevsoft.MigrateMongo.Runner package in your migrations project.";
            throw new ToolException(ExitCodes.ProtocolMismatch,
                $"The runner uses Kevsoft.MigrateMongo {handshake.Library} (protocol {theirs}); this tool supports protocol {supported}. {advice}");
        }

        _progress.Muted($"Runner: Kevsoft.MigrateMongo {handshake.Library} · MongoDB.Driver {handshake.Driver} · .NET {handshake.Runtime} · protocol {protocol}");

        using var tunnel = config.Tunnel is null
            ? null
            : await SshTunnel.OpenAsync(config.Tunnel, config.MongoUrl, handshake.Driver, invocation.InsecureSkipHostKeyCheck, _progress.Warning, cancellationToken);
        if (tunnel is not null)
        {
            if (invocation.InsecureSkipHostKeyCheck)
            {
                _progress.Warning("The SSH server's host key was not verified (--insecure-skip-host-key-check).");
            }

            _progress.Muted($"Tunnel: {tunnel.Description}");
            config.MongoUrl = tunnel.MongoUrl;
        }

        var request = new RunnerRequest
        {
            Protocol = protocol,
            Command = invocation.Command,
            Arguments = invocation.Arguments,
            Config = config,
            Tool = ToolInfo.Description,
        };

        return await RunProtocolAsync(fileName, prefix, protocol, request, configDirectory, invocation.Json, cancellationToken);
    }

    private async Task<int> RunProtocolAsync(
        string fileName,
        string[] prefix,
        int protocol,
        RunnerRequest request,
        string workingDir,
        bool json,
        CancellationToken cancellationToken)
    {
        var info = ProcessRunner.StartInfo(fileName, [.. prefix, ProtocolVersions.ProtocolArgument, protocol.ToString(CultureInfo.InvariantCulture)], workingDir);
        info.RedirectStandardInput = true;

        using var process = Process.Start(info) ?? throw new ToolException(ExitCodes.Failure, $"Failed to start the runner '{fileName}'.");
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                Console.Error.WriteLine(e.Data);
            }
        };
        process.BeginErrorReadLine();

        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, ProtocolJsonContext.Default.RunnerRequest));
        await process.StandardInput.FlushAsync(CancellationToken.None);

        await using var registration = cancellationToken.Register(() =>
        {
            try
            {
                _progress.Muted("Cancelling…");
                process.StandardInput.WriteLine(JsonSerializer.Serialize(new RunnerControl { Type = "cancel" }, ProtocolJsonContext.Default.RunnerControl));
                process.StandardInput.Flush();
            }
            catch (Exception)
            {
                // The runner already exited.
            }
        });

        CommandResult? result = null;
        while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
        {
            RunnerEvent? runnerEvent = null;
            try
            {
                runnerEvent = JsonSerializer.Deserialize(line, ProtocolJsonContext.Default.RunnerEvent);
            }
            catch (JsonException)
            {
            }

            if (runnerEvent is null)
            {
                Console.Error.WriteLine(line);
                continue;
            }

            if (runnerEvent.Type == EventTypes.Result)
            {
                result = runnerEvent.Result;
            }

            if (json)
            {
                Console.Out.WriteLine(line);
                Console.Out.Flush();
            }
            else
            {
                _out.Render(runnerEvent);
            }
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        await process.WaitForExitAsync(CancellationToken.None);

        if (result is null)
        {
            throw new ToolException(ExitCodes.Failure, $"The runner exited unexpectedly with code {process.ExitCode} without reporting a result.");
        }

        return result.ExitCode;
    }

    private static async Task<HandshakeInfo> HandshakeAsync(string fileName, string[] prefix, string workingDir, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(fileName, [.. prefix, ProtocolVersions.HandshakeArgument], workingDir, cancellationToken);
        var line = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith('{'));
        HandshakeInfo? handshake = null;
        if (result.ExitCode == 0 && line is not null)
        {
            try
            {
                handshake = JsonSerializer.Deserialize(line, ProtocolJsonContext.Default.HandshakeInfo);
            }
            catch (JsonException)
            {
            }
        }

        return handshake ?? throw new ToolException(ExitCodes.ProtocolMismatch,
            "The migrations project didn't respond to the migrate-mongo handshake. Make sure its Program.cs calls " +
            "`return await MigrateMongoRunner.RunAsync(args);` and it references Kevsoft.MigrateMongo.Runner." +
            Environment.NewLine + (result.StandardOutput + result.StandardError).Trim());
    }

    private async Task BuildAsync(string projectFile, string workingDir, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await ProcessRunner.RunAsync(
            ProcessRunner.DotnetHost,
            ["build", projectFile, "-nologo", "-v", "q", "-clp:NoSummary"],
            workingDir,
            cancellationToken);
        if (result.ExitCode != 0)
        {
            Console.Error.WriteLine(result.StandardOutput.Trim());
            Console.Error.WriteLine(result.StandardError.Trim());
            throw new ToolException(ExitCodes.BuildFailed, $"Building {Relative(projectFile)} failed.");
        }

        _progress.Muted($"Built {Relative(projectFile)} ({stopwatch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s)");
    }

    private static async Task<string> GetTargetPathAsync(string projectFile, string workingDir, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            ProcessRunner.DotnetHost,
            ["msbuild", projectFile, "-nologo", "-getProperty:TargetPath"],
            workingDir,
            cancellationToken);
        var targetPath = result.StandardOutput.Trim();
        if (result.ExitCode != 0 || targetPath.Length == 0)
        {
            throw new ToolException(ExitCodes.BuildFailed,
                $"Couldn't determine the output of {projectFile}. Projects with multiple target frameworks aren't supported; set a single <TargetFramework>." +
                Environment.NewLine + (result.StandardOutput + result.StandardError).Trim());
        }

        if (!File.Exists(targetPath))
        {
            throw new ToolException(ExitCodes.BuildFailed, $"{targetPath} doesn't exist. Build the project, or run without --no-build.");
        }

        return targetPath;
    }

    private async Task<string> DetectTargetFrameworkAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(ProcessRunner.DotnetHost, ["--version"], workingDirectory, cancellationToken);
            var major = result.StandardOutput.Trim().Split('.')[0];
            if (result.ExitCode == 0 && int.TryParse(major, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) && version >= 8)
            {
                return $"net{version}.0";
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
        }

        return "net8.0";
    }

    private (string Path, ResolvedConfig Config) LoadConfig(CliInvocation invocation, bool projectOnly = false)
    {
        var path = ConfigLoader.Find(invocation.ConfigFile, workingDirectory)
                   ?? throw new ConfigException($"No {ConfigLoader.DefaultFileName} found in {workingDirectory}. Run `migrate-mongo init` to create one, or pass --file.");
        return (path, ConfigLoader.LoadFile(path, invocation.Environment, projectOnly: projectOnly));
    }

    private static string ResolveProjectFile(string configPath, ResolvedConfig config)
    {
        var project = config.Project ?? throw new ConfigException($"'project' is not set in {configPath}.");
        if (File.Exists(project))
        {
            return project;
        }

        if (Directory.Exists(project))
        {
            var candidates = Directory.GetFiles(project, "*.csproj");
            return candidates.Length switch
            {
                1 => candidates[0],
                0 => throw new ConfigException($"No .csproj file found in '{project}'."),
                _ => throw new ConfigException($"More than one .csproj file found in '{project}'; set 'project' to the file."),
            };
        }

        throw new ConfigException($"Project '{project}' was not found.");
    }

    private int Fail(CliInvocation invocation, int exitCode, string message)
    {
        if (invocation.Json)
        {
            WriteJsonResult(new CommandResult { ExitCode = exitCode, Message = message });
        }
        else
        {
            new ConsoleRenderer(Console.Error, !Console.IsErrorRedirected).Error(message);
        }

        return exitCode;
    }

    private static void WriteJsonResult(CommandResult result)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(new RunnerEvent { Type = EventTypes.Result, Result = result }, ProtocolJsonContext.Default.RunnerEvent));
        Console.Out.Flush();
    }

    private string Relative(string path) => Path.GetRelativePath(workingDirectory, path);
}

internal sealed class ToolException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
