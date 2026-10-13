using System.Runtime.InteropServices;
using MongoDB.Driver;
using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests;

/// <summary><c>migrate-mongo bundle</c>: self-contained single-file runners for machines without .NET.</summary>
public sealed class BundleTests(IntegrationEnvironment environment)
{
    private const string UrlVariable = "MIGRATE_MONGO_IT_URL";

    [Fact]
    public async Task Bundle_runs_on_its_own_and_through_the_tool()
    {
        await using var workspace = CreateWorkspace();
        var variables = new Dictionary<string, string?> { [UrlVariable] = environment.MongoUrl };

        var bundle = await workspace.RunAsync(["bundle", "-o", "out"], variables);

        bundle.ExitCode.ShouldBe(0, bundle.ToString());
        bundle.Output.ShouldContain($"Bundle: Kevsoft.MigrateMongo {environment.PackageVersion} · MongoDB.Driver 3.12.0 · .NET 8.0.");
        var executable = workspace.PathOf("out", OperatingSystem.IsWindows() ? "BasicMigrations.exe" : "BasicMigrations");
        File.Exists(executable).ShouldBeTrue();
        File.ReadAllText(workspace.PathOf("out", "migrate-mongo.yaml")).ShouldBe(File.ReadAllText(workspace.ConfigPath));

        // The bundle needs neither the project nor a config file in the current directory.
        Directory.Delete(workspace.PathOf("BasicMigrations"), recursive: true);
        var elsewhere = Directory.CreateDirectory(workspace.PathOf("elsewhere")).FullName;
        var up = await ProcessHelper.RunAsync(executable, ["up"], elsewhere, variables, TimeSpan.FromMinutes(2));

        up.ExitCode.ShouldBe(0, up.ToString());
        var states = workspace.Find("_migrations");
        states.Select(s => s["_id"].AsString).ShouldBe(["20260101000001", "20260101000002", "20260101000003"]);
        states.ShouldAllBe(s => s["state"].AsString == "applied" && s["appliedBy"]["runtime"].AsString.StartsWith("8.0."));

        var down = await workspace.RunAsync(["down", "--batch", "--runner", executable], variables);

        down.ExitCode.ShouldBe(0, down.ToString());
        down.Output.ShouldContain($"Runner: Kevsoft.MigrateMongo {environment.PackageVersion}");
        workspace.Find("_migrations").ShouldBeEmpty();
    }

    [Fact]
    public async Task Linux_bundle_runs_in_a_container_without_dotnet()
    {
        Assert.SkipWhen(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MIGRATE_MONGO_TEST_MONGODB_URL")),
            "Needs the test MongoDB container to be reachable from another container.");

        await using var workspace = CreateWorkspace();
        var runtimeIdentifier = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";

        // Built on any OS: only Native AOT needs to be built on the target platform.
        var bundle = await workspace.RunAsync(["bundle", "-r", runtimeIdentifier, "-o", "out"], new Dictionary<string, string?> { [UrlVariable] = environment.MongoUrl });
        bundle.ExitCode.ShouldBe(0, bundle.ToString());

        var url = new MongoUrlBuilder(environment.MongoUrl);
        url.Server = new MongoServerAddress("host.docker.internal", url.Server.Port);
        var run = await ProcessHelper.RunAsync("docker",
            [
                "run", "--rm", "--add-host", "host.docker.internal:host-gateway",
                "-e", $"{UrlVariable}={url}", "-v", $"{workspace.PathOf("out")}:/bundle", "-w", "/",
                "mcr.microsoft.com/dotnet/runtime-deps:8.0", "/bundle/BasicMigrations", "up",
            ],
            workspace.Directory, null, TimeSpan.FromMinutes(5));

        run.ExitCode.ShouldBe(0, run.ToString());
        var states = workspace.Find("_migrations");
        states.Select(s => s["_id"].AsString).ShouldBe(["20260101000001", "20260101000002", "20260101000003"]);
        states.ShouldAllBe(s => s["state"].AsString == "applied" && s["appliedBy"]["host"].AsString != Environment.MachineName);
    }

    private Workspace CreateWorkspace()
    {
        var workspace = Workspace.FromFixture(environment, "BasicMigrations");
        workspace.WriteConfigFile($$"""
            project: BasicMigrations
            mongodb:
              url: ${{{UrlVariable}}}
              databaseName: {{workspace.DatabaseName}}
            lockWaitTimeout: 5
            """);
        return workspace;
    }
}
