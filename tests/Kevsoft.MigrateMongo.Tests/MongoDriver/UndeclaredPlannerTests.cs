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

    private static BsonDocument View(string name, string on, BsonArray? pipeline = null) => new()
    {
        { "name", name },
        { "type", "view" },
        { "options", pipeline is null ? new BsonDocument("viewOn", on) : new BsonDocument { { "viewOn", on }, { "pipeline", pipeline } } },
    };

    [Fact]
    public void Declared_ignored_system_and_state_collections_are_left_out()
    {
        var undeclared = UndeclaredPlanner.Undeclared(Entries, new HashSet<string> { "orders" }, ["audit_*"], "_migrations");

        undeclared.Select(e => e["name"].AsString).ShouldBe(["leftover", "old_view"]);
    }

    [Fact]
    public void A_source_a_declared_view_reads_from_through_a_lookup_is_protected()
    {
        BsonDocument[] entries =
        [
            Collection("orders"),
            Collection("lookup_target"),
            View("active", "orders", [new BsonDocument("$lookup", new BsonDocument { { "from", "lookup_target" }, { "as", "x" } })]),
        ];

        var undeclared = UndeclaredPlanner.Undeclared(entries, new HashSet<string> { "active", "orders" }, [], "_migrations");

        undeclared.ShouldBeEmpty();
    }

    [Fact]
    public void An_undeclared_view_a_declared_view_depends_on_and_its_source_are_protected()
    {
        BsonDocument[] entries =
        [
            Collection("base"),
            View("middle", "base"),
            View("top", "middle"),
        ];

        var undeclared = UndeclaredPlanner.Undeclared(entries, new HashSet<string> { "top", "middle" }, [], "_migrations");

        undeclared.ShouldBeEmpty();
    }

    [Fact]
    public void An_undeclared_view_no_declared_view_depends_on_is_still_undeclared()
    {
        BsonDocument[] entries =
        [
            Collection("orders"),
            View("old_view", "orders"),
        ];

        var undeclared = UndeclaredPlanner.Undeclared(entries, new HashSet<string> { "orders" }, [], "_migrations");

        undeclared.Select(e => e["name"].AsString).ShouldBe(["old_view"]);
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
