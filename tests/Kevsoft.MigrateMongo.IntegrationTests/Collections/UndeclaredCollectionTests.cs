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

    [Fact]
    public async Task Drop_keeps_an_empty_collection_a_declared_view_reads_from_through_a_lookup()
    {
        await using var mongo = new CollectionHarness(environment);
        var customers = CollectionHarness.Define<BsonDocument>(c => c.Indexes.Add(i => i.Ascending("status")), "customers");
        var joined = CollectionHarness.DefineView("customer_orders", "customers", v => v.Pipeline(
            """
            [{ "$lookup": { "from": "lookup_orders", "localField": "_id", "foreignField": "customerId", "as": "orders" } }]
            """));
        await mongo.UpAllAsync([customers, joined]);
        // An undeclared, empty collection the declared view reads from through $lookup: an empty collection would be dropped,
        // but a managed view depends on it, so it must be kept.
        await mongo.Database.CreateCollectionAsync("lookup_orders");

        var plan = await mongo.RunAsync("plan", [customers, joined], Prune("drop"));
        plan.Collections.ShouldNotBeNull().ShouldNotContain(c => c.Kind == "undeclared");

        var up = await mongo.RunAsync("up", [customers, joined], Prune("drop"));

        up.ExitCode.ShouldBe(ExitCodes.Success, up.Message);
        mongo.CollectionNames().ShouldContain("lookup_orders");
    }

    [Fact]
    public async Task Drop_keeps_an_undeclared_view_and_its_source_that_a_declared_view_depends_on()
    {
        await using var mongo = new CollectionHarness(environment);
        // base_things (undeclared, empty) <- mid_view (undeclared view) <- top_view (declared view).
        await mongo.Database.CreateCollectionAsync("base_things");
        await mongo.Database.CreateViewAsync<BsonDocument, BsonDocument>("mid_view", "base_things",
            PipelineDefinition<BsonDocument, BsonDocument>.Create(BsonDocument.Parse("""{ "$match": { "n": 1 } }""")));
        var top = CollectionHarness.DefineView("top_view", "mid_view", v => v.Pipeline("""[{ "$limit": 5 }]"""));

        var up = await mongo.UpAllAsync([top], Prune("drop"));

        up.ExitCode.ShouldBe(ExitCodes.Success, up.Message);
        var names = mongo.CollectionNames();
        names.ShouldContain("top_view");
        names.ShouldContain("mid_view");
        names.ShouldContain("base_things");
    }

    [Fact]
    public async Task Drop_cancelled_part_way_through_still_records_the_drops_that_already_happened()
    {
        await using var mongo = await SeedAsync(environment);
        // Two empty, undeclared collections so there are at least two drops; the cancel fires after the first one.
        await mongo.Database.CreateCollectionAsync("aaa_leftover");
        await mongo.Database.CreateCollectionAsync("bbb_leftover");
        await mongo.Collection("full_leftover").DeleteManyAsync(FilterDefinition<BsonDocument>.Empty);
        using var cancel = new CancellationTokenSource();
        using var client = mongo.CancellingAfter("drop", cancel);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            mongo.RunAsync("up", [Orders], Prune("drop"), client: client, cancellationToken: cancel.Token));

        // The first drop happened and its history was persisted durably, even though the run was cancelled before finishing.
        var dropped = mongo.History().Where(h => h["event"] == "dropped").Select(h => h["migrationId"].AsString).ToList();
        dropped.ShouldContain("aaa_leftover");
        mongo.CollectionNames().ShouldNotContain("aaa_leftover");
    }

    [Fact]
    public async Task Planning_shares_one_listCollections_read_across_all_the_definitions_and_pruning()
    {
        await using var mongo = new CollectionHarness(environment);
        var one = CollectionHarness.Define<BsonDocument>(c => c.Indexes.Add(i => i.Ascending("a")), "one");
        var two = CollectionHarness.Define<BsonDocument>(c => c.Indexes.Add(i => i.Ascending("a")), "two");
        var three = CollectionHarness.Define<BsonDocument>(c => c.Indexes.Add(i => i.Ascending("a")), "three");

        var withOne = new Dictionary<string, int>();
        using (var client = mongo.Counting(withOne))
        {
            await mongo.RunAsync("plan", [one], Prune("report"), client: client);
        }

        var withThree = new Dictionary<string, int>();
        using (var client = mongo.Counting(withThree))
        {
            await mongo.RunAsync("plan", [one, two, three], Prune("report"), client: client);
        }

        // Planning three definitions reads the namespace listing no more times than planning one does: the pass shares a
        // single listCollections across every definition and the pruning scan rather than listing once per definition.
        withThree.GetValueOrDefault("listCollections").ShouldBe(withOne.GetValueOrDefault("listCollections"));
        withThree.GetValueOrDefault("listCollections").ShouldBeLessThan(3);
    }
}
