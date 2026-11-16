using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.Protocol;
using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests.Collections;

/// <summary>
/// Atlas Search / Vector Search index management against a real Atlas Local server: create, online update, type change,
/// prune and ignore. The container runs <c>mongot</c>, so declared search indexes are really built.
/// </summary>
public sealed class SearchIndexTests(IntegrationEnvironment environment)
{
    private const string Dynamic = """{ "mappings": { "dynamic": true } }""";
    private const string Static = """{ "mappings": { "dynamic": false, "fields": { "title": { "type": "string" } } } }""";
    private const string Vector = """{ "fields": [ { "type": "vector", "path": "embedding", "numDimensions": 3, "similarity": "cosine" } ] }""";

    [Fact]
    public async Task A_declared_search_index_is_created_and_becomes_queryable()
    {
        await using var mongo = new CollectionHarness(environment);
        await SeedAsync(mongo);

        var collection = CollectionHarness.Define<BsonDocument>(c => c.SearchIndexes.Add("by_text", Dynamic));
        CollectionHarness.Changes(await mongo.PlanAsync(collection)).ShouldBe(["create-search by_text"]);

        var up = await mongo.UpAsync(collection, WaitForReady);
        up.ExitCode.ShouldBe(ExitCodes.Success, up.Message);
        up.Reconciled.ShouldBe([CollectionHarness.Things]);

        var index = (await SearchIndexesAsync(mongo)).ShouldHaveSingleItem();
        index["name"].AsString.ShouldBe("by_text");
        index["queryable"].AsBoolean.ShouldBeTrue();
        mongo.Operations().ShouldAllBe(kind => kind == "runCommand");

        await mongo.ShouldBeInSyncAsync(collection, WaitForReady);
    }

    [Fact]
    public async Task A_changed_definition_is_updated_online_without_dropping_the_index()
    {
        await using var mongo = new CollectionHarness(environment);
        await SeedAsync(mongo);
        await mongo.UpAsync(CollectionHarness.Define<BsonDocument>(c => c.SearchIndexes.Add("by_text", Dynamic)), WaitForReady);
        var createdId = (await SearchIndexesAsync(mongo)).Single()["id"].AsString;

        var changed = CollectionHarness.Define<BsonDocument>(c => c.SearchIndexes.Add("by_text", Static));
        CollectionHarness.Changes(await mongo.PlanAsync(changed)).ShouldBe(["update-search by_text"]);

        (await mongo.UpAsync(changed, WaitForReady)).Reconciled.ShouldBe([CollectionHarness.Things]);
        var server = (await SearchIndexesAsync(mongo)).ShouldHaveSingleItem();
        server["id"].AsString.ShouldBe(createdId, "updating keeps the same index, it isn't recreated");
        server["latestDefinition"]["mappings"]["dynamic"].AsBoolean.ShouldBeFalse();
        await mongo.ShouldBeInSyncAsync(changed, WaitForReady);
    }

    [Fact]
    public async Task Changing_the_type_drops_and_recreates_the_index()
    {
        await using var mongo = new CollectionHarness(environment);
        await SeedAsync(mongo);
        await mongo.UpAsync(CollectionHarness.Define<BsonDocument>(c => c.SearchIndexes.Add("idx", Dynamic)), WaitForReady);

        var asVector = CollectionHarness.Define<BsonDocument>(c => c.SearchIndexes.AddVector("idx", Vector));
        CollectionHarness.Changes(await mongo.PlanAsync(asVector)).ShouldBe(["update-search idx"]);

        (await mongo.UpAsync(asVector, WaitForReady)).Reconciled.ShouldBe([CollectionHarness.Things]);
        var server = (await SearchIndexesAsync(mongo)).ShouldHaveSingleItem();
        server["name"].AsString.ShouldBe("idx");
        server["type"].AsString.ShouldBe("vectorSearch");
        await mongo.ShouldBeInSyncAsync(asVector, WaitForReady);
    }

    [Fact]
    public async Task An_undeclared_search_index_is_kept_by_default_and_dropped_when_prune_is_drop()
    {
        await using var mongo = new CollectionHarness(environment);
        await SeedAsync(mongo);
        await mongo.UpAsync(CollectionHarness.Define<BsonDocument>(c =>
        {
            c.SearchIndexes.Add("keep_me", Dynamic);
            c.SearchIndexes.Add("remove_me", Dynamic);
        }), WaitForReady);

        // Only keep_me is declared now; remove_me is undeclared.
        var declared = CollectionHarness.Define<BsonDocument>(c => c.SearchIndexes.Add("keep_me", Dynamic));

        var report = await mongo.PlanAsync(declared);
        report.Collections.ShouldNotBeNull().ShouldHaveSingleItem().Changes.Single(ch => ch.Index == "remove_me").Action.ShouldBe("keep");
        (await SearchIndexesAsync(mongo)).Select(i => i["name"].AsString).ShouldBe(["keep_me", "remove_me"], ignoreOrder: true);

        CollectionHarness.Changes(await mongo.PlanAsync(declared, DropUndeclared)).ShouldBe(["drop-search remove_me"]);
        (await mongo.UpAsync(declared, DropUndeclared)).Reconciled.ShouldBe([CollectionHarness.Things]);
        (await SearchIndexesAsync(mongo)).ShouldHaveSingleItem()["name"].AsString.ShouldBe("keep_me");
    }

