using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.Protocol;
using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests.Collections;

/// <summary>
/// Collections and views in the database that no definition declares (<c>collections.prune</c>, <c>collections.ignore</c>):
/// left alone by default, reported, or dropped when they're views or empty.
/// </summary>
public sealed class UndeclaredCollectionTests(IntegrationEnvironment environment)
{
    private static readonly CollectionDescriptor Orders = CollectionHarness.Define<BsonDocument>(c => c.Indexes.Add(i => i.Ascending("at")), "orders");

    private static async Task<CollectionHarness> SeedAsync(IntegrationEnvironment environment)
    {
        var mongo = new CollectionHarness(environment);
        await mongo.UpAllAsync([Orders]);
        await mongo.Database.CreateCollectionAsync("empty_leftover");
        await mongo.Collection("full_leftover").InsertManyAsync([new BsonDocument("n", 1), new BsonDocument("n", 2)]);
        await mongo.Database.CreateViewAsync<BsonDocument, BsonDocument>("old_view", "orders", PipelineDefinition<BsonDocument, BsonDocument>.Create(BsonDocument.Parse("""{ "$match": { "x": 1 } }""")));
        return mongo;
    }

    private static Action<EngineOptions> Prune(string prune, params string[] ignore) => o =>
    {
        o.CollectionsPrune = prune;
        o.CollectionsIgnore = [.. ignore];
    };

    [Fact]
    public async Task Undeclared_collections_are_left_alone_and_not_listed_by_default()
    {
        await using var mongo = await SeedAsync(environment);

        var plan = await mongo.RunAsync("plan", [Orders]);

        plan.Collections.ShouldNotBeNull().Select(c => c.Name).ShouldBe(["orders"]);
        (plan.Issues ?? []).ShouldBeEmpty();
    }

    [Fact]
    public async Task Report_lists_them_as_kept_with_warnings_and_drops_nothing()
    {
        await using var mongo = await SeedAsync(environment);

        var plan = await mongo.RunAsync("plan", [Orders], Prune("report"));

        var undeclared = plan.Collections.ShouldNotBeNull().Single(c => c.Kind == "undeclared");
        undeclared.Changes.Select(c => $"{c.Action} {c.Index}: {c.Detail}").ShouldBe([
            "keep empty_leftover: not declared (0 documents)",
            "keep full_leftover: not declared (2 documents)",
            "keep old_view: view on orders, not declared",
        ]);
        plan.Issues.ShouldNotBeNull().Where(i => i.Kind == "undeclaredCollection").Count().ShouldBe(3);

        var up = await mongo.RunAsync("up", [Orders], Prune("report"));
        up.ExitCode.ShouldBe(ExitCodes.Success, up.Message);
        mongo.CollectionNames().ShouldContain("empty_leftover");
        mongo.CollectionNames().ShouldContain("old_view");
    }

    [Fact]
    public async Task Drop_removes_views_and_empty_collections_and_keeps_ones_with_documents()
    {
        await using var mongo = await SeedAsync(environment);

        var plan = await mongo.RunAsync("plan", [Orders], Prune("drop"));
        plan.Collections.ShouldNotBeNull().Single(c => c.Kind == "undeclared").Changes.Select(c => $"{c.Action} {c.Index}").ShouldBe([
            "drop-collection empty_leftover",
            "keep full_leftover",
            "drop-view old_view",
        ]);

        var up = await mongo.RunAsync("up", [Orders], Prune("drop"));

        up.ExitCode.ShouldBe(ExitCodes.Success, up.Message);
        up.Issues.ShouldNotBeNull().ShouldContain(i => i.Message == "full_leftover isn't declared but isn't empty (2 documents), so it isn't dropped.");
        var names = mongo.CollectionNames();
        names.ShouldNotContain("empty_leftover");
        names.ShouldNotContain("old_view");
        names.ShouldContain("full_leftover");
        names.ShouldContain("orders");
        names.ShouldContain("_migrations");
        names.ShouldContain("_migrations.history");
        names.ShouldContain("_migrations.collections");

        var dropped = mongo.History().Where(h => h["event"] == "dropped").ToDictionary(h => h["migrationId"].AsString);
        dropped.Keys.Order(StringComparer.Ordinal).ShouldBe(["empty_leftover", "old_view"]);
        var statement = dropped["old_view"]["operations"][0].AsBsonDocument;
        statement["command"].ShouldBe(new BsonDocument("drop", "old_view"));
        statement["droppedCollection"]["options"]["viewOn"].AsString.ShouldBe("orders");
        dropped["empty_leftover"]["operations"][0]["indexes"].AsBsonArray.Select(i => i["name"].AsString).ShouldBe(["_id_"]);

        var again = await mongo.RunAsync("plan", [Orders], Prune("drop"));
        again.Collections.ShouldNotBeNull().Single(c => c.Kind == "undeclared").Changes.Select(c => $"{c.Action} {c.Index}").ShouldBe(["keep full_leftover"]);
    }

    [Fact]
    public async Task Ignored_patterns_are_left_alone()
    {
        await using var mongo = await SeedAsync(environment);

        var plan = await mongo.RunAsync("plan", [Orders], Prune("drop", "*_leftover", "old_*"));
        plan.Collections.ShouldNotBeNull().ShouldNotContain(c => c.Kind == "undeclared");

        var up = await mongo.RunAsync("up", [Orders], Prune("drop", "*_leftover", "old_*"));

        up.ExitCode.ShouldBe(ExitCodes.Success, up.Message);
        mongo.CollectionNames().ShouldContain("empty_leftover");
        mongo.CollectionNames().ShouldContain("old_view");
    }

    [Fact]
    public async Task Only_runs_when_collection_definitions_run()
    {
        await using var mongo = await SeedAsync(environment);

        var up = await mongo.RunAsync("up", [Orders], Prune("drop"), new CommandArguments { Only = "migrations" });

        up.ExitCode.ShouldBe(ExitCodes.Success, up.Message);
        mongo.CollectionNames().ShouldContain("empty_leftover");
    }
}
