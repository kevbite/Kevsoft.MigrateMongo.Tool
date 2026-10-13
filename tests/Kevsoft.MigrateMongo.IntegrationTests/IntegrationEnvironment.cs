using System.Diagnostics;
using System.Runtime.InteropServices;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Kevsoft.MigrateMongo.IntegrationTests;
using MongoDB.Driver;

[assembly: AssemblyFixture(typeof(IntegrationEnvironment))]

namespace Kevsoft.MigrateMongo.IntegrationTests;

/// <summary>
/// Shared, once-per-run setup: a MongoDB Atlas Local container, the repo packed into a local NuGet feed, and the
/// migrate-mongo tool installed from that feed exactly as a user would install it.
/// </summary>
/// <remarks>
/// Overrides (useful on CI or when iterating locally):
/// <list type="bullet">
/// <item><c>MIGRATE_MONGO_TEST_MONGODB_URL</c> – use this MongoDB instead of starting a container (needs a replica set for transactions).</item>
/// <item><c>MIGRATE_MONGO_TEST_IMAGE</c> – container image to start instead of <c>mongodb/mongodb-atlas-local:8.0</c>.</item>
/// <item><c>MIGRATE_MONGO_PACKAGE_VERSION</c> + <c>MIGRATE_MONGO_PACKAGE_FEED</c> – use already packed packages instead of packing.</item>
/// </list>
/// </remarks>
public sealed class IntegrationEnvironment : IAsyncLifetime
{
    private const string AtlasLocalImage = "mongodb/mongodb-atlas-local:8.0";

    private static readonly string[] PackableProjects =
    [
        "Kevsoft.MigrateMongo.Abstractions",
        "Kevsoft.MigrateMongo.Contracts",
        "Kevsoft.MigrateMongo",
        "Kevsoft.MigrateMongo.MongoDriver",
        "Kevsoft.MigrateMongo.Runner",
        "Kevsoft.MigrateMongo.Hosting",
        "Kevsoft.MigrateMongo.Tunnel",
        "Kevsoft.MigrateMongo.Templates",
        "Kevsoft.MigrateMongo.Tool",
    ];

    private IContainer? _container;

    public string RepositoryRoot { get; } = FindRepositoryRoot();

    public string MongoUrl { get; private set; } = "";

    public IMongoClient Client { get; private set; } = null!;

    public string PackageVersion { get; private set; } = "";

    public string PackageFeed { get; private set; } = "";

    public string ToolPath { get; private set; } = "";

    /// <summary>Isolated NuGet package cache so test package versions never leak into the user's global cache.</summary>
    public string NuGetPackages => Path.Combine(ArtifactsDirectory, "nuget-packages");

    private string ArtifactsDirectory => Path.Combine(RepositoryRoot, "artifacts", "integration");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(ArtifactsDirectory);
        await Task.WhenAll(StartMongoAsync(), PreparePackagesAsync());
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private async Task StartMongoAsync()
    {
        var url = Environment.GetEnvironmentVariable("MIGRATE_MONGO_TEST_MONGODB_URL");
        if (string.IsNullOrEmpty(url))
        {
            var image = Environment.GetEnvironmentVariable("MIGRATE_MONGO_TEST_IMAGE") is { Length: > 0 } custom ? custom : AtlasLocalImage;
            _container = new ContainerBuilder(image)
                .WithPortBinding(27017, assignRandomHostPort: true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilContainerIsHealthy())
                .Build();
            await _container.StartAsync();
            url = $"mongodb://{_container.Hostname}:{_container.GetMappedPublicPort(27017)}/?directConnection=true";
        }

        MongoUrl = url;
        Client = new MongoClient(url);
    }

