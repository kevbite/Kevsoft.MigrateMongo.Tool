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

    private static CollectionDeclaration Declare<T>(Action<CollectionBuilder<T>> define) =>
        CollectionDescriptor.Create<Definition<T>, T>("orders", "Test.Orders", _ => new Definition<T>(define)).Declare(new EmptyServices(), Database);

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
