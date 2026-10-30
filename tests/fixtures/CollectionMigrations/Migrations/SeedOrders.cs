using Kevsoft.MigrateMongo;
using MongoDB.Bson;

namespace CollectionMigrations.Migrations;

[Migration("20260501000001", "seed-orders", Transaction = TransactionMode.Never)]
public sealed class SeedOrders : IMigration
{
    public Task UpAsync(MigrationContext context, CancellationToken cancellationToken) =>
        context.Track(context.Database.GetCollection<BsonDocument>("orders")).InsertManyAsync(
        [
            new BsonDocument { { "_id", 1 }, { "number", 1001 }, { "reference", "A-1" }, { "companyId", 1 }, { "status", "open" }, { "title", "First" } },
            new BsonDocument { { "_id", 2 }, { "number", 1002 }, { "reference", "A-2" }, { "companyId", 1 }, { "status", "closed" }, { "title", "Second" } },
            new BsonDocument { { "_id", 3 }, { "number", 1003 }, { "reference", "B-1" }, { "companyId", 2 }, { "status", "open" }, { "title", "Third" } },
        ], cancellationToken: cancellationToken);

    public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) =>
        context.Database.DropCollectionAsync("orders", cancellationToken);
}
