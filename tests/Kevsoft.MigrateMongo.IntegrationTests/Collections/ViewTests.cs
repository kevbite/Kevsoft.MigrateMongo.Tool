using Kevsoft.MigrateMongo.Protocol;
using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests.Collections;

/// <summary>
/// Views against a real server: created after the collections, changed in place with <c>collMod</c>, recreated when the
/// collation changes, ordered when one reads from another, and blocked by a collection with the same name.
/// </summary>
public sealed class ViewTests(IntegrationEnvironment environment)
{
    private static readonly CollectionDescriptor Customers =
        CollectionHarness.Define<BsonDocument>(c => c.Indexes.Add(i => i.Ascending("status")), "customers");

    private static CollectionDescriptor Active(string status = "active") =>
        CollectionHarness.DefineView("active_customers", "customers", v => v.Pipeline($$"""[{ "$match": { "status": "{{status}}" } }]"""));

    [Fact]
    public async Task A_view_is_created_after_its_collection()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.Collection("customers").InsertManyAsync([new BsonDocument { { "status", "active" } }, new BsonDocument { { "status", "closed" } }]);

        var up = await mongo.UpAllAsync([Active(), Customers]);

        up.Reconciled.ShouldBe(["customers", "active_customers"]);
        var entry = mongo.ListCollection("active_customers").ShouldNotBeNull();
        entry["type"].AsString.ShouldBe("view");
        entry["options"].AsBsonDocument.ShouldBe(BsonDocument.Parse("""{ "viewOn": "customers", "pipeline": [{ "$match": { "status": "active" } }] }"""));
        (await mongo.Collection("active_customers").CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty)).ShouldBe(1);
        mongo.Operations("active_customers").ShouldBe(["runCommand"]);
        mongo.History().Select(h => h["migrationId"].AsString).ShouldContain("active_customers");

        await mongo.ShouldBeInSyncAsync(Active());
    }

    [Fact]
    public async Task A_changed_pipeline_is_applied_in_place_with_collMod()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.UpAllAsync([Customers, Active()]);

        var plan = await mongo.PlanAsync(Active("closed"));
        var change = plan.Collections.ShouldNotBeNull().Single().Changes.ShouldHaveSingleItem();
        (change.Action, change.Index, change.Detail).ShouldBe(("modify-view", "pipeline", """$match: { status: "active" } → { status: "closed" }"""));

        await mongo.ApplyAsync(Active("closed"));

        mongo.ListCollection("active_customers").ShouldNotBeNull()["options"].AsBsonDocument
            .ShouldBe(BsonDocument.Parse("""{ "viewOn": "customers", "pipeline": [{ "$match": { "status": "closed" } }] }"""));
        mongo.Operations("active_customers").ShouldBe(["runCommand"]);
    }

    [Fact]
    public async Task A_changed_collation_drops_and_recreates_the_view()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.UpAllAsync([Customers, Active()]);

        var french = CollectionHarness.DefineView("active_customers", "customers", v => v
            .Pipeline("""[{ "$match": { "status": "active" } }]""")
            .Collation(new Collation("fr")));
        var plan = await mongo.PlanAsync(french);
        var change = plan.Collections.ShouldNotBeNull().Single().Changes.ShouldHaveSingleItem();
        (change.Action, change.Index).ShouldBe(("recreate-view", "collation"));

        await mongo.ApplyAsync(french);

        var options = mongo.ListCollection("active_customers").ShouldNotBeNull()["options"].AsBsonDocument;
        options["collation"]["locale"].AsString.ShouldBe("fr");
        mongo.Operations("active_customers").ShouldBe(["runCommand", "runCommand"]);
    }

    [Fact]
    public async Task A_recreate_cancelled_after_the_drop_restores_the_previous_view()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.UpAllAsync([Customers, Active()]);

        var french = CollectionHarness.DefineView("active_customers", "customers", v => v
            .Pipeline("""[{ "$match": { "status": "active" } }]""")
            .Collation(new Collation("fr")));
        using var cancel = new CancellationTokenSource();
        using var client = mongo.CancellingAfter("drop", cancel);

        var up = await mongo.RunAsync("up", [Customers, french], client: client, cancellationToken: cancel.Token);

        up.ExitCode.ShouldBe(ExitCodes.Failure);
        up.Message!.ShouldContain("the previous view was restored");
        // The drop went through and the create was cancelled, so the view is created again as it was rather than left missing.
        var entry = mongo.ListCollection("active_customers").ShouldNotBeNull();
        entry["type"].AsString.ShouldBe("view");
        entry["options"].AsBsonDocument.Contains("collation").ShouldBeFalse();
        entry["options"]["pipeline"].AsBsonArray.ShouldHaveSingleItem().AsBsonDocument
            .ShouldBe(BsonDocument.Parse("""{ "$match": { "status": "active" } }"""));
    }

    [Fact]
    public async Task A_view_on_a_view_is_created_after_the_view_it_reads_from()
    {
        await using var mongo = new CollectionHarness(environment);
        var recent = CollectionHarness.DefineView("recent_active", "active_customers", v => v.Pipeline("""[{ "$sort": { "_id": -1 } }, { "$limit": 10 }]"""));

        var up = await mongo.UpAllAsync([recent, Active(), Customers]);

        up.Reconciled.ShouldBe(["customers", "active_customers", "recent_active"]);
        mongo.ListCollection("recent_active").ShouldNotBeNull()["options"]["viewOn"].AsString.ShouldBe("active_customers");
    }

    [Fact]
    public async Task A_view_with_a_lookup_sub_pipeline_is_created_and_joins_on_the_server()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.Collection("customers").InsertOneAsync(new BsonDocument { { "_id", 1 }, { "status", "active" } });
        await mongo.Collection("orders").InsertManyAsync(
        [
            new BsonDocument { { "customerId", 1 }, { "total", 10 } },
            new BsonDocument { { "customerId", 1 }, { "total", 20 } },
            new BsonDocument { { "customerId", 2 }, { "total", 99 } },
        ]);

        var orders = CollectionHarness.Define<BsonDocument>(c => c.Indexes.Add(i => i.Ascending("customerId")), "orders");
        var joined = CollectionHarness.DefineView("customer_orders", "customers", v => v.Pipeline(
            """
            [{ "$lookup": {
                "from": "orders",
                "let": { "cid": "$_id" },
                "pipeline": [{ "$match": { "$expr": { "$eq": ["$customerId", "$$cid"] } } }],
                "as": "orders"
            } }]
            """));

        var up = await mongo.UpAllAsync([joined, Customers, orders]);

        up.Reconciled.ShouldBe(["customers", "orders", "customer_orders"]);
        var entry = mongo.ListCollection("customer_orders").ShouldNotBeNull();
        entry["type"].AsString.ShouldBe("view");
        entry["options"]["viewOn"].AsString.ShouldBe("customers");
        entry["options"]["pipeline"].AsBsonArray.ShouldHaveSingleItem().AsBsonDocument.Names.ShouldHaveSingleItem().ShouldBe("$lookup");

        var document = (await mongo.Collection("customer_orders").FindAsync(FilterDefinition<BsonDocument>.Empty)).ToList().ShouldHaveSingleItem();
        document["orders"].AsBsonArray.Count.ShouldBe(2);

        await mongo.ShouldBeInSyncAsync(joined);
    }

    [Fact]
    public async Task A_collection_with_the_same_name_blocks_the_view()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.Database.CreateCollectionAsync("active_customers");

        var up = await mongo.RunAsync("up", [Customers, Active()]);

        up.ExitCode.ShouldBe(ExitCodes.ValidationFailed);
        up.Issues.ShouldNotBeNull().ShouldContain(i => i.Message.Contains("active_customers: a collection with this name exists, so it can't be managed as a view."));
        mongo.ListCollection("active_customers").ShouldNotBeNull()["type"].AsString.ShouldBe("collection");
    }
}
