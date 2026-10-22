using MongoDB.Bson;
using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests;

/// <summary>Recording results, notes and tracked operations with the ResultsMigrations fixture.</summary>
public sealed class ResultsTests(IntegrationEnvironment environment)
{
    [Fact]
    public async Task Up_stores_results_notes_and_operations_on_the_state_and_history()
    {
        await using var workspace = Workspace.FromFixture(environment, "ResultsMigrations");

        var up = await workspace.RunAsync("up");

        up.ExitCode.ShouldBe(0, up.ToString());
        up.Output.ShouldContain("    archive        matched 2 · modified 2 · upserted 0");
        up.Output.ShouldContain("    voidedRemoved  1");
        up.Output.ShouldContain("2 operation(s) recorded – migrate-mongo show 20260401000002");

        var states = workspace.Find("_migrations");
        var seed = states[0];
        seed.Contains("results").ShouldBeFalse();
        seed["resultsCommitted"].AsBoolean.ShouldBeTrue();
        StripTimings(seed["operations"].AsBsonArray).ShouldBe(new BsonArray
        {
            new BsonDocument { { "kind", "insertMany" }, { "collection", "orders" }, { "counts", new BsonDocument("inserted", 4L) } },
            new BsonDocument
            {
                { "kind", "createIndex" }, { "collection", "orders" },
                { "keys", new BsonDocument("status", 1) }, { "options", new BsonDocument("name", "status_1") },
                { "counts", new BsonDocument("created", 1L) },
            },
        });

        var archive = states[1];
        archive["results"].ShouldBe(new BsonDocument
        {
            { "archive", new BsonDocument { { "matched", 2L }, { "modified", 2L }, { "upserted", 0L } } },
            { "voidedRemoved", 1L },
            { "cutoff", new BsonDateTime(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)) },
        });
        var note = archive["notes"].AsBsonArray.Single().AsBsonDocument;
        note["level"].AsString.ShouldBe("warning");
        note["message"].AsString.ShouldBe("Order 3 is still open.");
        archive["resultsCommitted"].AsBoolean.ShouldBeTrue();
        StripTimings(archive["operations"].AsBsonArray).ShouldBe(new BsonArray
        {
            new BsonDocument
            {
                { "kind", "updateMany" }, { "collection", "orders" }, { "label", "archive" },
                { "filter", new BsonDocument("status", "complete") }, { "update", new BsonDocument("$set", new BsonDocument("archived", true)) },
                { "counts", new BsonDocument { { "matched", 2L }, { "modified", 2L }, { "upserted", 0L } } },
            },
            new BsonDocument
            {
                { "kind", "deleteMany" }, { "collection", "orders" },
                { "filter", new BsonDocument("status", "void") },
                { "counts", new BsonDocument("deleted", 1L) },
            },
        });

        // The dropped index's full definition is recorded, found from its keys.
        StripTimings(states[2]["operations"].AsBsonArray).ShouldBe(new BsonArray
        {
            new BsonDocument
            {
                { "kind", "dropIndex" }, { "collection", "orders" },
                { "index", new BsonDocument { { "name", "status_1" }, { "keys", new BsonDocument("status", 1) } } },
                { "counts", new BsonDocument("dropped", 1L) },
            },
        });

        var history = workspace.Find("_migrations.history", sortBy: "at");
        history[1]["results"].ShouldBe(archive["results"]);
        history[1]["operations"].ShouldBe(archive["operations"]);
    }

    [Fact]
    public async Task Show_prints_a_migrations_results_and_operations()
    {
        await using var workspace = Workspace.FromFixture(environment, "ResultsMigrations");
        (await workspace.RunAsync("up")).ExitCode.ShouldBe(0);

        var show = await workspace.RunAsync("show", "20260401000002", "--no-build");

        show.ExitCode.ShouldBe(0, show.ToString());
        show.Output.ShouldContain("20260401000002 archive-orders");
        show.Output.ShouldContain("""
            Results
              archive        { matched: 2, modified: 2, upserted: 0 }
              voidedRemoved  1
              cutoff         2026-04-01T00:00:00Z
            """.ReplaceLineEndings());
        show.Output.ShouldContain("warning  Order 3 is still open.");
        show.Output.ShouldContain("1. updateMany orders [archive]  matched 2 · modified 2 · upserted 0");
        show.Output.ShouldContain("""       filter: { status: "complete" }""");
        show.Output.ShouldContain("2. deleteMany orders  deleted 1");

        var (process, json) = await workspace.RunJsonAsync("show", "20260401000002", "--no-build", "--no-statements");
        process.ExitCode.ShouldBe(0, process.ToString());
        var output = json.GetProperty("show").GetProperty("migration").GetProperty("output");
        output.GetProperty("results").GetProperty("voidedRemoved").GetInt64().ShouldBe(1);
        output.GetProperty("operations")[0].TryGetProperty("statement", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Down_records_its_results_on_the_rolled_back_history_entry()
    {
        await using var workspace = Workspace.FromFixture(environment, "ResultsMigrations");
        (await workspace.RunAsync("up", "--to", "20260401000002")).ExitCode.ShouldBe(0);

        var down = await workspace.RunAsync("down", "--no-build");

        down.ExitCode.ShouldBe(0, down.ToString());
        down.Output.ShouldContain("    unarchived  2");
        var rolledBack = workspace.Find("_migrations.history", sortBy: "at").Last();
        rolledBack["event"].AsString.ShouldBe("rolledBack");
        rolledBack["results"].ShouldBe(new BsonDocument("unarchived", 2L));
        rolledBack["resultsCommitted"].AsBoolean.ShouldBeTrue();
    }

    private static BsonArray StripTimings(BsonArray operations) =>
        new(operations.Select(o =>
        {
            var copy = o.AsBsonDocument.DeepClone().AsBsonDocument;
            copy["at"].ToUniversalTime().ShouldBe(DateTime.UtcNow, TimeSpan.FromMinutes(5));
            copy["durationMs"].ToInt64().ShouldBeGreaterThanOrEqualTo(0);
            copy.Remove("at");
            copy.Remove("durationMs");
            return copy;
        }));
}
