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

    /// <summary>The Atlas Search and Vector Search indexes the collection should have.</summary>
    public IReadOnlyList<SearchIndexDeclaration> SearchIndexes { get; init; } = [];

    /// <summary>Patterns of index names to leave alone.</summary>
    public IReadOnlyList<string> Ignored { get; init; } = [];

    /// <summary>Patterns of search index names to leave alone.</summary>
    public IReadOnlyList<string> SearchIgnored { get; init; } = [];

    public IndexPrune? Prune { get; init; }

    /// <summary>What happens to search indexes that aren't declared, unless the config sets its own.</summary>
    public SearchIndexPrune? SearchPrune { get; init; }

    public IndexRebuild? OnRebuild { get; init; }

    /// <summary>
    /// The declared collection options as <c>create</c> takes them, e.g. <c>{ capped: true, size: 1048576 }</c>, with any of
    /// <c>capped</c>, <c>size</c>, <c>max</c>, <c>timeseries</c>, <c>expireAfterSeconds</c>, <c>clusteredIndex</c>,
    /// <c>collation</c> and <c>changeStreamPreAndPostImages</c>. Empty when none are declared.
    /// </summary>
    public BsonDocument Options { get; init; } = [];

    /// <summary>The declared validator, level and action, or null when none of them are declared.</summary>
    public ValidatorDeclaration? Validator { get; init; }

    /// <summary>What happens to options MongoDB can't change on an existing collection, unless the config sets its own.</summary>
    public UnchangeableOption? OnUnchangeable { get; init; }

    /// <summary>Whether the collection may be converted to capped. It can only make the config stricter.</summary>
    public ConvertToCapped? OnConvertToCapped { get; init; }

    /// <summary>Whether the definition declares any collection option or validator, so they're compared with the server's.</summary>
    public bool DeclaresOptions =>
        Options.ElementCount > 0 || Validator is not null || OnUnchangeable is not null || OnConvertToCapped is not null;
}

/// <summary>A declared validator, rendered.</summary>
public sealed class ValidatorDeclaration
{
    /// <summary>The validator as <c>collMod</c> takes it, e.g. <c>{ $jsonSchema: { ... } }</c>; null when only the level or action is declared.</summary>
    public BsonDocument? Validator { get; init; }

    /// <summary><c>strict</c>, <c>moderate</c>, <c>constraint</c> or <c>off</c>, when declared.</summary>
    public string? Level { get; init; }

    /// <summary><c>error</c>, <c>warn</c> or <c>errorAndLog</c>, when declared.</summary>
    public string? Action { get; init; }
}

/// <summary>One declared search index, rendered.</summary>
public sealed class SearchIndexDeclaration
{
    public required string Name { get; init; }

    /// <summary>The <c>createSearchIndexes</c> type: <c>search</c> or <c>vectorSearch</c>.</summary>
    public required string Type { get; init; }

