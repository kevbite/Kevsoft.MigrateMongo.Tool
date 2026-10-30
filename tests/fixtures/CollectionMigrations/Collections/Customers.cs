using Kevsoft.MigrateMongo;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace CollectionMigrations.Collections;

public sealed class Customer
{
    public ObjectId Id { get; set; }

    [BsonElement("email")]
    public string Email { get; set; } = "";

    [BsonElement("tier")]
    public string Tier { get; set; } = "";
}

/// <summary>A typed definition: the keys are rendered through the class map, so they use the element names.</summary>
[ManagedCollection("customers")]
public sealed class Customers : ICollectionDefinition<Customer>
{
    public void Define(CollectionBuilder<Customer> collection) =>
        collection.Indexes
            .Add(k => k.Ascending(c => c.Email), new CreateIndexOptions<Customer> { Unique = true })
            .Add(k => k.Ascending(c => c.Tier));
}
