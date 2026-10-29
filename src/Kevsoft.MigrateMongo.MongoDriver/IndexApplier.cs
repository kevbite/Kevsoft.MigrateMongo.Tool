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
                    await CreateAsync(collection, change.Create!, cancellationToken);
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
                    await RebuildAsync(collection, database, collectionName, change, cancellationToken);
                    break;
                case IndexActions.Drop:
                    await collection.DropIndexAsync(change.DropName!, cancellationToken: cancellationToken);
                    break;
            }
        }
    }

    private static Task<string> CreateAsync(TrackedCollection<BsonDocument> collection, BsonDocument spec, CancellationToken cancellationToken) =>
        collection.CreateIndexAsync(new CreateIndexModel<BsonDocument>(spec["key"].AsBsonDocument, Options(spec)), cancellationToken: cancellationToken);

    private static Task<BsonDocument> CollModAsync(TrackedDatabase database, string collection, BsonDocument index, CancellationToken cancellationToken) =>
        database.RunCommandAsync(new BsonDocument { { "collMod", collection }, { "index", index } }, cancellationToken: cancellationToken);

    /// <summary>
    /// <c>prepareUnique</c> first (new duplicates are rejected), then <c>unique</c>, which fails if duplicates already exist.
    /// On failure the index is left as it was.
    /// </summary>
    private static async Task ConvertToUniqueAsync(TrackedDatabase database, string collection, string index, CancellationToken cancellationToken)
    {
        await CollModAsync(database, collection, new BsonDocument { { "name", index }, { "prepareUnique", true } }, cancellationToken);
        try
        {
            await CollModAsync(database, collection, new BsonDocument { { "name", index }, { "unique", true } }, cancellationToken);
        }
        catch (MongoCommandException)
        {
            await CollModAsync(database, collection, new BsonDocument { { "name", index }, { "prepareUnique", false } }, CancellationToken.None);
            throw;
        }
    }

    /// <summary>Drops the index and creates the declared one. If creating fails, the dropped index is created again.</summary>
    private static async Task RebuildAsync(
        TrackedCollection<BsonDocument> collection,
        TrackedDatabase database,
        string collectionName,
        PlannedChange change,
        CancellationToken cancellationToken)
    {
        await collection.DropIndexAsync(change.DropName!, cancellationToken: cancellationToken);
        try
        {
            await CreateAsync(collection, change.Create!, cancellationToken);
        }
        catch (Exception e) when (e is MongoException or TimeoutException)
        {
            var restore = new BsonDocument(change.Existing!.Where(element => element.Name is not ("v" or "ns")));
            try
            {
                await database.RunCommandAsync(
                    new BsonDocument { { "createIndexes", collectionName }, { "indexes", new BsonArray { restore } } },
                    cancellationToken: CancellationToken.None);
            }
            catch (MongoException restoreError)
            {
                throw new InvalidOperationException(
                    $"Rebuilding {collectionName}.{change.DropName} failed ({e.Message}), and restoring the previous index failed too ({restoreError.Message}).", e);
            }

            throw new InvalidOperationException($"Rebuilding {collectionName}.{change.DropName} failed ({e.Message}); the previous index was restored.", e);
        }
    }

    /// <summary>The options of a <c>createIndexes</c> index document, as the driver takes them.</summary>
    internal static CreateIndexOptions<BsonDocument> Options(BsonDocument spec)
    {
        var options = new CreateIndexOptions<BsonDocument> { Name = spec["name"].AsString };
        foreach (var element in spec.Where(e => e.Name is not ("key" or "name")))
        {
            var value = element.Value;
            switch (element.Name)
            {
                case "unique":
                    options.Unique = value.ToBoolean();
                    break;
                case "sparse":
                    options.Sparse = value.ToBoolean();
                    break;
                case "hidden":
                    options.Hidden = value.ToBoolean();
                    break;
                case "expireAfterSeconds":
                    options.ExpireAfter = TimeSpan.FromSeconds(value.ToDouble());
                    break;
                case "collation":
                    options.Collation = Collation.FromBsonDocument(value.AsBsonDocument);
                    break;
                case "partialFilterExpression":
                    options.PartialFilterExpression = value.AsBsonDocument;
                    break;
                case "weights":
                    options.Weights = value.AsBsonDocument;
                    break;
                case "default_language":
                    options.DefaultLanguage = value.AsString;
                    break;
                case "language_override":
                    options.LanguageOverride = value.AsString;
                    break;
                case "textIndexVersion":
                    options.TextIndexVersion = value.ToInt32();
                    break;
                case "2dsphereIndexVersion":
                    options.SphereIndexVersion = value.ToInt32();
                    break;
                case "bits":
                    options.Bits = value.ToInt32();
                    break;
                case "min":
                    options.Min = value.ToDouble();
                    break;
                case "max":
                    options.Max = value.ToDouble();
                    break;
                case "wildcardProjection":
                    options.WildcardProjection = value.AsBsonDocument;
                    break;
                case "storageEngine":
                    options.StorageEngine = value.AsBsonDocument;
                    break;
                default:
                    throw new InvalidOperationException($"Index option '{element.Name}' isn't supported.");
            }
        }

        return options;
    }
}
