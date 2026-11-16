using Kevsoft.MigrateMongo.Protocol;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kevsoft.MigrateMongo.MongoDriver;

/// <summary>
/// Carries out a <see cref="CollectionOptionsPlan"/> through <c>context.Track(...)</c>, so every <c>create</c>,
/// <c>collMod</c> and <c>convertToCapped</c> is recorded, with the server's previous values alongside each change.
/// </summary>
internal static class CollectionOptionsApplier
{
    public static async Task ApplyAsync(MigrationContext context, CollectionOptionsPlan plan, CancellationToken cancellationToken)
    {
        var database = context.Track(context.Database);
        foreach (var change in plan.Changes)
        {
            // Recreating a view drops then creates, which can't be done in place and leaves nothing behind if it fails, so it
            // has its own recovery path rather than the straight run-each-command loop.
            if (change.Info.Action == IndexActions.RecreateView)
            {
                await RecreateViewAsync(database, change, cancellationToken);
                continue;
            }

            foreach (var command in change.Commands)
            {
                var statement = change.Previous is { } previous
                    ? new BsonDocument { { "command", command }, { "previous", previous } }
                    : null;
                await database.RunCommandAsync(command, recordStatement: statement, cancellationToken: cancellationToken);
            }
        }
    }

    /// <summary>
    /// Drops the view and creates it with the declared definition (a view's collation can't be changed in place). The drop
    /// isn't cancellable, so once it's sent the view is known to be gone: if creating the new definition fails or is cancelled,
    /// the previous definition is created again with a non-cancelled token, so a working view is never left missing.
    /// </summary>
    private static async Task RecreateViewAsync(TrackedDatabase database, CollectionOptionsChange change, CancellationToken cancellationToken)
    {
        var drop = change.Commands[0];
        var create = change.Commands[1];
        var name = drop["drop"].AsString;

        cancellationToken.ThrowIfCancellationRequested();
        var dropStatement = change.Previous is { } previous
            ? new BsonDocument { { "command", drop }, { "previous", previous } }
            : null;
        await database.RunCommandAsync(drop, recordStatement: dropStatement, cancellationToken: CancellationToken.None);
        try
        {
            await database.RunCommandAsync(create, cancellationToken: cancellationToken);
        }
        catch (Exception e)
        {
            try
            {
                await database.RunCommandAsync(RestoreView(name, change.Previous!), cancellationToken: CancellationToken.None);
            }
            catch (Exception restoreError) when (restoreError is MongoException or TimeoutException)
            {
                throw new InvalidOperationException(
                    $"Recreating view {name} failed ({e.Message}), and restoring the previous view failed too ({restoreError.Message}).", e);
            }

            throw new InvalidOperationException($"Recreating view {name} failed ({e.Message}); the previous view was restored.", e);
        }
    }

    /// <summary>A <c>create</c> that rebuilds the view as it was, from the <c>viewOn</c>, pipeline and collation captured before the drop.</summary>
    private static BsonDocument RestoreView(string name, BsonDocument previous)
    {
        var create = new BsonDocument
        {
            { "create", name },
            { "viewOn", previous.GetValue("viewOn", "") },
            { "pipeline", previous.GetValue("pipeline", new BsonArray()) },
        };
        // The server reports a collation with an ICU version that create rejects, so restore every other field of it.
        if (previous.GetValue("collation", null) is BsonDocument collation)
        {
            create["collation"] = new BsonDocument(collation.Where(e => e.Name != "version"));
        }

        return create;
    }
}
