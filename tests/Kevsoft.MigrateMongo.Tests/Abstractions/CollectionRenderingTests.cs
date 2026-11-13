using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Abstractions;

public sealed class CollectionRenderingTests
{
    private static readonly IMongoDatabase Database = new MongoClient("mongodb://localhost:27017").GetDatabase("rendering");

    [Fact]
    public void Typed_keys_and_options_render_with_the_registered_serialization()
    {
        var declaration = Declare<Order>(c =>
        {
            c.Indexes.Prune = IndexPrune.Hide;
            c.Indexes.OnRebuild = IndexRebuild.Fail;
            c.Indexes
                .Add(k => k.Ascending(o => o.Reference), new CreateIndexOptions<Order> { Name = "reference_unique", Unique = true, Sparse = false })
                .Add(k => k.Ascending(o => o.ClosedAt), new CreateIndexOptions<Order> { ExpireAfter = TimeSpan.FromDays(30) }, IndexRebuild.Fail)
                .Add(
                    k => k.Ascending(o => o.CompanyId).Descending(o => o.Status),
                    new CreateIndexOptions<Order>
                    {
                        PartialFilterExpression = Builders<Order>.Filter.Eq(o => o.Status, "open"),
                        Collation = new Collation("en", strength: CollationStrength.Secondary),
                    })
                .Ignore("adhoc_*");
        });

        declaration.Name.ShouldBe("orders");
        declaration.Prune.ShouldBe(IndexPrune.Hide);
        declaration.OnRebuild.ShouldBe(IndexRebuild.Fail);
        declaration.Ignored.ShouldBe(["adhoc_*"]);
        declaration.Indexes.Select(i => (i.Keys, i.Options, i.OnRebuild)).ShouldBe([
            (new BsonDocument("ref", 1), new BsonDocument { { "name", "reference_unique" }, { "unique", true } }, null),
            (new BsonDocument("closedAt", 1), new BsonDocument("expireAfterSeconds", 2592000L), IndexRebuild.Fail),
            (
                new BsonDocument { { "companyId", 1 }, { "status", -1 } },
                new BsonDocument
                {
                    { "collation", new BsonDocument { { "locale", "en" }, { "strength", 2 } } },
                    { "partialFilterExpression", new BsonDocument("status", "open") },
                },
                (IndexRebuild?)null),
        ]);
    }

    [Fact]
    public void Text_options_use_the_server_names()
    {
        var declaration = Declare<BsonDocument>(c => c.Indexes.Add(
            k => k.Text("title"),
            new CreateIndexOptions<BsonDocument> { DefaultLanguage = "french", LanguageOverride = "lang", Weights = new BsonDocument("title", 5) }));

        declaration.Indexes.Single().Options.ShouldBe(new BsonDocument
        {
            { "weights", new BsonDocument("title", 5) },
            { "default_language", "french" },
            { "language_override", "lang" },
        });
    }

    [Fact]
    public void An_index_that_cant_be_rendered_names_the_collection_and_position()
    {
        var error = Should.Throw<InvalidOperationException>(() => Declare<Order>(c => c.Indexes
            .Add(k => k.Ascending(o => o.Status))
            .Add(k => k.Ascending(o => o.Reference.Length))));

        error.Message.ShouldStartWith("Index 2 on 'orders' couldn't be rendered:");
    }

    [Fact]
    public void Typed_time_series_fields_and_the_other_options_render_as_create_takes_them()
    {
        var declaration = Declare<Order>(c =>
        {
            c.Options
                .TimeSeries(o => o.ClosedAt, o => o.CompanyId, TimeSeriesGranularity.Hours)
                .ExpireAfter(TimeSpan.FromDays(30))
                .Collation(new Collation("fr", strength: CollationStrength.Secondary))
                .ChangeStreamPreAndPostImages();
            c.Options.OnUnchangeable = UnchangeableOption.Skip;
        });

        declaration.Options.ShouldBe(BsonDocument.Parse("""
            {
              "timeseries": { "timeField": "closedAt", "metaField": "companyId", "granularity": "hours" },
              "expireAfterSeconds": { "$numberLong": "2592000" },
              "collation": { "locale": "fr", "strength": 2 },
              "changeStreamPreAndPostImages": { "enabled": true }
            }
            """));
        declaration.OnUnchangeable.ShouldBe(UnchangeableOption.Skip);
        declaration.DeclaresOptions.ShouldBeTrue();
    }

    [Fact]
    public void Capped_clustered_and_custom_bucketing_render_as_create_takes_them()
    {
        Declare<Order>(c => c.Options.Capped(1024, 100)).Options
            .ShouldBe(BsonDocument.Parse("""{ "capped": true, "size": { "$numberLong": "1024" }, "max": { "$numberLong": "100" } }"""));
        Declare<Order>(c => c.Options.Clustered("by_id", TimeSpan.FromHours(1))).Options
            .ShouldBe(BsonDocument.Parse("""{ "clusteredIndex": { "key": { "_id": 1 }, "unique": true, "name": "by_id" }, "expireAfterSeconds": { "$numberLong": "3600" } }"""));
        Declare<Order>(c => c.Options.TimeSeries("at", null, TimeSpan.FromHours(2), TimeSpan.FromHours(2))).Options
            .ShouldBe(BsonDocument.Parse("""{ "timeseries": { "timeField": "at", "bucketMaxSpanSeconds": 7200, "bucketRoundingSeconds": 7200 } }"""));
    }

