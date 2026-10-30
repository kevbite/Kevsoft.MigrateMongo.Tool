using MongoDB.Bson;
using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests;

/// <summary>The FailingMigrations fixture: its second migration writes in its transaction and then throws.</summary>
public sealed class FailureTests(IntegrationEnvironment environment)
{
    [Fact]
    public async Task Failed_transactional_migration_is_rolled_back_and_stops_the_run()
    {
        await using var workspace = Workspace.FromFixture(environment, "FailingMigrations");

        var up = await workspace.RunAsync("up");

        up.ExitCode.ShouldBe(1, up.ToString());
        up.Output.ShouldContain("Boom after writing.");
        // The output has the exception's stack trace, so you can see where the migration failed.
        up.Output.ShouldContain("ExplodesAfterWrite.UpAsync");

        workspace.Find("_migrations").Select(s => (s["_id"].AsString, s["state"].AsString, s["appliedBy"]["runtime"].AsString[..3]))
            .ShouldBe([("20260201000001", "applied", "10.")]);
        var history = workspace.Find("_migrations.history", sortBy: "at");
        history.Select(h => (h["migrationId"].AsString, h["event"].AsString)).ShouldBe([
            ("20260201000001", "applied"),
            ("20260201000002", "failed"),
        ]);
        history[1]["error"].AsString.ShouldBe("InvalidOperationException: Boom after writing.");
        workspace.Find("things").ShouldBe([new BsonDocument("_id", "first")]);
        workspace.Find("_migrations.lock").ShouldBeEmpty();

        var status = await workspace.RunJsonAsync("status", "--no-build");
        status.Result.GetProperty("migrations").EnumerateArray().Select(m => (m.GetProperty("id").GetString(), m.GetProperty("state").GetString())).ShouldBe([
            ("20260201000001", "applied"),
            ("20260201000002", "pending"),
            ("20260201000003", "pending"),
        ]);
    }

    [Fact]
    public async Task Build_errors_exit_with_code_6()
    {
        await using var workspace = Workspace.FromFixture(environment, "FailingMigrations");
        File.WriteAllText(workspace.PathOf("FailingMigrations", "Broken.cs"), "this is not C#");

        var up = await workspace.RunAsync("up");

        up.ExitCode.ShouldBe(6, up.ToString());
        workspace.CollectionNames().ShouldBeEmpty();
    }

    [Fact]
    public async Task Missing_config_exits_with_code_1()
    {
        await using var workspace = Workspace.Empty(environment);

        var status = await workspace.RunAsync("status");

        status.ExitCode.ShouldBe(1, status.ToString());
        status.Error.ShouldContain("migrate-mongo.yaml");
    }
}
