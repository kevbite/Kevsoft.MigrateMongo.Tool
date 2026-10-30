using Kevsoft.MigrateMongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace CollectionMigrations.Collections;

// The integration tests rewrite this file to change the definition between runs.
[ManagedCollection("orders")]
public sealed class Orders : ICollectionDefinition<BsonDocument>
{
    public void Define(CollectionBuilder<BsonDocument> collection) =>
        collection.Indexes
            .Add(k => k.Ascending("companyId").Descending("createdAt"))
            .Add(k => k.Ascending("reference"), new CreateIndexOptions<BsonDocument> { Unique = true })
            .Add(k => k.Ascending("number"))
            .Add(k => k.Ascending("closedAt"), new CreateIndexOptions<BsonDocument> { ExpireAfter = TimeSpan.FromDays(30) })
            .Add(k => k.Text("title").Text("notes"), new CreateIndexOptions<BsonDocument> { Weights = new BsonDocument("title", 10) })
            .Add(k => k.Ascending("customer"), new CreateIndexOptions<BsonDocument> { Name = "customer_ci", Collation = new Collation("en", strength: CollationStrength.Secondary) })
            .Add(k => k.Ascending("companyId"), new CreateIndexOptions<BsonDocument> { Name = "open_by_company", PartialFilterExpression = Builders<BsonDocument>.Filter.Eq("status", "open") })
            .Ignore("adhoc_*");
}
