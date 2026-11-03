using MongoDB.Bson;
using MongoDB.Driver;

namespace Kevsoft.MigrateMongo.MongoDriver;

/// <summary>
/// Carries out a <see cref="SearchIndexPlan"/> through <c>context.Track(...)</c>, so every search index command is recorded
/// (and a dropped index's full definition with it). When waiting is on, it polls <c>$listSearchIndexes</c> until each created
/// or updated index is queryable.
/// </summary>
internal static class SearchIndexApplier
{
    public static async Task ApplyAsync(
        MigrationContext context,
        string collectionName,
        SearchIndexPlan plan,
        bool waitForReady,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        if (plan.Changes.All(c => c.Commands.Count == 0))
        {
            return;
        }

        var database = context.Track(context.Database);
        var collection = context.Database.GetCollection<BsonDocument>(collectionName);
        foreach (var change in plan.Changes)
        {
            foreach (var command in change.Commands)
            {
                await database.RunCommandAsync(command, cancellationToken: cancellationToken);
            }

            if (waitForReady && change.WaitForName is { } name)
            {
                await WaitForReadyAsync(collection, collectionName, name, waitTimeout, cancellationToken);
            }
        }
    }

    private static async Task WaitForReadyAsync(
        IMongoCollection<BsonDocument> collection,
        string collectionName,
        string name,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + waitTimeout;
        while (true)
        {
            var index = await LatestAsync(collection, name, cancellationToken);
            var status = index?.GetValue("status", BsonNull.Value) is BsonString s ? s.AsString : null;
            if (index is not null && index.GetValue("queryable", false).ToBoolean())
            {
                return;
            }

            if (string.Equals(status, "FAILED", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Search index {collectionName}.{name} failed to build (status FAILED).");
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Search index {collectionName}.{name} wasn't queryable within {waitTimeout.TotalSeconds:0} seconds (status {status ?? "unknown"}).");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private static async Task<BsonDocument?> LatestAsync(IMongoCollection<BsonDocument> collection, string name, CancellationToken cancellationToken)
    {
        using var cursor = await collection.SearchIndexes.ListAsync(name, null, cancellationToken);
        var indexes = await cursor.ToListAsync(cancellationToken);
        return indexes.FirstOrDefault(i => i.GetValue("name", "").AsString == name);
    }
}
