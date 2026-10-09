using Kevsoft.MigrateMongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BasicMigrations.Migrations;

[Migration("20260101000001", "create-users", Transaction = TransactionMode.Never)]
public sealed class CreateUsers : IMigration
{
    public Task UpAsync(MigrationContext context, CancellationToken cancellationToken) =>
        context.Database.CreateCollectionAsync("users", new CreateCollectionOptions<BsonDocument>
        {
            Validator = new BsonDocument("$jsonSchema", new BsonDocument
            {
                { "bsonType", "object" },
                { "required", new BsonArray { "email" } },
            }),
        }, cancellationToken);

    public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) =>
        context.Database.DropCollectionAsync("users", cancellationToken);
}
