using Kevsoft.MigrateMongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BasicMigrations.Migrations;

[Migration("20260101000002", "users-email-index", Transaction = TransactionMode.Never)]
public sealed class UsersEmailIndex : IMigration
{
    public Task UpAsync(MigrationContext context, CancellationToken cancellationToken) =>
        context.Database.GetCollection<BsonDocument>("users").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("email"),
                new CreateIndexOptions { Name = "email_1", Unique = true }),
            cancellationToken: cancellationToken);

    public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) =>
        context.Database.GetCollection<BsonDocument>("users").Indexes.DropOneAsync("email_1", cancellationToken);
}
