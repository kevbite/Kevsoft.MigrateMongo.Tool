using Kevsoft.MigrateMongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace RetryMigrations.Migrations;

/// <summary>
/// Writes inside its transaction, then fails its first attempt with a driver error labelled
/// <c>TransientTransactionError</c> (as a write conflict or primary step-down would). Each attempt is also recorded
/// outside the transaction so the test can see how many times it ran.
/// </summary>
[Migration("20260301000001", "flaky")]
public sealed class Flaky : IMigration
{
    private static int _attempts;

    public async Task UpAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        var attempt = Interlocked.Increment(ref _attempts);
        await context.Database.GetCollection<BsonDocument>("attempts")
            .InsertOneAsync(new BsonDocument("attempt", attempt), cancellationToken: cancellationToken);
        await context.Database.GetCollection<BsonDocument>("things")
            .InsertOneAsync(context.Session!, new BsonDocument("_id", "flaky"), cancellationToken: cancellationToken);

        if (attempt == 1)
        {
            var error = new MongoException("Simulated write conflict.");
            error.AddErrorLabel("TransientTransactionError");
            throw error;
        }
    }

    public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) =>
        context.Database.GetCollection<BsonDocument>("things")
            .DeleteOneAsync(context.Session!, new BsonDocument("_id", "flaky"), cancellationToken: cancellationToken);
}