    private async Task PreparePackagesAsync()
    {
        var version = Environment.GetEnvironmentVariable("MIGRATE_MONGO_PACKAGE_VERSION");
        var feed = Environment.GetEnvironmentVariable("MIGRATE_MONGO_PACKAGE_FEED");
        if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(feed))
        {
            // A unique version per run means the package cache can never serve stale packages.
            version = $"0.0.0-it.{DateTime.UtcNow:yyyyMMddHHmmss}";
            feed = Path.Combine(ArtifactsDirectory, "feed");
            foreach (var project in PackableProjects)
            {
                await Dotnet.RunAsync(RepositoryRoot, null,
                    "pack", Path.Combine(RepositoryRoot, "src", project, project + ".csproj"), "-c", "Release", "-o", feed,
                    $"-p:MinVerVersionOverride={version}", "-nologo");
            }

            // .NET 10+ SDKs install the tool from its package for this RID. Native AOT needs a platform linker, so
            // locally that package is framework-dependent; CI tests the real Native AOT package.
            await Dotnet.RunAsync(RepositoryRoot, null,
                "pack", Path.Combine(RepositoryRoot, "src", "Kevsoft.MigrateMongo.Tool", "Kevsoft.MigrateMongo.Tool.csproj"), "-c", "Release", "-o", feed,
                "-r", RuntimeInformation.RuntimeIdentifier, "-p:PublishAot=false", $"-p:MinVerVersionOverride={version}", "-nologo");
        }

        PackageVersion = version;
        PackageFeed = Path.GetFullPath(feed);

        var toolDirectory = Path.Combine(ArtifactsDirectory, "tool", version);
        if (Directory.Exists(toolDirectory))
        {
            Directory.Delete(toolDirectory, recursive: true);
        }

        await Dotnet.RunAsync(RepositoryRoot, ProcessEnvironment,
            "tool", "install", "Kevsoft.MigrateMongo.Tool", "--version", version, "--add-source", PackageFeed, "--tool-path", toolDirectory);
        // RID-specific tools get a .cmd shim on Windows rather than an apphost.
        ToolPath = OperatingSystem.IsWindows()
            ? new[] { "migrate-mongo.exe", "migrate-mongo.cmd" }.Select(name => Path.Combine(toolDirectory, name)).First(File.Exists)
            : Path.Combine(toolDirectory, "migrate-mongo");
    }

    /// <summary>Environment variables for every process the tests start; <c>null</c> removes an inherited variable.</summary>
    public IReadOnlyDictionary<string, string?> ProcessEnvironment => new Dictionary<string, string?>
    {
        ["NUGET_PACKAGES"] = NuGetPackages,
        // Removed (not set) so the tool runs as it would on a machine where users haven't set them (CI sets them).
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = null,
        ["DOTNET_NOLOGO"] = null,
        ["MSBUILDDISABLENODEREUSE"] = "1",
        ["NO_COLOR"] = "1",
    };

    public IMongoDatabase Database(string name) => Client.GetDatabase(name);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Kevsoft.MigrateMongo.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Couldn't find the repository root (Kevsoft.MigrateMongo.slnx).");
    }
}

internal static class Dotnet
{
    public static async Task RunAsync(string workingDirectory, IReadOnlyDictionary<string, string?>? environment, params string[] arguments)
    {
        var result = await ProcessHelper.RunAsync("dotnet", arguments, workingDirectory, environment, TimeSpan.FromMinutes(10));
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet {string.Join(' ', arguments)} failed ({result.ExitCode}):{System.Environment.NewLine}{result.Output}{result.Error}");
        }
    }
}

public sealed record ProcessResult(int ExitCode, string Output, string Error)
{
    public override string ToString() => $"exit {ExitCode}{System.Environment.NewLine}--- stdout ---{System.Environment.NewLine}{Output}--- stderr ---{System.Environment.NewLine}{Error}";
}

internal static class ProcessHelper
{
    // Variables set by `dotnet test`/MSBuild that would make child builds pick up the wrong SDK or MSBuild.
    private static readonly string[] InheritedVariablesToRemove =
        ["MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBuildLoadMicrosoftTargetsReadOnly", "DOTNET_HOST_PATH", "VSTEST_HOST_DEBUG"];

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        TimeSpan timeout)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var name in InheritedVariablesToRemove)
        {
            info.Environment.Remove(name);
        }

        foreach (var (key, value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                info.Environment.Remove(key);
            }
            else
            {
                info.Environment[key] = value;
            }
        }

        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{fileName} {string.Join(' ', arguments)} didn't finish within {timeout}.{System.Environment.NewLine}{await output}{await error}");
        }

        return new ProcessResult(process.ExitCode, await output, await error);
    }
}
