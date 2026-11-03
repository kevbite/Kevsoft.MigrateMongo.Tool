using MongoDB.Bson;

namespace Kevsoft.MigrateMongo;

/// <summary>The kind of Atlas search index.</summary>
public enum SearchIndexType
{
    /// <summary>An Atlas Search index (<c>type: search</c>), queried with <c>$search</c>.</summary>
    Search,

    /// <summary>An Atlas Vector Search index (<c>type: vectorSearch</c>), queried with <c>$vectorSearch</c>.</summary>
    VectorSearch,
}

/// <summary>What happens to a search index that's on the server but not declared.</summary>
public enum SearchIndexPrune
{
    /// <summary>Left alone, with a warning.</summary>
    Report,

    /// <summary>Dropped. Its full definition is recorded first.</summary>
    Drop,
}

/// <summary>
/// The Atlas Search and Vector Search indexes a collection should have. Search indexes are identified by name (unique per
/// collection) and their definition is changed online with <c>updateSearchIndex</c>, so there's no drop-and-rebuild gap.
/// They're only available on Atlas and Atlas Local.
/// </summary>
public sealed class SearchIndexesBuilder<TDocument>
{
    private readonly List<DeclaredSearchIndex> _indexes = [];
    private readonly List<string> _ignored = [];

    internal SearchIndexesBuilder()
    {
    }

    /// <summary>What happens to search indexes that aren't declared. Overrides <c>searchIndexes.prune</c>.</summary>
    public SearchIndexPrune? Prune { get; set; }

    internal IReadOnlyList<DeclaredSearchIndex> Declared => _indexes;

    internal IReadOnlyList<string> Ignored => _ignored;

    /// <summary>Declares an Atlas Search index (<c>type: search</c>).</summary>
    /// <param name="name">The index name, unique per collection.</param>
    /// <param name="definition">The index definition, e.g. <c>{ mappings: { dynamic: true } }</c>.</param>
    public SearchIndexesBuilder<TDocument> Add(string name, BsonDocument definition) =>
        Add(name, SearchIndexType.Search, definition);

    /// <inheritdoc cref="Add(string, BsonDocument)"/>
    public SearchIndexesBuilder<TDocument> Add(string name, string definition) =>
        Add(name, SearchIndexType.Search, Parse(definition));

    /// <summary>Declares an Atlas Vector Search index (<c>type: vectorSearch</c>).</summary>
    /// <param name="name">The index name, unique per collection.</param>
    /// <param name="definition">The index definition, e.g. <c>{ fields: [ { type: "vector", path: "embedding", numDimensions: 1536, similarity: "cosine" } ] }</c>.</param>
    public SearchIndexesBuilder<TDocument> AddVector(string name, BsonDocument definition) =>
        Add(name, SearchIndexType.VectorSearch, definition);

    /// <inheritdoc cref="AddVector(string, BsonDocument)"/>
    public SearchIndexesBuilder<TDocument> AddVector(string name, string definition) =>
        Add(name, SearchIndexType.VectorSearch, Parse(definition));

    /// <summary>
    /// Leaves search indexes whose names match <paramref name="pattern"/> alone: they're never pruned. <c>*</c> matches any
    /// run of characters, e.g. <c>"adhoc_*"</c>.
    /// </summary>
    public SearchIndexesBuilder<TDocument> Ignore(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        _ignored.Add(pattern);
        return this;
    }

    private SearchIndexesBuilder<TDocument> Add(string name, SearchIndexType type, BsonDocument definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(definition);
        _indexes.Add(new DeclaredSearchIndex(name, type, definition));
        return this;
    }

    private static BsonDocument Parse(string definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition);
        return BsonDocument.Parse(definition);
    }
}

internal sealed record DeclaredSearchIndex(string Name, SearchIndexType Type, BsonDocument Definition);
