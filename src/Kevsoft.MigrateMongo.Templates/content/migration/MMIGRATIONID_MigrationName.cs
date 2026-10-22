using Kevsoft.MigrateMongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Company.Migrations;

[Migration("MIGRATIONID", "migration-name")]
public sealed class MigrationName : IMigration
{
    public Task UpAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        // Make this method async, then track collections so each operation is recorded with the migration:
        //
        // var orders = context.Track(context.Database.GetCollection<BsonDocument>("orders"));
        // var result = await orders.UpdateManyAsync(
        //     Builders<BsonDocument>.Filter.Eq("status", "pending"),
        //     Builders<BsonDocument>.Update.Set("status", "open"),
        //     cancellationToken: cancellationToken);
        //
        // Record anything else worth keeping:
        //
        // context.Results.Set("reopened", result.ModifiedCount);
        // context.Results.Note("Orders without a customer were skipped.", NoteLevel.Warning);
        //
        // See what ran with `migrate-mongo show MIGRATIONID`.
        throw new NotImplementedException();
    }

    public Task DownAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        // Undo UpAsync. Tracked operations and results here are recorded against the rollback.
        throw new NotImplementedException();
    }
}
