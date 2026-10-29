using MongoDB.Bson;
using MongoDB.Driver;

namespace Kevsoft.MigrateMongo;

/// <summary>
/// A collection definition discovered by an <see cref="IMigrationSource"/>.
/// </summary>
public sealed class CollectionDescriptor(
    string name,
    string source,
    string origin,
    Func<IServiceProvider, IMongoDatabase, CollectionDeclaration> declare)
{
    /// <summary>The collection's name.</summary>
    public string Name { get; } = name;

    /// <summary>Kind of source, e.g. <c>csharp</c>.</summary>
    public string Source { get; } = source;

    /// <summary>Where the definition came from, e.g. a type name.</summary>
    public string Origin { get; } = origin;

    /// <summary>
    /// Creates the definition and renders what it declares. Throws if a declaration can't be rendered, e.g. a typed key
    /// whose field has no class map.
    /// </summary>
    public CollectionDeclaration Declare(IServiceProvider services, IMongoDatabase database) => declare(services, database);

    /// <summary>Used by the source generator.</summary>
    public static CollectionDescriptor Create<TDefinition, TDocument>(string name, string origin, Func<IServiceProvider, TDefinition> factory)
        where TDefinition : ICollectionDefinition<TDocument>
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new CollectionDescriptor(name, "csharp", origin, (services, database) =>
        {
            var builder = new CollectionBuilder<TDocument>(name);
            factory(services).Define(builder);
            return CollectionRendering.Render(builder, database.GetCollection<TDocument>(name));
        });
    }
}

/// <summary>A collection definition rendered to BSON, as the server would see it.</summary>
public sealed class CollectionDeclaration
{
    public required string Name { get; init; }

    public required IReadOnlyList<IndexDeclaration> Indexes { get; init; }

    /// <summary>Patterns of index names to leave alone.</summary>
    public IReadOnlyList<string> Ignored { get; init; } = [];

    public IndexPrune? Prune { get; init; }

    public IndexRebuild? OnRebuild { get; init; }
}

/// <summary>One declared index, rendered.</summary>
public sealed class IndexDeclaration
{
    /// <summary>The keys as declared, e.g. <c>{ companyId: 1, status: 1 }</c> or <c>{ title: "text" }</c>.</summary>
    public required BsonDocument Keys { get; init; }

    /// <summary>
    /// The options as <c>createIndexes</c> takes them, e.g. <c>{ unique: true, expireAfterSeconds: 3600 }</c>. Has
    /// <c>name</c> only when one was declared.
    /// </summary>
    public required BsonDocument Options { get; init; }

    public IndexRebuild? OnRebuild { get; init; }
}

internal static class CollectionRendering
{
    public static CollectionDeclaration Render<TDocument>(CollectionBuilder<TDocument> builder, IMongoCollection<TDocument> collection)
    {
        var args = new RenderArgs<TDocument>(collection.DocumentSerializer, collection.Settings.SerializerRegistry);
        var indexes = new List<IndexDeclaration>();
        for (var i = 0; i < builder.Indexes.Declared.Count; i++)
        {
            var declared = builder.Indexes.Declared[i];
            try
            {
                indexes.Add(new IndexDeclaration
                {
                    Keys = declared.Keys.Render(args),
                    Options = IndexOptions(declared.Options, args),
                    OnRebuild = declared.OnRebuild,
                });
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                throw new InvalidOperationException($"Index {i + 1} on '{builder.Name}' couldn't be rendered: {e.Message}", e);
            }
        }

        return new CollectionDeclaration
        {
            Name = builder.Name,
            Indexes = indexes,
            Ignored = builder.Indexes.Ignored.ToList(),
            Prune = builder.Indexes.Prune,
            OnRebuild = builder.Indexes.OnRebuild,
        };
    }

    /// <summary>
    /// Index options under the names <c>createIndexes</c> and <c>listIndexes</c> use, for declarations and for the index
    /// operations <c>context.Track(...)</c> records. False booleans are left out.
    /// </summary>
    internal static BsonDocument IndexOptions<TDocument>(CreateIndexOptions? options, RenderArgs<TDocument> args)
    {
        var document = new BsonDocument();
        if (options is null)
        {
            return document;
        }

        if (!string.IsNullOrEmpty(options.Name))
        {
            document.Add("name", options.Name);
        }

        AddFlag(document, "unique", options.Unique);
        AddFlag(document, "sparse", options.Sparse);
        AddFlag(document, "hidden", options.Hidden);
        if (options.ExpireAfter is { } expireAfter)
        {
            document.Add("expireAfterSeconds", (long)expireAfter.TotalSeconds);
        }

        if (options.Collation is not null)
        {
            document.Add("collation", options.Collation.ToBsonDocument());
        }

        if (options is CreateIndexOptions<TDocument> { PartialFilterExpression: { } partial })
        {
            document.Add("partialFilterExpression", partial.Render(args));
        }

        if (options.Weights is not null)
        {
            document.Add("weights", options.Weights.DeepClone());
        }

        AddIfNotNull(document, "default_language", options.DefaultLanguage);
        AddIfNotNull(document, "language_override", options.LanguageOverride);
        AddIfNotNull(document, "textIndexVersion", options.TextIndexVersion);
        AddIfNotNull(document, "2dsphereIndexVersion", options.SphereIndexVersion);
        AddIfNotNull(document, "bits", options.Bits);
        AddIfNotNull(document, "min", options.Min);
        AddIfNotNull(document, "max", options.Max);
        if (options is CreateIndexOptions<TDocument> { WildcardProjection: { } projection })
        {
            document.Add("wildcardProjection", projection.Render(args));
        }

        if (options.StorageEngine is not null)
        {
            document.Add("storageEngine", options.StorageEngine.DeepClone());
        }

        return document;
    }

    private static void AddFlag(BsonDocument document, string name, bool? value)
    {
        if (value == true)
        {
            document.Add(name, true);
        }
    }

    private static void AddIfNotNull(BsonDocument document, string name, object? value)
    {
        if (value is not null)
        {
            document.Add(name, BsonValue.Create(value));
        }
    }
}