    [Fact]
    public void A_validator_renders_its_schema_level_and_action()
    {
        var declaration = Declare<Order>(c => c.Validator
            .JsonSchema("""{ "bsonType": "object", "required": ["ref"] }""")
            .Level(ValidationLevel.Moderate)
            .Action(ValidationAction.ErrorAndLog));

        var validator = declaration.Validator.ShouldNotBeNull();
        validator.Validator.ShouldBe(BsonDocument.Parse("""{ "$jsonSchema": { "bsonType": "object", "required": ["ref"] } }"""));
        validator.Level.ShouldBe("moderate");
        validator.Action.ShouldBe("errorAndLog");
        declaration.Options.ShouldBe(new BsonDocument());
    }

    [Fact]
    public void A_definition_without_options_declares_none()
    {
        var declaration = Declare<Order>(c => c.Indexes.Add(k => k.Ascending(o => o.Status)));

        declaration.Options.ShouldBe(new BsonDocument());
        declaration.Validator.ShouldBeNull();
        declaration.DeclaresOptions.ShouldBeFalse();
    }

    [Theory]
    [InlineData("capped+timeseries", "'orders' can't be both capped and a time series collection.")]
    [InlineData("capped+clustered", "'orders' can't be both capped and clustered.")]
    [InlineData("timeseries+clustered", "'orders' can't be both a time series and a clustered collection.")]
    public void Options_that_cannot_go_together_make_the_definition_invalid(string combination, string message)
    {
        var error = Should.Throw<InvalidOperationException>(() => Declare<Order>(c =>
        {
            if (combination.Contains("capped", StringComparison.Ordinal))
            {
                c.Options.Capped(4096);
            }

            if (combination.Contains("timeseries", StringComparison.Ordinal))
            {
                c.Options.TimeSeries("at");
            }

            if (combination.Contains("clustered", StringComparison.Ordinal))
            {
                c.Options.Clustered();
            }
        }));

        error.Message.ShouldBe(message);
    }

    [Fact]
    public void A_typed_view_pipeline_renders_with_the_source_documents_serialization()
    {
        var declaration = DeclareView<Order>(v => v
            .Pipeline(p => p.Match(o => o.Status == "open").Project(o => new { o.Reference }))
            .Collation(new Collation("en", strength: CollationStrength.Secondary)));

        declaration.Name.ShouldBe("open_orders");
        declaration.Indexes.ShouldBeEmpty();
        var view = declaration.View.ShouldNotBeNull();
        view.ViewOn.ShouldBe("orders");
        view.Pipeline.ShouldBe([
            BsonDocument.Parse("""{ "$match": { "status": "open" } }"""),
            BsonDocument.Parse("""{ "$project": { "Reference": "$ref", "_id": 0 } }"""),
        ]);
        view.Collation.ShouldBe(BsonDocument.Parse("""{ "locale": "en", "strength": 2 }"""));
    }

    [Fact]
    public void A_view_pipeline_can_be_json()
    {
        var view = DeclareView<BsonDocument>(v => v.Pipeline("""[{ "$match": { "status": "open" } }, { "$limit": 5 }]""")).View.ShouldNotBeNull();

        view.Pipeline.ShouldBe([BsonDocument.Parse("""{ "$match": { "status": "open" } }"""), BsonDocument.Parse("""{ "$limit": 5 }""")]);
        view.Collation.ShouldBeNull();
    }

    [Theory]
    [InlineData("""[{ "$match": {} }, { "$out": "copy" }]""", "$out")]
    [InlineData("""[{ "$merge": { "into": "copy" } }]""", "$merge")]
    [InlineData("""[{ "$facet": { "a": [{ "$match": {} }, { "$out": "copy" }] } }]""", "$out")]
    public void A_view_pipeline_that_writes_output_is_invalid(string pipeline, string stage)
    {
        var error = Should.Throw<InvalidOperationException>(() => DeclareView<BsonDocument>(v => v.Pipeline(pipeline)));

        error.Message.ShouldBe($"The pipeline of view 'open_orders' has a {stage} stage, which MongoDB doesn't allow in a view.");
    }

    [Fact]
    public void A_json_view_pipeline_must_be_an_array_of_documents()
    {
        Should.Throw<FormatException>(() => DeclareView<BsonDocument>(v => v.Pipeline("""{ "$match": {} }""")));
    }

    private static CollectionDeclaration DeclareView<T>(Action<ViewBuilder<T>> define) =>
        CollectionDescriptor.CreateView<ViewDefinition<T>, T>("open_orders", "orders", "Test.OpenOrders", _ => new ViewDefinition<T>(define)).Declare(new EmptyServices(), Database);

    private sealed class ViewDefinition<T>(Action<ViewBuilder<T>> define) : IViewDefinition<T>
    {
        public void Define(ViewBuilder<T> view) => define(view);
    }

    private static CollectionDeclaration Declare<T>(Action<CollectionBuilder<T>> define) =>        CollectionDescriptor.Create<Definition<T>, T>("orders", "Test.Orders", _ => new Definition<T>(define)).Declare(new EmptyServices(), Database);

    private sealed class Definition<T>(Action<CollectionBuilder<T>> define) : ICollectionDefinition<T>
    {
        public void Define(CollectionBuilder<T> collection) => define(collection);
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class Order
    {
        public ObjectId Id { get; set; }

        [BsonElement("ref")]
        public string Reference { get; set; } = "";

        [BsonElement("companyId")]
        public int CompanyId { get; set; }

        [BsonElement("status")]
        public string Status { get; set; } = "";

        [BsonElement("closedAt")]
        public DateTime? ClosedAt { get; set; }
    }
}
