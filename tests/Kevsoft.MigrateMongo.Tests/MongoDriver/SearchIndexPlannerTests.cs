using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.MongoDriver;
using MongoDB.Bson;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.MongoDriver;

public sealed class SearchIndexPlannerTests
{
    [Fact]
    public void A_declared_search_index_that_is_missing_is_created()
    {
        var plan = Plan(
            [Declared("products", """{ "mappings": { "dynamic": true } }""")],
            []);

        var change = plan.Changes.ShouldHaveSingleItem();
        change.Info.Action.ShouldBe("create-search");
        change.Info.Index.ShouldBe("products");
        change.Info.Detail.ShouldBe("search");
        change.WaitForName.ShouldBe("products");
        change.Commands.ShouldBe([
            new BsonDocument
            {
                { "createSearchIndexes", "catalog" },
                {
                    "indexes", new BsonArray
                    {
                        new BsonDocument
                        {
                            { "name", "products" },
                            { "type", "search" },
                            { "definition", new BsonDocument("mappings", new BsonDocument("dynamic", true)) },
                        },
                    }
                },
            },
        ]);
        plan.Problems.ShouldBeEmpty();
    }

    [Fact]
    public void A_declared_vector_index_is_created_with_the_vectorSearch_type()
    {
        var definition = """{ "fields": [ { "type": "vector", "path": "embedding", "numDimensions": 3, "similarity": "cosine" } ] }""";
        var plan = Plan([DeclaredVector("vectors", definition)], []);

        var change = plan.Changes.ShouldHaveSingleItem();
        change.Info.Action.ShouldBe("create-search");
        change.Info.Detail.ShouldBe("vectorSearch");
        change.Commands.ShouldHaveSingleItem()["indexes"][0]["type"].AsString.ShouldBe("vectorSearch");
    }

    [Fact]
    public void A_search_index_whose_definition_matches_the_server_is_left_alone()
    {
        var plan = Plan(
            [Declared("products", """{ "mappings": { "dynamic": true } }""")],
            [Server("products", "search", """{ "mappings": { "dynamic": true } }""")]);

        plan.Changes.ShouldBeEmpty();
        plan.Indexes.ShouldHaveSingleItem().Status.ShouldBe("declared");
    }

    [Fact]
    public void A_changed_definition_is_updated_online()
    {
        var plan = Plan(
            [Declared("products", """{ "mappings": { "dynamic": false } }""")],
            [Server("products", "search", """{ "mappings": { "dynamic": true } }""")]);

        var change = plan.Changes.ShouldHaveSingleItem();
        change.Info.Action.ShouldBe("update-search");
        change.Info.Detail.ShouldBe("definition changed");
        change.WaitForName.ShouldBe("products");
        change.Commands.ShouldBe([
            new BsonDocument
            {
                { "updateSearchIndex", "catalog" },
                { "name", "products" },
                { "definition", new BsonDocument("mappings", new BsonDocument("dynamic", false)) },
            },
        ]);
    }

    [Fact]
    public void Field_order_in_the_definition_is_not_a_change()
    {
        var plan = Plan(
            [Declared("products", """{ "mappings": { "dynamic": true }, "analyzer": "lucene.standard" }""")],
            [Server("products", "search", """{ "analyzer": "lucene.standard", "mappings": { "dynamic": true } }""")]);

        plan.Changes.ShouldBeEmpty();
    }

    [Fact]
    public void Changing_the_type_drops_and_recreates_the_index()
    {
        var plan = Plan(
            [DeclaredVector("products", """{ "fields": [] }""")],
            [Server("products", "search", """{ "mappings": { "dynamic": true } }""")]);

        var change = plan.Changes.ShouldHaveSingleItem();
        change.Info.Action.ShouldBe("update-search");
        change.Info.Detail.ShouldBe("type search → vectorSearch (recreated)");
        change.Commands.Count.ShouldBe(2);
        change.Commands[0].ShouldBe(new BsonDocument { { "dropSearchIndex", "catalog" }, { "name", "products" } });
        change.Commands[1]["createSearchIndexes"].AsString.ShouldBe("catalog");
        change.Commands[1]["indexes"][0]["type"].AsString.ShouldBe("vectorSearch");
    }

    [Fact]
    public void An_index_without_a_server_type_is_treated_as_a_plain_search_index()
    {
        var server = new BsonDocument
        {
            { "name", "products" },
            { "queryable", true },
            { "latestDefinition", BsonDocument.Parse("""{ "mappings": { "dynamic": true } }""") },
        };

        var plan = Plan([Declared("products", """{ "mappings": { "dynamic": true } }""")], [server]);

        plan.Changes.ShouldBeEmpty();
    }

    [Fact]
    public void An_undeclared_search_index_is_reported_by_default()
    {
        var plan = Plan(
            [Declared("products", """{ "mappings": { "dynamic": true } }""")],
            [Server("products", "search", """{ "mappings": { "dynamic": true } }"""), Server("adhoc", "search", "{ }")]);

        var change = plan.Changes.ShouldHaveSingleItem();
        change.Info.Action.ShouldBe("keep");
        change.Info.Index.ShouldBe("adhoc");
        change.Info.Detail.ShouldBe("not declared (prune: report)");
    }

    [Fact]
    public void An_undeclared_search_index_is_dropped_when_prune_is_drop()
    {
        var plan = Plan(
            [Declared("products", """{ "mappings": { "dynamic": true } }""")],
            [Server("products", "search", """{ "mappings": { "dynamic": true } }"""), Server("adhoc", "search", "{ }")],
            Policy(searchPrune: "drop"));

        var change = plan.Changes.ShouldHaveSingleItem();
        change.Info.Action.ShouldBe("drop-search");
        change.Info.Index.ShouldBe("adhoc");
        change.Commands.ShouldBe([new BsonDocument { { "dropSearchIndex", "catalog" }, { "name", "adhoc" } }]);
    }

