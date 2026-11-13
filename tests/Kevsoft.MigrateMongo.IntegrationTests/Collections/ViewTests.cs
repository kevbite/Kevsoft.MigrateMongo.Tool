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
    public async Task A_view_on_a_view_is_created_after_the_view_it_reads_from()
    {
        await using var mongo = new CollectionHarness(environment);
        var recent = CollectionHarness.DefineView("recent_active", "active_customers", v => v.Pipeline("""[{ "$sort": { "_id": -1 } }, { "$limit": 10 }]"""));

        var up = await mongo.UpAllAsync([recent, Active(), Customers]);

        up.Reconciled.ShouldBe(["customers", "active_customers", "recent_active"]);
        mongo.ListCollection("recent_active").ShouldNotBeNull()["options"]["viewOn"].AsString.ShouldBe("active_customers");
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