    [Fact]
    public async Task An_ignored_search_index_is_never_pruned()
    {
        await using var mongo = new CollectionHarness(environment);
        await SeedAsync(mongo);
        await mongo.UpAsync(CollectionHarness.Define<BsonDocument>(c =>
        {
            c.SearchIndexes.Add("declared", Dynamic);
            c.SearchIndexes.Add("adhoc_report", Dynamic);
        }), WaitForReady);

        var declared = CollectionHarness.Define<BsonDocument>(c =>
        {
            c.SearchIndexes.Add("declared", Dynamic);
            c.SearchIndexes.Ignore("adhoc_*");
        });

        var plan = await mongo.PlanAsync(declared, DropUndeclared);
        CollectionHarness.Changes(plan).ShouldBeEmpty();

        var show = await mongo.RunAsync("show", [declared], DropUndeclared, new CommandArguments { Id = CollectionHarness.Things });
        show.ShowCollection!.Indexes!.Single(i => i.Name == "adhoc_report").Status.ShouldBe("ignored");

        (await mongo.UpAsync(declared, DropUndeclared)).Reconciled.ShouldBeEmpty();
        (await SearchIndexesAsync(mongo)).Select(i => i["name"].AsString).ShouldBe(["declared", "adhoc_report"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_collection_that_declares_no_search_indexes_leaves_existing_ones_alone()
    {
        await using var mongo = new CollectionHarness(environment);
        await SeedAsync(mongo);
        await mongo.UpAsync(CollectionHarness.Define<BsonDocument>(c => c.SearchIndexes.Add("left_alone", Dynamic)), WaitForReady);

        // A definition with no search indexes and a regular index: search indexes are never listed or pruned.
        var regularOnly = CollectionHarness.Define<BsonDocument>(c => c.Indexes.Add(new BsonDocument("status", 1)));
        CollectionHarness.Changes(await mongo.PlanAsync(regularOnly, DropUndeclared)).ShouldBe(["create status_1"]);

        (await mongo.UpAsync(regularOnly, DropUndeclared)).Reconciled.ShouldBe([CollectionHarness.Things]);
        (await SearchIndexesAsync(mongo)).ShouldHaveSingleItem()["name"].AsString.ShouldBe("left_alone");
    }

    [Fact]
    public async Task A_declared_vector_search_index_is_created()
    {
        await using var mongo = new CollectionHarness(environment);
        await SeedAsync(mongo);

        var collection = CollectionHarness.Define<BsonDocument>(c => c.SearchIndexes.AddVector("vectors", Vector));
        CollectionHarness.Changes(await mongo.PlanAsync(collection)).ShouldBe(["create-search vectors"]);

        (await mongo.UpAsync(collection, WaitForReady)).Reconciled.ShouldBe([CollectionHarness.Things]);
        var server = (await SearchIndexesAsync(mongo)).ShouldHaveSingleItem();
        server["type"].AsString.ShouldBe("vectorSearch");
        server["queryable"].AsBoolean.ShouldBeTrue();
        await mongo.ShouldBeInSyncAsync(collection, WaitForReady);
    }

    [Fact]
    public async Task A_search_only_definition_on_a_fresh_database_creates_the_collection_then_the_index()
    {
        await using var mongo = new CollectionHarness(environment);

        // No SeedAsync: the collection doesn't exist yet, so it's created explicitly before createSearchIndexes, which would
        // otherwise fail with NamespaceNotFound.
        var collection = CollectionHarness.Define<BsonDocument>(c => c.SearchIndexes.Add("by_text", Dynamic));
        CollectionHarness.Changes(await mongo.PlanAsync(collection)).ShouldBe(["create-collection collection", "create-search by_text"]);

        var up = await mongo.UpAsync(collection, WaitForReady);
        up.ExitCode.ShouldBe(ExitCodes.Success, up.Message);
        up.Reconciled.ShouldBe([CollectionHarness.Things]);

        var index = (await SearchIndexesAsync(mongo)).ShouldHaveSingleItem();
        index["name"].AsString.ShouldBe("by_text");
        index["queryable"].AsBoolean.ShouldBeTrue();
        await mongo.ShouldBeInSyncAsync(collection, WaitForReady);
    }

    [Fact]
    public async Task Dropping_a_search_index_records_its_full_definition_in_history()
    {
        await using var mongo = new CollectionHarness(environment);
        await SeedAsync(mongo);
        await mongo.UpAsync(CollectionHarness.Define<BsonDocument>(c =>
        {
            c.SearchIndexes.Add("keep_me", Dynamic);
            c.SearchIndexes.Add("remove_me", Static);
        }), WaitForReady);

        // Only keep_me is declared now; remove_me is pruned. Its definition must be recorded, not just its name, so history
        // shows exactly what was removed.
        var declared = CollectionHarness.Define<BsonDocument>(c => c.SearchIndexes.Add("keep_me", Dynamic));
        (await mongo.UpAsync(declared, DropUndeclared)).Reconciled.ShouldBe([CollectionHarness.Things]);

        var dropped = mongo.State()["operations"].AsBsonArray
            .Select(o => o.AsBsonDocument)
            .Single(o => o.Contains("droppedIndex"))["droppedIndex"].AsBsonDocument;
        dropped["name"].AsString.ShouldBe("remove_me");
        dropped["type"].AsString.ShouldBe("search");
        dropped["definition"]["mappings"]["fields"]["title"]["type"].AsString.ShouldBe("string");
    }

    [Fact]
    public async Task Removing_a_field_from_a_static_mapping_is_an_update_and_settles_in_sync()
    {
        await using var mongo = new CollectionHarness(environment);
        await SeedAsync(mongo);
        const string twoFields = """{ "mappings": { "dynamic": false, "fields": { "title": { "type": "string" }, "body": { "type": "string" } } } }""";
        await mongo.UpAsync(CollectionHarness.Define<BsonDocument>(c => c.SearchIndexes.Add("by_text", twoFields)), WaitForReady);

        // Drop the body field. Comparing only declared keys against the server would miss this; the server still has body.
        var fewer = CollectionHarness.Define<BsonDocument>(c => c.SearchIndexes.Add("by_text", Static));
        CollectionHarness.Changes(await mongo.PlanAsync(fewer)).ShouldBe(["update-search by_text"]);

        (await mongo.UpAsync(fewer, WaitForReady)).Reconciled.ShouldBe([CollectionHarness.Things]);
        var server = (await SearchIndexesAsync(mongo)).ShouldHaveSingleItem();
        server["latestDefinition"]["mappings"]["fields"].AsBsonDocument.Names.ShouldBe(["title"]);

        // And once the removed field is gone, the server's own defaults on what's left aren't seen as a further change.
        await mongo.ShouldBeInSyncAsync(fewer, WaitForReady);
    }

    [Fact]
    public async Task The_options_the_server_fills_in_for_each_field_type_settle_in_sync()
    {
        await using var mongo = new CollectionHarness(environment);
        await SeedAsync(mongo);
        const string everyType = """
            { "mappings": { "fields": {
                "title": [ { "type": "string" }, { "type": "autocomplete" }, { "type": "token" } ],
                "price": { "type": "number" },
                "location": { "type": "geo" },
                "meta": { "type": "document" },
                "tags": { "type": "embeddedDocuments", "fields": { "name": { "type": "string" } } } } },
              "storedSource": false }
            """;
        var collection = CollectionHarness.Define<BsonDocument>(c => c.SearchIndexes.Add("by_text", everyType));

        (await mongo.UpAsync(collection, WaitForReady)).Reconciled.ShouldBe([CollectionHarness.Things]);

        await mongo.ShouldBeInSyncAsync(collection, WaitForReady);
    }

    [Fact]
    public async Task A_run_that_waits_waits_for_an_index_an_earlier_run_left_building()
    {
        await using var mongo = new CollectionHarness(environment);
        await SeedAsync(mongo);
        var collection = CollectionHarness.Define<BsonDocument>(c => c.SearchIndexes.Add("by_text", Dynamic));

        // E.g. an earlier run that didn't wait, or that timed out waiting: the index is declared and in line, but building.
        (await mongo.UpAsync(collection)).Reconciled.ShouldBe([CollectionHarness.Things]);

        var up = await mongo.UpAsync(collection, WaitForReady);

        up.ExitCode.ShouldBe(ExitCodes.Success, up.Message);
        up.Reconciled.ShouldBeEmpty();
        (await SearchIndexesAsync(mongo)).ShouldHaveSingleItem()["queryable"].AsBoolean.ShouldBeTrue();
    }

    private static void WaitForReady(EngineOptions options)
    {
        options.SearchIndexesWaitForReady = true;
        options.SearchIndexesWaitTimeout = TimeSpan.FromMinutes(2);
    }

    private static void DropUndeclared(EngineOptions options)
    {
        WaitForReady(options);
        options.SearchIndexesPrune = "drop";
    }

    /// <summary>Search indexes need the collection to exist, so give it a document first.</summary>
    private static async Task SeedAsync(CollectionHarness mongo) =>
        await mongo.Collection().InsertOneAsync(new BsonDocument { { "title", "hello" }, { "embedding", new BsonArray { 0.1, 0.2, 0.3 } } });

    private static async Task<List<BsonDocument>> SearchIndexesAsync(CollectionHarness mongo)
    {
        using var cursor = await mongo.Collection().SearchIndexes.ListAsync(null, null, TestContext.Current.CancellationToken);
        return await cursor.ToListAsync(TestContext.Current.CancellationToken);
    }
}