    [Fact]
    public void A_definitions_prune_setting_overrides_the_config()
    {
        var declaration = new CollectionDeclaration
        {
            Name = "catalog",
            Indexes = [],
            SearchIndexes = [Declared("products", """{ "mappings": { "dynamic": true } }""")],
            SearchPrune = SearchIndexPrune.Drop,
        };

        var plan = SearchIndexPlanner.Plan(
            declaration,
            [Server("products", "search", """{ "mappings": { "dynamic": true } }"""), Server("adhoc", "search", "{ }")],
            Policy());

        plan.Changes.ShouldHaveSingleItem().Info.Action.ShouldBe("drop-search");
    }

    [Fact]
    public void Ignored_search_indexes_are_never_dropped()
    {
        var declaration = new CollectionDeclaration
        {
            Name = "catalog",
            Indexes = [],
            SearchIndexes = [Declared("products", """{ "mappings": { "dynamic": true } }""")],
            SearchIgnored = ["adhoc_*"],
        };

        var plan = SearchIndexPlanner.Plan(
            declaration,
            [Server("products", "search", """{ "mappings": { "dynamic": true } }"""), Server("adhoc_test", "search", "{ }")],
            Policy(searchPrune: "drop"));

        plan.Changes.ShouldBeEmpty();
        plan.Indexes.Single(i => i.Name == "adhoc_test").Status.ShouldBe("ignored");
    }

    [Fact]
    public void Declaring_the_same_name_twice_is_a_problem()
    {
        var plan = Plan(
            [Declared("products", "{ }"), Declared("products", """{ "mappings": { "dynamic": true } }""")],
            []);

        plan.Problems.ShouldBe(["catalog: more than one declared search index is named products."]);
    }

    [Fact]
    public void Declaring_the_same_name_twice_marks_the_plan_invalid()
    {
        var plan = Plan(
            [Declared("products", "{ }"), Declared("products", """{ "mappings": { "dynamic": true } }""")],
            []);

        plan.Invalid.ShouldBeTrue();
    }

    [Fact]
    public void Declaring_a_change_to_an_index_whose_name_is_ignored_is_a_problem_not_an_update()
    {
        var declaration = new CollectionDeclaration
        {
            Name = "catalog",
            Indexes = [],
            SearchIndexes = [Declared("adhoc_text", """{ "mappings": { "dynamic": false } }""")],
            SearchIgnored = ["adhoc_*"],
        };

        var plan = SearchIndexPlanner.Plan(
            declaration,
            [Server("adhoc_text", "search", """{ "mappings": { "dynamic": true } }""")],
            Policy());

        plan.Changes.ShouldBeEmpty();
        plan.Problems.ShouldHaveSingleItem().ShouldContain("adhoc_text");
    }

    [Fact]
    public void Removing_a_field_from_a_static_mapping_is_an_update()
    {
        var plan = Plan(
            [Declared("products", """{ "mappings": { "dynamic": false, "fields": { "title": { "type": "string" } } } }""")],
            [Server("products", "search", """{ "mappings": { "dynamic": false, "fields": { "title": { "type": "string" }, "description": { "type": "string" } } } }""")]);

        var change = plan.Changes.ShouldHaveSingleItem();
        change.Info.Action.ShouldBe("update-search");
    }

    [Fact]
    public void A_declared_index_whose_build_failed_is_not_in_sync()
    {
        var plan = Plan(
            [Declared("products", """{ "mappings": { "dynamic": true } }""")],
            [Server("products", "search", """{ "mappings": { "dynamic": true } }""", status: "FAILED", queryable: false)]);

        plan.Changes.ShouldNotBeEmpty();
    }

    [Fact]
    public void The_declaration_hash_ignores_order_but_not_the_definition()
    {
        var a = Declared("a", """{ "mappings": { "dynamic": true } }""");
        var b = DeclaredVector("b", """{ "fields": [] }""");

        Hash([a, b]).ShouldBe(Hash([b, a]));
        Hash([a, b]).ShouldNotBe(Hash([a, Declared("b", """{ "mappings": { "dynamic": false } }""")]));
        Hash([a]).ShouldStartWith("sha256:");
    }

    private static SearchIndexPlan Plan(SearchIndexDeclaration[] declared, BsonDocument[] existing, CollectionPolicy? policy = null) =>
        SearchIndexPlanner.Plan(
            new CollectionDeclaration { Name = "catalog", Indexes = [], SearchIndexes = declared },
            existing,
            policy ?? Policy());

    private static string Hash(SearchIndexDeclaration[] declared) =>
        SearchIndexPlanner.Plan(new CollectionDeclaration { Name = "catalog", Indexes = [], SearchIndexes = declared }, [], Policy()).Declaration;

    private static CollectionPolicy Policy(string searchPrune = "report") => new()
    {
        SearchPrune = searchPrune,
        Now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc),
    };

    private static SearchIndexDeclaration Declared(string name, string definition) =>
        new() { Name = name, Type = "search", Definition = BsonDocument.Parse(definition) };

    private static SearchIndexDeclaration DeclaredVector(string name, string definition) =>
        new() { Name = name, Type = "vectorSearch", Definition = BsonDocument.Parse(definition) };

    private static BsonDocument Server(string name, string type, string definition) =>
        Server(name, type, definition, status: "READY", queryable: true);

    private static BsonDocument Server(string name, string type, string definition, string status, bool queryable) => new()
    {
        { "id", ObjectId.GenerateNewId().ToString() },
        { "name", name },
        { "type", type },
        { "status", status },
        { "queryable", queryable },
        { "latestDefinition", BsonDocument.Parse(definition) },
    };
}
