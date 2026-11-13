using Kevsoft.MigrateMongo.MongoDriver;
using MongoDB.Bson;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.MongoDriver;

public sealed class ViewPlannerTests
{
    private static readonly ViewDeclaration Active = new()
    {
        ViewOn = "customers",
        Pipeline = [BsonDocument.Parse("""{ "$match": { "status": "active" } }""")],
    };

    private static BsonDocument ViewEntry(string options) =>
        new() { { "name", "active" }, { "type", "view" }, { "options", BsonDocument.Parse(options) } };

    [Fact]
    public void A_missing_view_is_created_with_its_source_pipeline_and_collation()
    {
        var plan = ViewPlanner.Plan("active", new ViewDeclaration
        {
            ViewOn = "customers",
            Pipeline = Active.Pipeline,
            Collation = BsonDocument.Parse("""{ "locale": "fr" }"""),
        }, existing: null);

        var change = plan.Changes.ShouldHaveSingleItem();
        (change.Info.Action, change.Info.Index, change.Info.Detail).ShouldBe(("create-view", "view", "on customers, 1 stage"));
        change.Commands.ShouldBe([
            BsonDocument.Parse("""
                { "create": "active", "viewOn": "customers", "pipeline": [{ "$match": { "status": "active" } }], "collation": { "locale": "fr" } }
                """),
        ]);
        plan.IsView.ShouldBeTrue();
        plan.Declaration.ShouldStartWith("sha256:");
    }

    [Fact]
    public void A_view_stored_as_declared_is_in_sync()
    {
        var plan = ViewPlanner.Plan("active", Active, ViewEntry("""{ "viewOn": "customers", "pipeline": [{ "$match": { "status": "active" } }] }"""));

        plan.Changes.ShouldBeEmpty();
        plan.Problems.ShouldBeEmpty();
    }

    [Fact]
    public void A_changed_pipeline_is_modified_in_place_with_collMod()
    {
        var plan = ViewPlanner.Plan("active", new ViewDeclaration
        {
            ViewOn = "customers",
            Pipeline = [BsonDocument.Parse("""{ "$match": { "status": "open" } }"""), BsonDocument.Parse("""{ "$limit": 5 }""")],
        }, ViewEntry("""{ "viewOn": "customers", "pipeline": [{ "$match": { "status": "active" } }] }"""));

        var change = plan.Changes.ShouldHaveSingleItem();
        (change.Info.Action, change.Info.Index, change.Info.Detail).ShouldBe(("modify-view", "pipeline", """$match: { status: "active" } → { status: "open" }; + $limit: 5"""));
        change.Commands.ShouldBe([
            BsonDocument.Parse("""
                { "collMod": "active", "viewOn": "customers", "pipeline": [{ "$match": { "status": "open" } }, { "$limit": 5 }] }
                """),
        ]);
        change.Previous.ShouldBe(BsonDocument.Parse("""{ "viewOn": "customers", "pipeline": [{ "$match": { "status": "active" } }] }"""));
    }

    [Fact]
    public void A_changed_source_alone_is_a_viewOn_change()
    {
        var plan = ViewPlanner.Plan("active", Active, ViewEntry("""{ "viewOn": "clients", "pipeline": [{ "$match": { "status": "active" } }] }"""));

        var change = plan.Changes.ShouldHaveSingleItem();
        (change.Info.Action, change.Info.Index, change.Info.Detail).ShouldBe(("modify-view", "viewOn", "viewOn clients → customers"));
    }

    [Fact]
    public void A_changed_collation_drops_and_recreates_the_view()
    {
        var plan = ViewPlanner.Plan("active", Active, ViewEntry("""
            { "viewOn": "customers", "pipeline": [{ "$match": { "status": "active" } }], "collation": { "locale": "fr", "strength": 3, "version": "57.1" } }
            """));

        var change = plan.Changes.ShouldHaveSingleItem();
        (change.Info.Action, change.Info.Index, change.Info.Detail).ShouldBe(("recreate-view", "collation", """{ locale: "fr", strength: 3 } → none"""));
        change.Commands.ShouldBe([
            new BsonDocument("drop", "active"),
            BsonDocument.Parse("""{ "create": "active", "viewOn": "customers", "pipeline": [{ "$match": { "status": "active" } }] }"""),
        ]);
    }

    [Fact]
    public void A_collection_with_the_view_name_is_a_problem()
    {
        var plan = ViewPlanner.Plan("active", Active, new BsonDocument { { "name", "active" }, { "type", "collection" }, { "options", new BsonDocument() } });

        plan.Changes.ShouldBeEmpty();
        plan.Problems.ShouldBe(["active: a collection with this name exists, so it can't be managed as a view. Drop or rename the collection, or rename the definition."]);
    }
}
