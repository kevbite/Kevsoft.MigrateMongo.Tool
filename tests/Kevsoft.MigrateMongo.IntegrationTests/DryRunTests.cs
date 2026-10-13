using MongoDB.Bson;
using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests;

/// <summary><c>up --dry-run</c> and <c>down --dry-run</c> against the BasicMigrations fixture.</summary>
public sealed class DryRunTests(IntegrationEnvironment environment)
{
    [Fact]
    public async Task Up_dry_run_creates_nothing_and_up_then_applies_exactly_the_plan()
    {
        await using var workspace = Workspace.FromFixture(environment, "BasicMigrations");

        var dryRun = await workspace.RunJsonAsync("up", "--dry-run");

        dryRun.Process.ExitCode.ShouldBe(0, dryRun.Process.ToString());
        dryRun.Result.GetProperty("plan").EnumerateArray()
            .Select(p => (p.GetProperty("id").GetString(), p.GetProperty("direction").GetString(), p.GetProperty("transactional").GetBoolean()))
            .ShouldBe([
                ("20260101000001", "up", false),
                ("20260101000002", "up", false),
                ("20260101000003", "up", true),
            ]);
        dryRun.Result.GetProperty("batch").GetInt32().ShouldBe(1);
        dryRun.Result.GetProperty("message").GetString().ShouldBe("Dry run: would apply 3 migration(s) as batch 1; nothing was changed.");
        dryRun.Process.Output.ShouldNotContain("migrationStarted");
        // Not even the state collections or their indexes are created.
        workspace.CollectionNames().ShouldBeEmpty();

        var text = await workspace.RunAsync("up", "--dry-run", "--to", "20260101000002", "--no-build");
        text.ExitCode.ShouldBe(0, text.ToString());
        text.Output.ShouldContain("▶ 20260101000001  create-users   (no transaction)");
        text.Output.ShouldContain("▶ 20260101000002  users-email-index   (no transaction)");
        text.Output.ShouldNotContain("20260101000003");
        text.Output.ShouldContain("Dry run: would apply 2 migration(s) as batch 1; nothing was changed.");
        workspace.CollectionNames().ShouldBeEmpty();

        (await workspace.RunAsync("up", "--to", "20260101000002", "--no-build")).ExitCode.ShouldBe(0);

        var next = await workspace.RunJsonAsync("up", "--dry-run", "--no-build");
        next.Process.ExitCode.ShouldBe(0, next.Process.ToString());
        next.Result.GetProperty("plan").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ShouldBe(["20260101000003"]);
        next.Result.GetProperty("batch").GetInt32().ShouldBe(2);
        workspace.Find("_migrations.history").Count.ShouldBe(2);
        workspace.Find("roles").ShouldBeEmpty();

        var up = await workspace.RunJsonAsync("up", "--no-build");
        up.Process.ExitCode.ShouldBe(0, up.Process.ToString());
        up.Result.GetProperty("applied").EnumerateArray().Select(a => a.GetString()).ShouldBe(["20260101000003"]);
        up.Result.GetProperty("batch").GetInt32().ShouldBe(2);

        var nothing = await workspace.RunAsync("up", "--dry-run", "--no-build");
        nothing.ExitCode.ShouldBe(0, nothing.ToString());
        nothing.Output.ShouldContain("Dry run: nothing to apply; database is up to date.");
    }