    /// <summary>The index definition, as <c>createSearchIndexes</c> takes it.</summary>
    public required BsonDocument Definition { get; init; }
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
            SearchIndexes = SearchIndexes(builder),
            Ignored = builder.Indexes.Ignored.ToList(),
            SearchIgnored = builder.SearchIndexes.Ignored.ToList(),
            Prune = builder.Indexes.Prune,
            SearchPrune = builder.SearchIndexes.Prune,
            OnRebuild = builder.Indexes.OnRebuild,
            Options = CollectionOptions(builder.Name, builder.Options, args),
            Validator = Validator(builder.Validator),
            OnUnchangeable = builder.Options.OnUnchangeable,
            OnConvertToCapped = builder.Options.OnConvertToCapped,
        };
    }

    private static BsonDocument CollectionOptions<TDocument>(string name, CollectionOptionsBuilder<TDocument> options, RenderArgs<TDocument> args)
    {
        var capped = options.CappedOptions;
        var timeSeries = options.TimeSeriesOptions;
        var clustered = options.ClusteredOptions;
        if (capped is not null && timeSeries is not null)
        {
            throw new InvalidOperationException($"'{name}' can't be both capped and a time series collection.");
        }

        if (capped is not null && clustered is not null)
        {
            throw new InvalidOperationException($"'{name}' can't be both capped and clustered.");
        }

        if (timeSeries is not null && clustered is not null)
        {
            throw new InvalidOperationException($"'{name}' can't be both a time series and a clustered collection.");
        }

        if (options.TimeSeriesExpireAfter is not null && timeSeries is null)
        {
            throw new InvalidOperationException(
                $"'{name}' declares ExpireAfter without TimeSeries; for a clustered collection pass expireAfter to Clustered.");
        }

        var document = new BsonDocument();
        if (capped is not null)
        {
            document.Add("capped", true);
            document.Add("size", capped.MaxSize);
            if (capped.MaxDocuments is { } max)
            {
                document.Add("max", max);
            }
        }

        if (timeSeries is not null)
        {
            if (timeSeries.BucketMaxSpan is { } span && span != timeSeries.BucketRounding)
            {
                throw new InvalidOperationException($"'{name}' declares a time series bucketMaxSpan and bucketRounding that differ; MongoDB requires them to be equal.");
            }

            string time, meta;
            try
            {
                time = timeSeries.Time.Render(args).FieldName;
                meta = timeSeries.Meta?.Render(args).FieldName ?? "";
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                throw new InvalidOperationException($"The time series fields on '{name}' couldn't be rendered: {e.Message}", e);
            }

            var series = new BsonDocument("timeField", time);
            if (meta.Length > 0)
            {
                series.Add("metaField", meta);
            }

            if (timeSeries.Granularity is { } granularity)
            {
                series.Add("granularity", granularity.ToString().ToLowerInvariant());
            }

            if (timeSeries.BucketMaxSpan is { } maxSpan)
            {
                series.Add("bucketMaxSpanSeconds", (int)maxSpan.TotalSeconds);
                series.Add("bucketRoundingSeconds", (int)timeSeries.BucketRounding!.Value.TotalSeconds);
            }

            document.Add("timeseries", series);
            if (options.TimeSeriesExpireAfter is { } expireAfter)
            {
                document.Add("expireAfterSeconds", (long)expireAfter.TotalSeconds);
            }
        }

        if (clustered is not null)
        {
            var index = new BsonDocument { { "key", new BsonDocument("_id", 1) }, { "unique", true } };
            if (clustered.Name is not null)
            {
                index.Add("name", clustered.Name);
            }

            document.Add("clusteredIndex", index);
            if (clustered.ExpireAfter is { } expireAfter)
            {
                document.Add("expireAfterSeconds", (long)expireAfter.TotalSeconds);
            }
        }

        if (options.DefaultCollation is not null)
        {
            document.Add("collation", options.DefaultCollation.ToBsonDocument());
        }

        if (options.PreAndPostImages is { } enabled)
        {
            document.Add("changeStreamPreAndPostImages", new BsonDocument("enabled", enabled));
        }

        return document;
    }

    private static ValidatorDeclaration? Validator(ValidatorBuilder validator)
    {
        if (validator.Schema is null && validator.DeclaredLevel is null && validator.DeclaredAction is null)
        {
            return null;
        }

        return new ValidatorDeclaration
        {
            Validator = validator.Schema is null ? null : new BsonDocument("$jsonSchema", validator.Schema.DeepClone()),
            Level = validator.DeclaredLevel?.ToString().ToLowerInvariant(),
            Action = validator.DeclaredAction switch
            {
                null => null,
                ValidationAction.ErrorAndLog => "errorAndLog",
                var action => action.Value.ToString().ToLowerInvariant(),
            },
        };
    }

    private static List<SearchIndexDeclaration> SearchIndexes<TDocument>(CollectionBuilder<TDocument> builder)
    {
        var declared = builder.SearchIndexes.Declared;
        var searchIndexes = new List<SearchIndexDeclaration>(declared.Count);
        for (var i = 0; i < declared.Count; i++)
        {
            var search = declared[i];
            searchIndexes.Add(new SearchIndexDeclaration
            {
                Name = search.Name,
                Type = search.Type == SearchIndexType.VectorSearch ? "vectorSearch" : "search",
                Definition = (BsonDocument)search.Definition.DeepClone(),
            });
        }

        return searchIndexes;
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
