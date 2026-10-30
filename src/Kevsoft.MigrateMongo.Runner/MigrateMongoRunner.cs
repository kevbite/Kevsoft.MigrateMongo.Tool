using System.CommandLine;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Kevsoft.MigrateMongo.Cli;
using Kevsoft.MigrateMongo.Config;
using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.MongoDriver;
using Kevsoft.MigrateMongo.Protocol;
using Kevsoft.MigrateMongo.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Kevsoft.MigrateMongo;

public sealed class MigrateMongoRunnerOptions
{
    /// <summary>Register services that migrations take as constructor dependencies.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }

    /// <summary>
    /// Assemblies to load C# migrations from. When empty, the entry assembly and the assemblies it references are scanned
    /// for source-generated migrations.
    /// </summary>
    public List<Assembly> Assemblies { get; } = [];

    /// <summary>Additional migration sources (e.g. non C# loaders).</summary>
    public List<IMigrationSource> Sources { get; } = [];

    /// <summary>
    /// Opens the config's <c>tunnel</c> when run standalone (including as a bundle). Reference
    /// <c>Kevsoft.MigrateMongo.Tunnel</c> and set <c>new SshTunnelProvider()</c>. Not needed when the migrate-mongo tool
    /// runs the migrations: the tool opens the tunnel itself.
    /// </summary>
    public IMongoTunnelProvider? TunnelProvider { get; set; }
}

/// <summary>
/// Entry point for a migrations console project: <c>return await MigrateMongoRunner.RunAsync(args);</c>
/// </summary>
public static class MigrateMongoRunner
{
    public static Task<int> RunAsync(string[] args) => RunAsync(args, new MigrateMongoRunnerOptions());

    public static Task<int> RunAsync(string[] args, Action<IServiceCollection> configureServices) =>
        RunAsync(args, new MigrateMongoRunnerOptions { ConfigureServices = configureServices });

