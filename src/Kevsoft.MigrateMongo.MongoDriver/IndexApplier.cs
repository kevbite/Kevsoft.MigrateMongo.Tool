using Kevsoft.MigrateMongo.Protocol;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kevsoft.MigrateMongo.MongoDriver;

/// <summary>
/// Carries out an <see cref="IndexPlan"/> through <c>context.Track(...)</c>, so every index operation is recorded (and a
/// dropped index's full definition with it).
/// </summary>
internal static class IndexApplier
{
    public static async Task ApplyAsync(MigrationContext context, string collectionName, IndexPlan plan, CancellationToken cancellationToken)
    {
        var collection = context.Track(context.Database.GetCollection<BsonDocument>(collectionName));
        var database = context.Track(context.Database);
        foreach (var change in plan.Changes)
        {
            switch (change.Info.Action)
            {
                case IndexActions.Create:
                    await collection.CreateIndexAsync(change.Create!, cancellationToken);
                    break;
                case IndexActions.Modify or IndexActions.Hide:
                    foreach (var collMod in change.CollMods)
                    {
                        await CollModAsync(database, collectionName, collMod, cancellationToken);
                    }

                    if (change.ConvertToUnique)
                    {
                        await ConvertToUniqueAsync(database, collectionName, change.Info.Index, cancellationToken);
                    }

                    break;
                case IndexActions.Rebuild:
                    await RebuildAsync(collection, collectionName, change, cancellationToken);
                    break;
                case IndexActions.Drop:
                    await collection.DropIndexAsync(change.DropName!, cancellationToken: cancellationToken);
                    break;
            }
        }
    }

    private static Task<BsonDocument> CollModAsync(TrackedDatabase database, string collection, BsonDocument index, CancellationToken cancellationToken) =>
        database.RunCommandAsync(new BsonDocument { { "collMod", collection }, { "index", index } }, cancellationToken: cancellationToken);

    /// <summary>
    /// <c>prepareUnique</c> first (new duplicates are rejected), then <c>unique</c>, which fails if duplicates already exist.
    /// On failure, including cancellation, the index is left as it was.
    /// </summary>
    private static async Task ConvertToUniqueAsync(TrackedDatabase database, string collection, string index, CancellationToken cancellationToken)
    {
        var prepared = false;
        try
        {
            await CollModAsync(database, collection, new BsonDocument { { "name", index }, { "prepareUnique", true } }, cancellationToken);
            prepared = true;
            await CollModAsync(database, collection, new BsonDocument { { "name", index }, { "unique", true } }, cancellationToken);
        }
        // A prepareUnique the server rejected changed nothing; one that was cancelled or lost may still have been applied.
        catch (Exception e) when (prepared || e is not MongoCommandException)
        {
            try
            {
                await CollModAsync(database, collection, new BsonDocument { { "name", index }, { "prepareUnique", false } }, CancellationToken.None);
            }
            catch (Exception revertError) when (revertError is MongoException or TimeoutException)
            {
                throw new InvalidOperationException(
                    $"Making {collection}.{index} unique failed ({e.Message}), and turning prepareUnique off again failed too ({revertError.Message}).", e);
            }

            throw;
        }
    }

    /// <summary>
    /// Drops the index and creates the declared one. If creating fails or is cancelled, the dropped index is created again.
    /// </summary>
    private static async Task RebuildAsync(
        TrackedCollection<BsonDocument> collection,
        string collectionName,
        PlannedChange change,
        CancellationToken cancellationToken)
    {
        // The drop itself isn't cancellable, so once it's sent the index is known to be gone and is restored on any failure.
        cancellationToken.ThrowIfCancellationRequested();
        await collection.DropIndexAsync(change.DropName!, cancellationToken: CancellationToken.None);
        try
        {
            await collection.CreateIndexAsync(change.Create!, cancellationToken);
        }
        catch (Exception e)
        {
            try
            {
                var restore = new BsonDocument(change.Existing!.Where(element => element.Name is not ("v" or "ns")));
                await collection.CreateIndexAsync(restore, CancellationToken.None);
            }
            catch (Exception restoreError) when (restoreError is MongoException or TimeoutException)
            {
                throw new InvalidOperationException(
                    $"Rebuilding {collectionName}.{change.DropName} failed ({e.Message}), and restoring the previous index failed too ({restoreError.Message}).", e);
            }

            throw new InvalidOperationException($"Rebuilding {collectionName}.{change.DropName} failed ({e.Message}); the previous index was restored.", e);
        }
    }

}
