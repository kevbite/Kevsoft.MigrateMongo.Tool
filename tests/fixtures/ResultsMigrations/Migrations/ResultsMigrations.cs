using Kevsoft.MigrateMongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace ResultsMigrations.Migrations;

[Migration("20260401000001", "seed-orders", Transaction = TransactionMode.Never)]
public sealed class SeedOrders : IMigration
{
    public async Task UpAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        var orders = context.Track(context.Database.GetCollection<BsonDocument>("orders"));
        await orders.InsertManyAsync(
        [
            new BsonDocument { { "_id", 1 }, { "status", "complete" } },
            new BsonDocument { { "_id", 2 }, { "status", "complete" } },
            new BsonDocument { { "_id", 3 }, { "status", "open" } },
            new BsonDocument { { "_id", 4 }, { "status", "void" } },
        ], cancellationToken: cancellationToken);
        await orders.CreateIndexAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("status"), new CreateIndexOptions { Name = "status_1" }),
            cancellationToken: cancellationToken);
    }

    public async Task DownAsync(MigrationContext context, CancellationToken cancellationToken) =>
        await context.Database.DropCollectionAsync("orders", cancellationToken);
}

[Migration("20260401000002", "archive-orders")]
public sealed class ArchiveOrders : IMigration
{
    public async Task UpAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        var orders = context.Track(context.Database.GetCollection<BsonDocument>("orders"), "archive");
        await orders.UpdateManyAsync(
            Builders<BsonDocument>.Filter.Eq("status", "complete"),
            Builders<BsonDocument>.Update.Set("archived", true),
            cancellationToken: cancellationToken);

        var voided = await context.Track(context.Database.GetCollection<BsonDocument>("orders"))
            .DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("status", "void"), cancellationToken: cancellationToken);
        context.Results.Set("voidedRemoved", voided.DeletedCount);
        context.Results.Set("cutoff", new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc));
        context.Results.Note("Order 3 is still open.", NoteLevel.Warning);
    }

    public async Task DownAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        var result = await context.Database.GetCollection<BsonDocument>("orders").UpdateManyAsync(
            context.Session!,
            Builders<BsonDocument>.Filter.Eq("archived", true),
            Builders<BsonDocument>.Update.Unset("archived"),
            cancellationToken: cancellationToken);
        context.Results.Set("unarchived", result.ModifiedCount);
    }
}

[Migration("20260401000003", "drop-status-index", Transaction = TransactionMode.Never)]
public sealed class DropStatusIndex : IMigration
{
    public Task UpAsync(MigrationContext context, CancellationToken cancellationToken) =>
        context.Track(context.Database.GetCollection<BsonDocument>("orders"))
            .DropIndexAsync(Builders<BsonDocument>.IndexKeys.Ascending("status"), cancellationToken: cancellationToken);

    public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) =>
        context.Database.GetCollection<BsonDocument>("orders").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("status"), new CreateIndexOptions { Name = "status_1" }),
            cancellationToken: cancellationToken);
}
