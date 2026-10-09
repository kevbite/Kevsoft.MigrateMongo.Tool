using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests;

public sealed class ManagementTests(IntegrationEnvironment environment)
{
    [Fact]
    public async Task Lock_held_by_another_run_times_out_and_can_be_forced_open()
    {
        await using var workspace = Workspace.FromFixture(environment, "BasicMigrations", lockWaitTimeout: 1);
        await workspace.Database.GetCollection<BsonDocument>("_migrations.lock").InsertOneAsync(new BsonDocument
        {
            { "_id", "lock" },
            { "runId", "someone-else" },
            { "host", "elsewhere" },
            { "acquiredAt", DateTime.UtcNow },
            { "expiresAt", DateTime.UtcNow.AddMinutes(10) },
            { "heartbeatAt", DateTime.UtcNow },
        });

        var up = await workspace.RunAsync("up");
        up.ExitCode.ShouldBe(3, up.ToString());
        workspace.Find("_migrations").ShouldBeEmpty();

        (await workspace.RunAsync("unlock", "--no-build")).ExitCode.ShouldBe(1);
        workspace.Find("_migrations.lock").Single()["runId"].AsString.ShouldBe("someone-else");

        (await workspace.RunAsync("unlock", "--force", "--no-build")).ExitCode.ShouldBe(0);
        workspace.Find("_migrations.lock").ShouldBeEmpty();

        (await workspace.RunAsync("up", "--no-build")).ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task Baseline_records_existing_migrations_without_running_them()
    {
        await using var workspace = Workspace.FromFixture(environment, "BasicMigrations");

        var baseline = await workspace.RunAsync("baseline", "--to", "20260101000002");

        baseline.ExitCode.ShouldBe(0, baseline.ToString());
        workspace.Find("_migrations").Select(s => (s["_id"].AsString, s["state"].AsString, s["batch"].AsInt32))
            .ShouldBe([("20260101000001", "applied", 1), ("20260101000002", "applied", 1)]);
        workspace.Find("_migrations.history", sortBy: "at").Select(h => h["event"].AsString).ShouldBe(["baselined", "baselined"]);
        workspace.CollectionNames().ShouldNotContain("users");

        (await workspace.RunAsync("up", "--no-build")).ExitCode.ShouldBe(0);
        workspace.Find("_migrations").Select(s => (s["_id"].AsString, s["batch"].AsInt32))
            .ShouldBe([("20260101000001", 1), ("20260101000002", 1), ("20260101000003", 2)]);
    }

    [Fact]
    public async Task Mark_unapplied_makes_a_migration_pending_again()
    {
        await using var workspace = Workspace.FromFixture(environment, "BasicMigrations");
        (await workspace.RunAsync("up")).ExitCode.ShouldBe(0);

        var mark = await workspace.RunAsync("mark", "20260101000003", "--unapplied", "--no-build");

        mark.ExitCode.ShouldBe(0, mark.ToString());
        workspace.Find("_migrations").Select(s => s["_id"].AsString).ShouldBe(["20260101000001", "20260101000002"]);
        workspace.Find("_migrations.history", sortBy: "at").Last()["event"].AsString.ShouldBe("markedUnapplied");
        workspace.Find("users").Count.ShouldBe(1, "marking doesn't run the migration's down");
    }

    [Fact]
    public async Task Validate_detects_code_changes_but_ignores_formatting()
    {
        await using var workspace = Workspace.FromFixture(environment, "BasicMigrations");
        (await workspace.RunAsync("up")).ExitCode.ShouldBe(0);
        var file = workspace.PathOf("BasicMigrations", "Migrations", "M20260101000002_UsersEmailIndex.cs");
        var original = File.ReadAllText(file);

        File.WriteAllText(file, "// A comment that doesn't change anything.\n" + original.Replace("    ", "\t"));
        var reformatted = await workspace.RunAsync("validate");
        reformatted.ExitCode.ShouldBe(0, reformatted.ToString());

        File.WriteAllText(file, original.Replace("Unique = true", "Unique = false"));
        var changed = await workspace.RunJsonAsync("validate");
        changed.Process.ExitCode.ShouldBe(4, changed.Process.ToString());
        changed.Result.GetProperty("issues").EnumerateArray()
            .Select(i => (i.GetProperty("kind").GetString(), i.GetProperty("migrationId").GetString(), i.GetProperty("severity").GetString()))
            .ShouldBe([("checksumMismatch", "20260101000002", "error")]);

        (await workspace.RunAsync("up", "--no-build")).ExitCode.ShouldBe(4);
    }
}
