using Kevsoft.MigrateMongo.MongoDriver;
using MongoDB.Bson;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.MongoDriver;

public sealed class ViewDependenciesTests
{
    [Fact]
    public void A_pipeline_references_lookup_graph_lookup_and_union_with_sources()
    {
        var pipeline = Pipeline();

        ViewDependencies.Referenced(pipeline).Order(StringComparer.Ordinal)
            .ShouldBe(["audit", "customers", "nested_union", "orders", "regions"]);
    }

    [Fact]
    public void A_view_options_document_references_its_view_on_and_pipeline_sources()
    {
        var options = new BsonDocument
        {
            { "viewOn", "base" },
            { "pipeline", new BsonArray { new BsonDocument("$lookup", new BsonDocument { { "from", "joined" }, { "as", "j" } }) } },
        };

        ViewDependencies.ReferencedBy(options).Order(StringComparer.Ordinal).ShouldBe(["base", "joined"]);
    }

    [Fact]
    public void Literal_and_expression_field_names_are_not_treated_as_sources()
    {
        var pipeline = new BsonArray
        {
            new BsonDocument("$project", new BsonDocument("payload", new BsonDocument("$literal", new BsonDocument("$lookup", "label")))),
        };

        ViewDependencies.Referenced(pipeline).ShouldBeEmpty();
    }

    private static BsonArray Pipeline() =>
    [
        new BsonDocument("$lookup", new BsonDocument
        {
            { "from", "customers" },
            { "as", "c" },
            { "pipeline", new BsonArray { new BsonDocument("$unionWith", new BsonDocument { { "coll", "nested_union" }, { "pipeline", new BsonArray() } }) } },
        }),
        new BsonDocument("$graphLookup", new BsonDocument { { "from", "regions" }, { "startWith", "$region" } }),
        new BsonDocument("$unionWith", "orders"),
        new BsonDocument("$facet", new BsonDocument
        {
            { "a", new BsonArray { new BsonDocument("$lookup", new BsonDocument { { "from", "audit" }, { "as", "a" } }) } },
        }),
    ];
}
