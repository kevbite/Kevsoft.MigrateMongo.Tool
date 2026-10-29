using MongoDB.Driver;

namespace Kevsoft.MigrateMongo;

/// <summary>What happens to an index that's on the server but not declared.</summary>
public enum IndexPrune
{
    /// <summary>Left alone, with a warning.</summary>
    Report,

    /// <summary>Hidden from the query planner, then dropped once it has been hidden for <c>indexes.dropHiddenAfter</c>.</summary>
    Hide,

    /// <summary>Dropped straight away. Its full definition is recorded first.</summary>
    Drop,
}

/// <summary>
/// Whether an index may be dropped and created again, leaving the collection without it while it builds. That's needed
/// when keys or options change that MongoDB can't change in place.
/// </summary>
public enum IndexRebuild
{
    Allow,

    /// <summary>Fail instead. The run stops before anything changes, and <c>--allow-rebuild</c> approves one index.</summary>
    Fail,
}

/// <summary>Collects a <see cref="ICollectionDefinition{TDocument}"/>'s declarations.</summary>
public sealed class CollectionBuilder<TDocument>
{
    internal CollectionBuilder(string name) => Name = name;

    public string Name { get; }

    public IndexesBuilder<TDocument> Indexes { get; } = new();
}

/// <summary>The indexes a collection should have.</summary>
public sealed class IndexesBuilder<TDocument>
{
    private readonly List<DeclaredIndex<TDocument>> _indexes = [];
    private readonly List<string> _ignored = [];

    internal IndexesBuilder()
    {
    }

    /// <summary>What happens to indexes that aren't declared. Overrides <c>indexes.prune</c>.</summary>
    public IndexPrune? Prune { get; set; }

    /// <summary>
    /// <see cref="IndexRebuild.Fail"/> stops any index here from being rebuilt. It can only make <c>indexes.onRebuild</c>
    /// stricter: <see cref="IndexRebuild.Allow"/> doesn't override a config that says <c>fail</c>.
    /// </summary>
    public IndexRebuild? OnRebuild { get; set; }

    internal IReadOnlyList<DeclaredIndex<TDocument>> Declared => _indexes;

    internal IReadOnlyList<string> Ignored => _ignored;

    /// <summary>
    /// Declares an index. Without a name in <paramref name="options"/>, it matches an existing index with the same keys,
    /// collation and partial filter whatever that one is called, and a new one gets the default name (e.g. <c>status_1</c>).
    /// </summary>
    /// <param name="keys">The index keys.</param>
    /// <param name="options">Options such as <c>Unique</c>, <c>ExpireAfter</c>, <c>Collation</c> or <c>PartialFilterExpression</c>.</param>
    /// <param name="onRebuild"><see cref="IndexRebuild.Fail"/> stops this index from being rebuilt.</param>
    public IndexesBuilder<TDocument> Add(IndexKeysDefinition<TDocument> keys, CreateIndexOptions<TDocument>? options = null, IndexRebuild? onRebuild = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _indexes.Add(new DeclaredIndex<TDocument>(keys, options, onRebuild));
        return this;
    }

    /// <inheritdoc cref="Add(IndexKeysDefinition{TDocument}, CreateIndexOptions{TDocument}?, IndexRebuild?)"/>
    public IndexesBuilder<TDocument> Add(
        Func<IndexKeysDefinitionBuilder<TDocument>, IndexKeysDefinition<TDocument>> keys,
        CreateIndexOptions<TDocument>? options = null,
        IndexRebuild? onRebuild = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return Add(keys(Builders<TDocument>.IndexKeys), options, onRebuild);
    }

    /// <summary>
    /// Leaves indexes whose names match <paramref name="pattern"/> alone: they're never pruned. <c>*</c> matches any run of
    /// characters, e.g. <c>"adhoc_*"</c>.
    /// </summary>
    public IndexesBuilder<TDocument> Ignore(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        _ignored.Add(pattern);
        return this;
    }
}

internal sealed record DeclaredIndex<TDocument>(IndexKeysDefinition<TDocument> Keys, CreateIndexOptions<TDocument>? Options, IndexRebuild? OnRebuild);
