using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.MongoDriver;
using MongoDB.Bson;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.MongoDriver;

public sealed class UndeclaredPlannerTests
{
    private static readonly BsonDocument[] Entries =
    [
        Collection("orders"),
        Collection("leftover"),
        Collection("audit_2023"),
        Collection("_migrations"),
        Collection("_migrations.history"),
        Collection("_migrations.lock"),
        Collection("system.views"),
        Collection("system.buckets.metrics"),
        new() { { "name", "old_view" }, { "type", "view" }, { "options", new BsonDocument("viewOn", "orders") } },
    ];

    private static BsonDocument Collection(string name) => new() { { "name", name }, { "type", "collection" }, { "options", new BsonDocument() } };

    [Fact]
    public void Declared_ignored_system_and_state_collections_are_left_out()
    {
        var undeclared = UndeclaredPlanner.Undeclared(Entries, new UndeclaredPolicy { Declared = new HashSet<string> { "orders" }, Ignore = ["audit_*"] }, "_migrations");

        undeclared.Select(e => e["name"].AsString).ShouldBe(["leftover", "old_view"]);
    }

    [Fact]
    public void Report_keeps_everything()
    {
        var changes = UndeclaredPlanner.Plan([Collection("empty"), Collection("one"), Entries[^1]], "report", new Dictionary<string, long> { ["empty"] = 0, ["one"] = 1 });

        changes.Select(c => (c.Action, c.Index, c.Detail, c.Warning)).ShouldBe([
            ("keep", "empty", "not declared (0 documents)", (string?)null),
            ("keep", "one", "not declared (1 document)", null),
            ("keep", "old_view", "view on orders, not declared", null),
        ]);
    }

    [Fact]
    public void Drop_drops_views_and_empty_collections_and_warns_about_the_rest()
    {
        var changes = UndeclaredPlanner.Plan([Collection("empty"), Collection("full"), Entries[^1]], "drop", new Dictionary<string, long> { ["empty"] = 0, ["full"] = 12 });

        changes.Select(c => (c.Action, c.Index, c.Detail, c.Warning)).ShouldBe([
            ("drop-collection", "empty", "not declared (empty)", (string?)null),
            ("keep", "full", "not declared (12 documents)", "full isn't declared but isn't empty (12 documents), so it isn't dropped."),
            ("drop-view", "old_view", "view on orders, not declared", null),
        ]);
    }
}
