using MongoDB.Bson;
using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests;

/// <summary>
/// The HostingApp fixture is a generic host app that references the BasicMigrations project and applies it on startup
/// with Kevsoft.MigrateMongo.Hosting.
/// </summary>
public sealed class HostingTests(IntegrationEnvironment environment)
{
    private static readonly string[] MigrationIds = ["20260101000001", "20260101000002", "20260101000003"];

    [Fact]
    public async Task App_applies_migrations_before_it_starts_and_shares_state_with_the_cli()
    {
        await using var workspace = await AppWorkspaceAsync();

        var run = await RunAppAsync(workspace);

        run.ExitCode.ShouldBe(0, run.ToString());
        run.Output.ShouldContain("Applying migration 20260101000001 create-users");
        run.Output.ShouldContain("Applied migration 20260101000003 seed-defaults");
        run.Output.ShouldContain("Applied 3 migration(s) in batch 1.");
        run.Output.ShouldContain("App started; applied migrations: 3");

        var states = workspace.Find("_migrations");
        states.Select(s => (s["_id"].AsString, s["state"].AsString, s["batch"].AsInt32)).ShouldBe([
            ("20260101000001", "applied", 1),
            ("20260101000002", "applied", 1),
            ("20260101000003", "applied", 1),
        ]);
        foreach (var state in states)
        {
            var appliedBy = state["appliedBy"].AsBsonDocument;
            appliedBy["tool"].AsString.ShouldBe("app/HostingApp");
            appliedBy["library"].AsString.ShouldBe(environment.PackageVersion);
            appliedBy["driver"].AsString.ShouldBe(environment.DriverVersion);
            appliedBy["runtime"].AsString.ShouldStartWith("10.0.");
        }

        // SeedDefaults took SeedOptions from the app's service collection.
        workspace.Find("users").ShouldBe([
            new BsonDocument { { "_id", "admin" }, { "email", "app-admin@example.com" }, { "roles", new BsonArray { "admin" } } },
        ]);
        workspace.Find("_migrations.lock").ShouldBeEmpty();

        // The CLI sees the app's work: nothing pending, nothing modified.
        var plan = await workspace.RunJsonAsync("plan");
        plan.Process.ExitCode.ShouldBe(0, plan.Process.ToString());
        var validate = await workspace.RunJsonAsync("validate", "--no-build");
        validate.Process.ExitCode.ShouldBe(0, validate.Process.ToString());

        // Restarting the app doesn't apply anything again.
        var restart = await RunAppAsync(workspace);
        restart.ExitCode.ShouldBe(0, restart.ToString());
        restart.Output.ShouldNotContain("Applying migration");
        restart.Output.ShouldContain("Nothing to apply; database is up to date.");
        restart.Output.ShouldContain("App started; applied migrations: 3");
        workspace.Find("_migrations.history", sortBy: "at").Select(h => (h["migrationId"].AsString, h["event"].AsString))
            .ShouldBe(MigrationIds.Select(id => (id, "applied")));
    }

    [Fact]
    public async Task Instances_starting_together_apply_each_migration_once()
    {
        await using var workspace = await AppWorkspaceAsync();

        var runs = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => RunAppAsync(workspace)));

        foreach (var run in runs)
        {
            run.ExitCode.ShouldBe(0, run.ToString());
            run.Output.ShouldContain("App started; applied migrations: 3");
        }

        runs.Count(r => r.Output.Contains("Applying migration 20260101000001 create-users")).ShouldBe(1);
        workspace.Find("_migrations").Select(s => (s["_id"].AsString, s["batch"].AsInt32))
            .ShouldBe(MigrationIds.Select(id => (id, 1)));
        workspace.Find("_migrations.history", sortBy: "at").Select(h => (h["migrationId"].AsString, h["event"].AsString))
            .ShouldBe(MigrationIds.Select(id => (id, "applied")));
        workspace.Find("_migrations.lock").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failing_migration_stops_the_app_from_starting()
    {
        await using var workspace = await AppWorkspaceAsync();
        // SeedDefaults inserts the "admin" role, so an existing one makes it fail with a duplicate key error.
        await workspace.Database.GetCollection<BsonDocument>("roles").InsertOneAsync(new BsonDocument("_id", "admin"));

        var run = await RunAppAsync(workspace);

        run.ExitCode.ShouldBe(1, run.ToString());
        run.Output.ShouldNotContain("App started");
        run.Error.ShouldContain("Migrations failed: ");
        run.Output.ShouldContain("Migration 20260101000003 seed-defaults failed: ");
        run.Output.ShouldContain("E11000");

        workspace.Find("_migrations").Select(s => (s["_id"].AsString, s["state"].AsString)).ShouldBe([
            ("20260101000001", "applied"),
            ("20260101000002", "applied"),
        ]);
        workspace.Find("_migrations.history", sortBy: "at").Select(h => (h["migrationId"].AsString, h["event"].AsString)).ShouldBe([
            ("20260101000001", "applied"),
            ("20260101000002", "applied"),
            ("20260101000003", "failed"),
        ]);
        workspace.Find("users").ShouldBeEmpty();
        workspace.Find("roles").ShouldBe([new BsonDocument("_id", "admin")]);
        workspace.Find("_migrations.lock").ShouldBeEmpty();
    }

    [Fact]
    public async Task App_can_dry_run_without_changing_anything()
    {
        await using var workspace = await AppWorkspaceAsync();

        var run = await RunAppAsync(workspace, dryRun: true);

        run.ExitCode.ShouldBe(0, run.ToString());
        run.Output.ShouldContain("Would apply 20260101000001 create-users");
        run.Output.ShouldContain("Would apply 20260101000003 seed-defaults");
        run.Output.ShouldContain("Dry run: would apply 3 migration(s) as batch 1; nothing was changed.");
        run.Output.ShouldNotContain("Applying migration");
        workspace.CollectionNames().ShouldBeEmpty();
    }

    /// <summary>A workspace with BasicMigrations (and its migrate-mongo.yaml) plus HostingApp, built once into <c>app/</c>.</summary>
    private async Task<Workspace> AppWorkspaceAsync()
    {
        var workspace = Workspace.FromFixture(environment, "BasicMigrations", lockWaitTimeout: 60);
        workspace.AddFixture("HostingApp");
        await Dotnet.RunAsync(workspace.Directory, environment.ProcessEnvironment,
            "build", Path.Combine("HostingApp", "HostingApp.csproj"), "-o", workspace.PathOf("app"), "-nologo");
        return workspace;
    }

    private Task<ProcessResult> RunAppAsync(Workspace workspace, bool dryRun = false)
    {
        var variables = new Dictionary<string, string?>(environment.ProcessEnvironment)
        {
            ["MONGODB_URL"] = environment.MongoUrl,
            ["DATABASE_NAME"] = workspace.DatabaseName,
            ["DRY_RUN"] = dryRun ? "1" : null,
            ["Logging__Console__FormatterName"] = "simple",
            ["Logging__Console__FormatterOptions__SingleLine"] = "true",
        };
        return ProcessHelper.RunAsync("dotnet", [workspace.PathOf("app", "HostingApp.dll")], workspace.Directory, variables, TimeSpan.FromMinutes(3));
    }
}
