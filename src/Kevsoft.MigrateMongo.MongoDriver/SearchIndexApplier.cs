using MongoDB.Bson;
using MongoDB.Driver;

namespace Kevsoft.MigrateMongo.MongoDriver;

/// <summary>
/// Carries out a <see cref="SearchIndexPlan"/> through <c>context.Track(...)</c>, so every search index command is recorded
/// (and a dropped index's full definition with it). The collection exists by then: every managed collection is created
/// explicitly before its indexes. A type change (or a failed-build rebuild) drops then creates, waiting for the old index to disappear
/// in between so the name is free. When waiting is on, it polls <c>$listSearchIndexes</c> until each created or updated index
/// (and each already declared one still building) has reached <c>READY</c> with the requested definition, and until each
/// dropped index has gone.
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
        if (plan.Changes.All(c => c.Commands.Count == 0) && (!waitForReady || plan.Pending.Count == 0))
        {
            return;
        }

        var database = context.Track(context.Database);
        var collection = context.Database.GetCollection<BsonDocument>(collectionName);

        foreach (var change in plan.Changes)
        {
            foreach (var command in change.Commands)
            {
                var statement = Drops(command) && change.DroppedIndex is { } dropped
                    ? new BsonDocument { { "command", command }, { "droppedIndex", dropped } }
                    : null;
                await database.RunCommandAsync(command, recordStatement: statement, cancellationToken: cancellationToken);

                // A drop that a create follows (a type change or a failed rebuild) has to finish before the name is free; when
                // waiting is on, so does any other drop, so the run doesn't return with the index still listed.
                if (Drops(command) && (change.WaitForDropName is not null || waitForReady))
                {
                    await WaitForGoneAsync(collection, collectionName, command["name"].AsString, waitTimeout, cancellationToken);
                }
            }

            if (waitForReady && change.WaitForName is { } name)
            {
                await WaitForReadyAsync(collection, collectionName, name, change.WaitForDefinition, waitTimeout, cancellationToken);
            }
        }

        if (waitForReady)
        {
            foreach (var name in plan.Pending)
            {
                await WaitForReadyAsync(collection, collectionName, name, null, waitTimeout, cancellationToken);
            }
        }
    }

    private static bool Drops(BsonDocument command) => command.Contains("dropSearchIndex");

    /// <summary>Polls until the named index is no longer listed.</summary>
    private static async Task WaitForGoneAsync(
        IMongoCollection<BsonDocument> collection,
        string collectionName,
        string name,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + waitTimeout;
        while (await LatestAsync(collection, name, cancellationToken) is not null)
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Search index {collectionName}.{name} wasn't dropped within {waitTimeout.TotalSeconds:0} seconds.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private static async Task WaitForReadyAsync(
        IMongoCollection<BsonDocument> collection,
        string collectionName,
        string name,
        BsonDocument? expectedDefinition,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + waitTimeout;
        while (true)
        {
            var index = await LatestAsync(collection, name, cancellationToken);
            var status = index?.GetValue("status", BsonNull.Value) is BsonString s ? s.AsString : null;

            // Check failure first: a FAILED build never becomes ready, so waiting out the timeout would only hide it.
            if (string.Equals(status, "FAILED", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Search index {collectionName}.{name} failed to build (status FAILED).");
            }

            // Only READY with the requested definition means the new definition is serving: during an online update the
            // previous definition stays queryable while the new one builds, so queryable alone would return too early.
            if (index is not null &&
                string.Equals(status, "READY", StringComparison.Ordinal) &&
                index.GetValue("queryable", false).ToBoolean() &&
                DefinitionReady(index, expectedDefinition))
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Search index {collectionName}.{name} wasn't ready within {waitTimeout.TotalSeconds:0} seconds (status {status ?? "unknown"}).");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private static bool DefinitionReady(BsonDocument index, BsonDocument? expectedDefinition) =>
        expectedDefinition is null ||
        SearchIndexPlanner.SameDefinition(index.GetValue("latestDefinition", BsonNull.Value) as BsonDocument, expectedDefinition);

    private static async Task<BsonDocument?> LatestAsync(IMongoCollection<BsonDocument> collection, string name, CancellationToken cancellationToken)
    {
        using var cursor = await collection.SearchIndexes.ListAsync(name, null, cancellationToken);
        var indexes = await cursor.ToListAsync(cancellationToken);
        return indexes.FirstOrDefault(i => i.GetValue("name", "").AsString == name);
    }
}