    [Fact]
    public async Task Down_dry_run_lists_what_would_be_rolled_back_without_touching_it()
    {
        await using var workspace = Workspace.FromFixture(environment, "BasicMigrations");
        (await workspace.RunAsync("up", "--to", "20260101000001")).ExitCode.ShouldBe(0);
        (await workspace.RunAsync("up", "--no-build")).ExitCode.ShouldBe(0);
        var statesBefore = workspace.Find("_migrations");

        var batch = await workspace.RunJsonAsync("down", "--batch", "--dry-run", "--no-build");

        batch.Process.ExitCode.ShouldBe(0, batch.Process.ToString());
        batch.Result.GetProperty("plan").EnumerateArray()
            .Select(p => (p.GetProperty("id").GetString(), p.GetProperty("direction").GetString(), p.GetProperty("transactional").GetBoolean()))
            .ShouldBe([
                ("20260101000003", "down", true),
                ("20260101000002", "down", false),
            ]);
        batch.Result.GetProperty("message").GetString().ShouldBe("Dry run: would roll back 2 migration(s); nothing was changed.");

        var text = await workspace.RunAsync("down", "--dry-run", "--no-build");
        text.ExitCode.ShouldBe(0, text.ToString());
        text.Output.ShouldContain("◀ 20260101000003  seed-defaults   (transaction)");
        text.Output.ShouldContain("Dry run: would roll back 1 migration(s); nothing was changed.");

        workspace.Find("_migrations").ShouldBe(statesBefore);
        workspace.Find("_migrations.history").Count.ShouldBe(3);
        workspace.Find("_migrations.lock").ShouldBeEmpty();
        workspace.Find("roles").Count.ShouldBe(2);
        workspace.Find("users").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Dry_run_warns_about_a_held_lock_and_leaves_it_in_place()
    {
        await using var workspace = Workspace.FromFixture(environment, "BasicMigrations");
        var expiresAt = DateTime.UtcNow.AddMinutes(10);
        var held = new BsonDocument
        {
            { "_id", "lock" },
            { "runId", "deploy-from-another-agent" },
            { "host", "ci-agent-7" },
            { "acquiredAt", DateTime.UtcNow },
            { "expiresAt", expiresAt },
            { "heartbeatAt", DateTime.UtcNow },
        };
        await workspace.Database.GetCollection<BsonDocument>("_migrations.lock").InsertOneAsync(held);

        var dryRun = await workspace.RunAsync("up", "--dry-run");

        dryRun.ExitCode.ShouldBe(0, dryRun.ToString());
        dryRun.Output.ShouldContain("warning: Migrations are locked by run deploy-from-another-agent (ci-agent-7) until ");
        dryRun.Output.ShouldContain("a real run would wait up to 5s for it.");
        dryRun.Output.ShouldContain("Dry run: would apply 3 migration(s) as batch 1; nothing was changed.");
        workspace.Find("_migrations.lock").Single()["runId"].AsString.ShouldBe("deploy-from-another-agent");
        workspace.CollectionNames().ShouldBe(["_migrations.lock"]);
    }

    [Fact]
    public async Task An_old_runner_is_refused_a_dry_run_instead_of_applying_migrations()
    {
        await using var workspace = Workspace.FromFixture(environment, "BasicMigrations");
        // Kevsoft.MigrateMongo.Runner 0.1.1 (from nuget.org) predates --dry-run and would ignore it.
        var project = workspace.PathOf("BasicMigrations", "BasicMigrations.csproj");
        await File.WriteAllTextAsync(project, (await File.ReadAllTextAsync(project)).Replace("$(MigrateMongoVersion)", "0.1.1"));

        var dryRun = await workspace.RunAsync("up", "--dry-run");

        dryRun.ExitCode.ShouldBe(5, dryRun.ToString());
        dryRun.Output.ShouldContain("Runner: Kevsoft.MigrateMongo 0.1.1");
        dryRun.Error.ShouldContain(
            "--dry-run isn't supported by the runner (Kevsoft.MigrateMongo 0.1.1); nothing was run. " +
            "Update the Kevsoft.MigrateMongo.Runner package in your migrations project to 0.2.0 or later.");
        workspace.CollectionNames().ShouldBeEmpty();

        // Everything else still works with the old runner.
        var plan = await workspace.RunAsync("plan", "--no-build");
        plan.ExitCode.ShouldBe(2, plan.ToString());
        workspace.Find("_migrations").ShouldBeEmpty();
    }
}
