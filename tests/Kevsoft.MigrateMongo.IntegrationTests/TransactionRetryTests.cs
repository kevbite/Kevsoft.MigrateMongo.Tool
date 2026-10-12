using MongoDB.Bson;
using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests;

public sealed class TransactionRetryTests(IntegrationEnvironment environment)
{
    [Fact]
    public async Task Transient_transaction_error_is_retried_and_the_migration_is_applied_once()
    {
        await using var workspace = Workspace.FromFixture(environment, "RetryMigrations");

        var up = await workspace.RunAsync("up");

        up.ExitCode.ShouldBe(0, up.ToString());
        up.Output.ShouldContain("transient transaction error (attempt 1)");
        workspace.Find("attempts", sortBy: "attempt").Select(a => a["attempt"].AsInt32).ShouldBe([1, 2]);
        workspace.Find("things").ShouldBe([new BsonDocument("_id", "flaky")]);
        workspace.Find("_migrations").Select(s => (s["_id"].AsString, s["state"].AsString, s["transactional"].AsBoolean))
            .ShouldBe([("20260301000001", "applied", true)]);
        workspace.Find("_migrations.history").Select(h => h["event"].AsString).ShouldBe(["applied"]);
    }
}
