using Kevsoft.MigrateMongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace BasicMigrations.Migrations;

/// <summary>Runs in a transaction (Auto on a replica set) and takes a dependency from the runner's services.</summary>
[Migration("20260101000003", "seed-defaults")]
public sealed class SeedDefaults(SeedOptions options) : IMigration
{
    public async Task UpAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        var session = context.Session ?? throw new InvalidOperationException("Expected to run in a transaction.");
        var roles = context.Database.GetCollection<BsonDocument>("roles");
        var users = context.Database.GetCollection<BsonDocument>("users");

        await roles.InsertManyAsync(session, [
            new BsonDocument { { "_id", "admin" }, { "permissions", new BsonArray { "read", "write" } } },
            new BsonDocument { { "_id", "reader" }, { "permissions", new BsonArray { "read" } } },
        ], cancellationToken: cancellationToken);

        await users.InsertOneAsync(session,
            new BsonDocument { { "_id", "admin" }, { "email", options.AdminEmail }, { "roles", new BsonArray { "admin" } } },
            cancellationToken: cancellationToken);
    }

    public async Task DownAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        var session = context.Session ?? throw new InvalidOperationException("Expected to run in a transaction.");
        await context.Database.GetCollection<BsonDocument>("users")
            .DeleteOneAsync(session, new BsonDocument("_id", "admin"), cancellationToken: cancellationToken);
        await context.Database.GetCollection<BsonDocument>("roles")
            .DeleteManyAsync(session, new BsonDocument("_id", new BsonDocument("$in", new BsonArray { "admin", "reader" })), cancellationToken: cancellationToken);
    }
}
