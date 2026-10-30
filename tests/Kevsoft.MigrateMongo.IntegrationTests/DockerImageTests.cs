using System.Runtime.InteropServices;
using MongoDB.Driver;
using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests;

/// <summary>
/// The <c>ghcr.io/kevbite/migrate-mongo</c> images. CI builds them from the AOT binaries and sets
/// <c>MIGRATE_MONGO_TEST_TOOL_IMAGE</c> / <c>MIGRATE_MONGO_TEST_BUNDLE_IMAGE</c>; locally the tests skip unless those are set.
/// </summary>
public sealed class DockerImageTests(IntegrationEnvironment environment)
{
    private const string UrlVariable = "MIGRATE_MONGO_IT_URL";
    private static readonly string[] AppliedIds = ["20260101000001", "20260101000002", "20260101000003"];

    [Fact]
    public async Task Tool_image_builds_and_runs_a_migrations_project()
    {
        var image = ImageOrSkip("MIGRATE_MONGO_TEST_TOOL_IMAGE");
        await using var workspace = CreateWorkspace();
        // Inside the container the feed is mounted at /feed, and the image's SDK is used rather than the repo's pinned one.
        File.WriteAllText(workspace.PathOf("nuget.config"), File.ReadAllText(workspace.PathOf("nuget.config")).Replace(environment.PackageFeed, "/feed"));
        File.Delete(workspace.PathOf("global.json"));

        var run = await DockerRunAsync(image, workspace, ["-v", $"{environment.PackageFeed}:/feed:ro"], "up");

        run.ExitCode.ShouldBe(0, run.ToString());
        run.Output.ShouldContain("✔ 20260101000003");
        AppliedIds.ShouldBe(workspace.Find("_migrations").Select(s => s["_id"].AsString));
    }

    [Fact]
    public async Task Tool_image_applies_and_reports_collection_definitions()
    {
        var image = ImageOrSkip("MIGRATE_MONGO_TEST_TOOL_IMAGE");
        await using var workspace = CreateWorkspace("CollectionMigrations");
        File.WriteAllText(workspace.PathOf("nuget.config"), File.ReadAllText(workspace.PathOf("nuget.config")).Replace(environment.PackageFeed, "/feed"));
        File.Delete(workspace.PathOf("global.json"));
        string[] feed = ["-v", $"{environment.PackageFeed}:/feed:ro"];

        // The AOT tool reads the runner's collection events and results, and writes them as text and as JSON.
        var up = await DockerRunAsync(image, workspace, feed, "up");
        up.ExitCode.ShouldBe(0, up.ToString());
        up.Output.ShouldContain("⟳ orders");
        up.Output.ShouldContain("+ create   companyId_1_createdAt_-1   { companyId: 1, createdAt: -1 }");
        up.Output.ShouldContain("Applied 1 migration(s) in batch 1 and changed 2 collection(s).");
        workspace.Database.GetCollection<MongoDB.Bson.BsonDocument>("orders").Indexes.List().ToList().Count.ShouldBe(8);

        var status = await DockerRunAsync(image, workspace, feed, "status", "--check", "--no-build");
        status.ExitCode.ShouldBe(0, status.ToString());
        status.Output.ShouldContain(" orders      in sync");

        var show = await DockerRunAsync(image, workspace, feed, "show", "orders", "--no-build");
        show.ExitCode.ShouldBe(0, show.ToString());
        show.Output.ShouldContain("orders (collection)");
        show.Output.ShouldContain("customer_ci               { customer: 1 }  collation");

        var plan = await DockerRunAsync(image, workspace, feed, "plan", "--json", "--no-build");
        plan.ExitCode.ShouldBe(0, plan.ToString());
        var result = System.Text.Json.JsonDocument.Parse(plan.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]).RootElement.GetProperty("result");
        result.GetProperty("collections").EnumerateArray()
            .Select(c => (c.GetProperty("name").GetString(), c.GetProperty("state").GetString()))
            .ShouldBe([("customers", "inSync"), ("orders", "inSync")]);
    }

    [Fact]
    public async Task Bundle_image_drives_a_bundle_through_the_tool()
    {
        var image = ImageOrSkip("MIGRATE_MONGO_TEST_BUNDLE_IMAGE");
        await using var workspace = CreateWorkspace();
        var runtimeIdentifier = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
        var bundle = await workspace.RunAsync(["bundle", "-r", runtimeIdentifier, "-o", "out"], new Dictionary<string, string?> { [UrlVariable] = environment.MongoUrl });
        bundle.ExitCode.ShouldBe(0, bundle.ToString());

        var run = await DockerRunAsync(image, workspace, [], "up", "--runner", "out/BasicMigrations");

        run.ExitCode.ShouldBe(0, run.ToString());
        run.Output.ShouldContain("✔ 20260101000003");
        AppliedIds.ShouldBe(workspace.Find("_migrations").Select(s => s["_id"].AsString));
    }

    [Fact]
    public async Task Bundle_image_asks_for_a_runner_as_it_cant_build_the_project()
    {
        var image = ImageOrSkip("MIGRATE_MONGO_TEST_BUNDLE_IMAGE");
        await using var workspace = CreateWorkspace();

        var run = await DockerRunAsync(image, workspace, [], "up");

        run.ExitCode.ShouldBe(1, run.ToString());
        run.Error.ShouldContain("Couldn't run dotnet");
        run.Error.ShouldContain("pass --runner with a bundle made by `migrate-mongo bundle`");
        workspace.CollectionNames().ShouldBeEmpty();
    }

    private static string ImageOrSkip(string variable)
    {
        var image = Environment.GetEnvironmentVariable(variable);
        Assert.SkipWhen(string.IsNullOrEmpty(image), $"Set {variable} to the image to test.");
        Assert.SkipWhen(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MIGRATE_MONGO_TEST_MONGODB_URL")),
            "Needs the test MongoDB container to be reachable from another container.");
        return image!;
    }

    private Workspace CreateWorkspace(string fixture = "BasicMigrations")
    {
        var workspace = Workspace.FromFixture(environment, fixture);
        workspace.WriteConfigFile($$"""
            project: {{fixture}}
            mongodb:
              url: ${{{UrlVariable}}}
              databaseName: {{workspace.DatabaseName}}
            lockWaitTimeout: 5
            """);
        return workspace;
    }

    private async Task<ProcessResult> DockerRunAsync(string image, Workspace workspace, string[] dockerArguments, params string[] arguments)
    {
        var url = new MongoUrlBuilder(environment.MongoUrl) { Server = new MongoServerAddress("host.docker.internal", new MongoUrl(environment.MongoUrl).Server.Port) };
        List<string> run = ["run", "--rm", "--add-host", "host.docker.internal:host-gateway", "-e", $"{UrlVariable}={url}", "-e", "NO_COLOR=1", "-v", $"{workspace.Directory}:/src"];
        if (OperatingSystem.IsLinux())
        {
            // Run as the current user so the build output in the bind mount can be cleaned up.
            var id = await ProcessHelper.RunAsync("id", ["-u"], workspace.Directory, null, TimeSpan.FromSeconds(10));
            var group = await ProcessHelper.RunAsync("id", ["-g"], workspace.Directory, null, TimeSpan.FromSeconds(10));
            run.AddRange(["--user", $"{id.Output.Trim()}:{group.Output.Trim()}", "-e", "HOME=/tmp"]);
        }

        return await ProcessHelper.RunAsync("docker", [.. run, .. dockerArguments, image, .. arguments], workspace.Directory, null, TimeSpan.FromMinutes(10));
    }
}
