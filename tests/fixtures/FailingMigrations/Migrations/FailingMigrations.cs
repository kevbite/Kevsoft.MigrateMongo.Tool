using Kevsoft.MigrateMongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FailingMigrations.Migrations;

[Migration("20260201000001", "create-things")]
public sealed class CreateThings : IMigration
{
    public Task UpAsync(MigrationContext context, CancellationToken cancellationToken) =>
        context.Database.GetCollection<BsonDocument>("things")
            .InsertOneAsync(context.Session!, new BsonDocument { { "_id", "first" } }, cancellationToken: cancellationToken);

    public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) =>
        context.Database.GetCollection<BsonDocument>("things")
            .DeleteOneAsync(context.Session!, new BsonDocument("_id", "first"), cancellationToken: cancellationToken);
}

/// <summary>Writes inside its transaction and then fails, so the write must be rolled back.</summary>
[Migration("20260201000002", "explodes-after-write")]
public sealed class ExplodesAfterWrite : IMigration
{
    public async Task UpAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        await context.Database.GetCollection<BsonDocument>("things")
            .InsertOneAsync(context.Session!, new BsonDocument { { "_id", "partial" } }, cancellationToken: cancellationToken);
        throw new InvalidOperationException("Boom after writing.");
    }

    public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

[Migration("20260201000003", "never-reached")]
public sealed class NeverReached : IMigration
{
    public Task UpAsync(MigrationContext context, CancellationToken cancellationToken) =>
        context.Database.GetCollection<BsonDocument>("things")
            .InsertOneAsync(context.Session!, new BsonDocument { { "_id", "never" } }, cancellationToken: cancellationToken);

    public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}
