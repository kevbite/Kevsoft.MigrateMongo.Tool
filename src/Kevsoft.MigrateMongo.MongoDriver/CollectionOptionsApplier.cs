using MongoDB.Bson;

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
            foreach (var command in change.Commands)
            {
                var statement = change.Previous is { } previous
                    ? new BsonDocument { { "command", command }, { "previous", previous } }
                    : null;
                await database.RunCommandAsync(command, recordStatement: statement, cancellationToken: cancellationToken);
            }
        }
    }
}