    public static async Task<int> RunAsync(string[] args, MigrateMongoRunnerOptions options)
    {
        var entryAssembly = Assembly.GetEntryAssembly();

        if (args.Length > 0 && args[0] == ProtocolVersions.HandshakeArgument)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(Handshake(), ProtocolJsonContext.Default.HandshakeInfo));
            return ExitCodes.Success;
        }

        if (args.Length > 1 && args[0] == ProtocolVersions.ProtocolArgument)
        {
            return await RunProtocolAsync(args[1], options, entryAssembly);
        }

        return await RunStandaloneAsync(args, options, entryAssembly);
    }

    public static string LibraryVersion => VersionOf(typeof(MigrateMongoRunner).Assembly);

    internal static HandshakeInfo Handshake() => new()
    {
        Protocol = ProtocolVersions.Supported,
        Library = LibraryVersion,
        Driver = MongoDriverBackend.DriverVersion,
        Runtime = Environment.Version.ToString(),
    };

    private static async Task<int> RunProtocolAsync(string protocolArgument, MigrateMongoRunnerOptions options, Assembly? entryAssembly)
    {
        // Anything migrations write to Console goes to stderr so stdout only carries protocol messages.
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(Console.Error);
        var sink = new JsonLineEventSink(stdout);

        if (!int.TryParse(protocolArgument, out var protocol) || !ProtocolVersions.Supported.Contains(protocol))
        {
            sink.Result(new CommandResult
            {
                ExitCode = ExitCodes.ProtocolMismatch,
                Message = $"Protocol {protocolArgument} is not supported by Kevsoft.MigrateMongo {LibraryVersion} (supports {string.Join(", ", ProtocolVersions.Supported)}).",
            });
            return ExitCodes.ProtocolMismatch;
        }

        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var line = await stdin.ReadLineAsync();
        RunnerRequest? request;
        try
        {
            request = line is null ? null : JsonSerializer.Deserialize(line, ProtocolJsonContext.Default.RunnerRequest);
        }
        catch (JsonException e)
        {
            request = null;
            sink.Log("error", $"Invalid request: {e.Message}");
        }

        if (request is null)
        {
            sink.Result(new CommandResult { ExitCode = ExitCodes.ProtocolMismatch, Message = "No valid request was received on stdin." });
            return ExitCodes.ProtocolMismatch;
        }

        using var cancellation = new CancellationTokenSource();

        // Ctrl+C goes to the whole console process group. The tool turns it into a "cancel" message (and stdin closes
        // if the tool dies), so don't let it kill this process mid-migration.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        });
        _ = Task.Run(async () =>
        {
            // A "cancel" message, or stdin closing (the tool went away), cancels the run.
            try
            {
                while (await stdin.ReadLineAsync() is { } control)
                {
                    var message = JsonSerializer.Deserialize(control, ProtocolJsonContext.Default.RunnerControl);
                    if (message?.Type == "cancel")
                    {
                        break;
                    }
                }
            }
            catch (Exception)
            {
                // Treat unreadable input as a cancellation.
            }

            try
            {
                await cancellation.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
            }
        });

        var result = await ExecuteAsync(request.Command, request.Arguments, request.Config, request.Tool, options, entryAssembly, sink, cancellation.Token);
        sink.Result(result);
        return result.ExitCode;
    }

    private static async Task<int> RunStandaloneAsync(string[] args, MigrateMongoRunnerOptions options, Assembly? entryAssembly)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        });

        var root = MigrateMongoCli.Create(
            $"Kevsoft.MigrateMongo runner {LibraryVersion}",
            includeToolCommands: false,
            async (invocation, ct) =>
            {
                IMigrationEventSink sink = invocation.Json
                    ? new JsonLineEventSink(Console.Out)
                    : new RendererEventSink(new ConsoleRenderer(Console.Out, !Console.IsOutputRedirected));

                CommandResult result;
                try
                {
                    var path = ConfigLoader.Find(invocation.ConfigFile, Environment.CurrentDirectory)
                               ?? ConfigLoader.Find(null, AppContext.BaseDirectory)
                               ?? throw new ConfigException($"No {ConfigLoader.DefaultFileName} found in the current directory.");
                    var config = ConfigLoader.LoadFile(path, invocation.Environment);
                    using var tunnel = await OpenTunnelAsync(config, options, invocation.InsecureSkipHostKeyCheck, sink, ct);
                    result = await ExecuteAsync(invocation.Command, invocation.Arguments, config, null, options, entryAssembly, sink, ct);
                }
                catch (ConfigException e)
                {
                    result = new CommandResult { ExitCode = ExitCodes.Failure, Message = e.Message };
                }
                catch (MongoTunnelException e)
                {
                    result = new CommandResult { ExitCode = ExitCodes.Failure, Message = e.Message };
                }

                sink.OnEvent(new RunnerEvent { Type = EventTypes.Result, Result = result });
                return result.ExitCode;
            });

        // System.CommandLine's own handler would end the process 2s after Ctrl+C/SIGTERM, before the lock is released.
        return await root.Parse(args).InvokeAsync(new InvocationConfiguration { ProcessTerminationTimeout = null }, cancellation.Token);
    }

    private static async Task<IMongoTunnel?> OpenTunnelAsync(
        ResolvedConfig config,
        MigrateMongoRunnerOptions options,
        bool skipHostKeyCheck,
        IMigrationEventSink sink,
        CancellationToken cancellationToken)
    {
        if (config.Tunnel is null)
        {
            return null;
        }

        ConfigLoader.EnsureConnectable(config);
        if (options.TunnelProvider is null)
        {
            throw new ConfigException(
                "The config has a 'tunnel'. To open it without the migrate-mongo tool, add the Kevsoft.MigrateMongo.Tunnel package and set " +
                "`TunnelProvider = new SshTunnelProvider()` in MigrateMongoRunnerOptions; or run through the tool (`migrate-mongo up --runner <this runner>`).");
        }

        void Log(string level, string message) => sink.OnEvent(new RunnerEvent { Type = EventTypes.Log, Level = level, Message = message });

        var tunnel = await options.TunnelProvider.OpenAsync(
            config.Tunnel,
            config.MongoUrl,
            new MongoTunnelOptions { DriverVersion = MongoDriverBackend.DriverVersion, SkipHostKeyCheck = skipHostKeyCheck, Warn = m => Log("warning", m) },
            cancellationToken);
        if (skipHostKeyCheck)
        {
            Log("warning", "The SSH server's host key was not verified (--insecure-skip-host-key-check).");
        }

        Log("info", $"Tunnel: {tunnel.Description}");
        config.MongoUrl = tunnel.MongoUrl;
        return tunnel;
    }

    internal static async Task<CommandResult> ExecuteAsync(
        string command,
        CommandArguments arguments,
        ResolvedConfig config,
        string? tool,
        MigrateMongoRunnerOptions options,
        Assembly? entryAssembly,
        IMigrationEventSink sink,
        CancellationToken cancellationToken)
    {
        if (!MigrateMongoCli.RunnerCommands.Contains(command))
        {
            return new CommandResult { ExitCode = ExitCodes.Failure, Message = $"The runner doesn't support the '{command}' command." };
        }

        try
        {
            ConfigLoader.EnsureConnectable(config);
            var migrations = MigrationDiscovery.Discover(options, entryAssembly);
            if (migrations.Count == 0 && command is not ("unlock" or "history"))
            {
                // Silence here would hide a build that skipped the source generator: `up` would report nothing to do.
                sink.OnEvent(new RunnerEvent
                {
                    Type = EventTypes.Log,
                    Level = "warning",
                    Message = "No migrations were found. If you expected some, check the build output for warning CS9057 " +
                              "(the compiler is too old for the Kevsoft.MigrateMongo source generator), or add the assembly " +
                              "that contains them to MigrateMongoRunnerOptions.Assemblies.",
                });
            }

            using var loggerFactory = new EventSinkLoggerFactory(sink);
            MongoDriverBackend? backend = null;
            var services = new ServiceCollection();
            services.AddSingleton<ILoggerFactory>(loggerFactory);
            services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
            services.AddSingleton<IMongoClient>(_ => backend!.Client);
            services.AddSingleton<IMongoDatabase>(_ => backend!.Database);
            options.ConfigureServices?.Invoke(services);
            await using var provider = services.BuildServiceProvider();

            backend = MongoDriverBackend.Create(config, provider, loggerFactory);
            try
            {
                var engine = new MigrationEngine(backend.Store, backend.Executor, migrations, CreateEngineOptions(config, tool), sink);
                return await engine.ExecuteAsync(command, arguments, cancellationToken);
            }
            finally
            {
                (backend.Client as IDisposable)?.Dispose();
            }
        }
        catch (ConfigException e)
        {
            return new CommandResult { ExitCode = ExitCodes.Failure, Message = e.Message };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new CommandResult { ExitCode = ExitCodes.Failure, Message = "Cancelled." };
        }
        catch (Exception e)
        {
            return new CommandResult { ExitCode = ExitCodes.Failure, Message = $"{e.GetType().Name}: {e.Message}" };
        }
    }

    private static EngineOptions CreateEngineOptions(ResolvedConfig config, string? tool) => new()
    {
        LockTtl = TimeSpan.FromSeconds(config.LockTtlSeconds),
        LockWaitTimeout = TimeSpan.FromSeconds(config.LockWaitTimeoutSeconds),
        OnChecksumMismatch = config.OnChecksumMismatch,
        Transaction = config.Transaction,
        AllowOutOfOrder = config.AllowOutOfOrder,
        TransactionRetryTimeout = TimeSpan.FromSeconds(config.TransactionRetryTimeoutSeconds),
        Environment = config.Environment,
        Provenance = new Provenance
        {
            Host = Environment.MachineName,
            User = Environment.UserName,
            Tool = tool,
            Library = LibraryVersion,
            Driver = MongoDriverBackend.DriverVersion,
            Runtime = Environment.Version.ToString(),
        },
    };

    private static string VersionOf(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? assembly.GetName().Version?.ToString()
        ?? "unknown";
}
