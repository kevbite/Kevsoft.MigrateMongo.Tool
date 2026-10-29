using System.Diagnostics;
using System.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kevsoft.MigrateMongo;

/// <summary>
/// Runs operations on a collection and records each one (kind, statement, counts and duration) in
/// <see cref="MigrationContext.Results"/>. The migration's transaction session is passed automatically. With a label, the
/// counts are also added to the named results under it.
/// </summary>
/// <typeparam name="T">The collection's document type.</typeparam>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "It wraps an IMongoCollection.")]
public sealed class TrackedCollection<T>
{
    private readonly IClientSessionHandle? _session;
    private readonly MigrationResults _results;
    private readonly bool _captureStatement;

    internal TrackedCollection(IMongoCollection<T> collection, IClientSessionHandle? session, MigrationResults results, string? label, bool captureStatement)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (label is not null)
        {
            MigrationResults.ValidateKey(label);
        }

        Collection = collection;
        _session = session;
        _results = results;
        Label = label;
        _captureStatement = captureStatement && results.CaptureStatements;
    }

    /// <summary>The underlying collection, for operations that don't need recording.</summary>
    public IMongoCollection<T> Collection { get; }

    public string? Label { get; }

    private string Name => Collection.CollectionNamespace.CollectionName;

    public Task<UpdateResult> UpdateOneAsync(Expression<Func<T, bool>> filter, UpdateDefinition<T> update, UpdateOptions? options = null, CancellationToken cancellationToken = default) =>
        UpdateOneAsync(new ExpressionFilterDefinition<T>(filter), update, options, cancellationToken);

    public Task<UpdateResult> UpdateOneAsync(FilterDefinition<T> filter, UpdateDefinition<T> update, UpdateOptions? options = null, CancellationToken cancellationToken = default) =>
        RunAsync(
            "updateOne",
            () => UpdateStatement(filter, update, options),
            () => _session is null
                ? Collection.UpdateOneAsync(filter, update, options, cancellationToken)
                : Collection.UpdateOneAsync(_session, filter, update, options, cancellationToken),
            r => r.IsAcknowledged ? MigrationResults.Counts(r) : null);

    public Task<UpdateResult> UpdateManyAsync(Expression<Func<T, bool>> filter, UpdateDefinition<T> update, UpdateOptions? options = null, CancellationToken cancellationToken = default) =>
        UpdateManyAsync(new ExpressionFilterDefinition<T>(filter), update, options, cancellationToken);

    public Task<UpdateResult> UpdateManyAsync(FilterDefinition<T> filter, UpdateDefinition<T> update, UpdateOptions? options = null, CancellationToken cancellationToken = default) =>
        RunAsync(
            "updateMany",
            () => UpdateStatement(filter, update, options),
            () => _session is null
                ? Collection.UpdateManyAsync(filter, update, options, cancellationToken)
                : Collection.UpdateManyAsync(_session, filter, update, options, cancellationToken),
            r => r.IsAcknowledged ? MigrationResults.Counts(r) : null);

    /// <summary>Replaces a document. Only the filter is recorded, never the replacement document.</summary>
    public Task<ReplaceOneResult> ReplaceOneAsync(Expression<Func<T, bool>> filter, T replacement, ReplaceOptions? options = null, CancellationToken cancellationToken = default) =>
        ReplaceOneAsync(new ExpressionFilterDefinition<T>(filter), replacement, options, cancellationToken);

    /// <summary>Replaces a document. Only the filter is recorded, never the replacement document.</summary>
    public Task<ReplaceOneResult> ReplaceOneAsync(FilterDefinition<T> filter, T replacement, ReplaceOptions? options = null, CancellationToken cancellationToken = default) =>
        RunAsync(
            "replaceOne",
            () => Statement(("filter", Render(filter)), ("options", WriteOptions(options?.IsUpsert ?? false, options?.Hint, options?.Collation))),
            () => _session is null
                ? Collection.ReplaceOneAsync(filter, replacement, options, cancellationToken)
                : Collection.ReplaceOneAsync(_session, filter, replacement, options, cancellationToken),
            r => r.IsAcknowledged ? MigrationResults.Counts(r) : null);

    public Task<DeleteResult> DeleteOneAsync(Expression<Func<T, bool>> filter, DeleteOptions? options = null, CancellationToken cancellationToken = default) =>
        DeleteOneAsync(new ExpressionFilterDefinition<T>(filter), options, cancellationToken);

    public Task<DeleteResult> DeleteOneAsync(FilterDefinition<T> filter, DeleteOptions? options = null, CancellationToken cancellationToken = default) =>
        RunAsync(
            "deleteOne",
            () => Statement(("filter", Render(filter)), ("options", WriteOptions(false, options?.Hint, options?.Collation))),
            () => _session is null
                ? Collection.DeleteOneAsync(filter, options, cancellationToken)
                : Collection.DeleteOneAsync(_session, filter, options, cancellationToken),
            r => r.IsAcknowledged ? MigrationResults.Counts(r) : null);

    public Task<DeleteResult> DeleteManyAsync(Expression<Func<T, bool>> filter, DeleteOptions? options = null, CancellationToken cancellationToken = default) =>
        DeleteManyAsync(new ExpressionFilterDefinition<T>(filter), options, cancellationToken);

    public Task<DeleteResult> DeleteManyAsync(FilterDefinition<T> filter, DeleteOptions? options = null, CancellationToken cancellationToken = default) =>
        RunAsync(
            "deleteMany",
            () => Statement(("filter", Render(filter)), ("options", WriteOptions(false, options?.Hint, options?.Collation))),
            () => _session is null
                ? Collection.DeleteManyAsync(filter, options, cancellationToken)
                : Collection.DeleteManyAsync(_session, filter, options, cancellationToken),
            r => r.IsAcknowledged ? MigrationResults.Counts(r) : null);

    /// <summary>Inserts a document. Only the count is recorded, never the document.</summary>
    public async Task InsertOneAsync(T document, InsertOneOptions? options = null, CancellationToken cancellationToken = default) =>
        await RunAsync(
            "insertOne",
            () => null,
            async () =>
            {
                await (_session is null
                    ? Collection.InsertOneAsync(document, options, cancellationToken)
                    : Collection.InsertOneAsync(_session, document, options, cancellationToken));
                return true;
            },
            _ => new Dictionary<string, long> { ["inserted"] = 1 });

    /// <summary>Inserts documents. Only the count is recorded, never the documents.</summary>
    public async Task InsertManyAsync(IEnumerable<T> documents, InsertManyOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var list = documents as IReadOnlyCollection<T> ?? documents.ToList();
        await RunAsync(
            "insertMany",
            () => null,
            async () =>
            {
                await (_session is null
                    ? Collection.InsertManyAsync(list, options, cancellationToken)
                    : Collection.InsertManyAsync(_session, list, options, cancellationToken));
                return true;
            },
            _ => new Dictionary<string, long> { ["inserted"] = list.Count });
    }

    /// <summary>Runs a bulk write. Each request's kind and filter/update is recorded (inserted documents aren't).</summary>
    public Task<BulkWriteResult<T>> BulkWriteAsync(IEnumerable<WriteModel<T>> requests, BulkWriteOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var list = requests as IReadOnlyCollection<WriteModel<T>> ?? requests.ToList();
        return RunAsync(
            "bulkWrite",
            () => Statement(
                ("requests", new BsonArray(list.Select(RenderRequest))),
                ("options", options is { IsOrdered: false } ? new BsonDocument("ordered", false) : null)),
            () => _session is null
                ? Collection.BulkWriteAsync(list, options, cancellationToken)
                : Collection.BulkWriteAsync(_session, list, options, cancellationToken),
            r => r.IsAcknowledged ? MigrationResults.Counts(r) : null);
    }

    /// <summary>Creates an index and records its keys and options.</summary>
    public Task<string> CreateIndexAsync(CreateIndexModel<T> model, CreateOneIndexOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        return RunAsync(
            "createIndex",
            () => Statement(("keys", Render(model.Keys)), ("options", IndexOptions(model.Options))),
            () => _session is null
                ? Collection.Indexes.CreateOneAsync(model, options, cancellationToken)
                : Collection.Indexes.CreateOneAsync(_session, model, options, cancellationToken),
            _ => new Dictionary<string, long> { ["created"] = 1 });
    }

    /// <summary>
    /// Creates an index from a <c>createIndexes</c> index document (<c>key</c>, <c>name</c> and options, as
    /// <c>listIndexes</c> lists them), and records its keys and options.
    /// </summary>
    public Task<BsonDocument> CreateIndexAsync(BsonDocument index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        var command = new BsonDocument { { "createIndexes", Name }, { "indexes", new BsonArray { index } } };
        var options = new BsonDocument(index.Where(e => e.Name is not ("v" or "key" or "ns")));
        return RunAsync(
            "createIndex",
            () => Statement(("keys", index.GetValue("key", null)), ("options", options.ElementCount > 0 ? options : null)),
            () => _session is null
                ? Collection.Database.RunCommandAsync<BsonDocument>(command, cancellationToken: cancellationToken)
                : Collection.Database.RunCommandAsync<BsonDocument>(_session, command, cancellationToken: cancellationToken),
            _ => new Dictionary<string, long> { ["created"] = 1 });
    }

    /// <summary>Creates indexes and records each one's keys and options.</summary>
    public Task<IEnumerable<string>> CreateIndexesAsync(IEnumerable<CreateIndexModel<T>> models, CreateManyIndexesOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(models);
        var list = models as IReadOnlyCollection<CreateIndexModel<T>> ?? models.ToList();
        return RunAsync(
            "createIndexes",
            () => Statement(("indexes", new BsonArray(list.Select(m => Statement(("keys", Render(m.Keys)), ("options", IndexOptions(m.Options)))!)))),
            () => _session is null
                ? Collection.Indexes.CreateManyAsync(list, options, cancellationToken)
                : Collection.Indexes.CreateManyAsync(_session, list, options, cancellationToken),
            _ => new Dictionary<string, long> { ["created"] = list.Count });
    }

    /// <summary>
    /// Drops an index by name, first recording its full definition (keys and options) so the history shows exactly what
    /// was removed. With <paramref name="ignoreMissing"/>, a missing index or collection records <c>dropped: 0</c>
    /// instead of throwing.
    /// </summary>
    public async Task DropIndexAsync(string name, bool ignoreMissing = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var index = (await ListIndexesAsync(cancellationToken)).FirstOrDefault(i => i.GetValue("name", "").ToString() == name);
        await DropAsync(name, index ?? new BsonDocument("name", name), ignoreMissing, cancellationToken);
    }

    /// <summary>
    /// Drops the index with these keys, first recording its full definition. With <paramref name="ignoreMissing"/>, a
    /// missing index or collection records <c>dropped: 0</c> instead of throwing.
    /// </summary>
    public async Task DropIndexAsync(IndexKeysDefinition<T> keys, bool ignoreMissing = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var rendered = Render(keys) ?? throw new ArgumentException("The index keys couldn't be rendered.", nameof(keys));
        var index = (await ListIndexesAsync(cancellationToken)).FirstOrDefault(i => i.TryGetValue("key", out var key) && key.Equals(rendered));
        if (index is not null)
        {
            await DropAsync(index["name"].AsString, index, ignoreMissing, cancellationToken);
            return;
        }

        // Let the server report that it doesn't exist (or drop it, if it was created since it was listed).
        var command = new BsonDocument { { "dropIndexes", Name }, { "index", rendered } };
        await RunAsync(
            "dropIndex",
            () => Statement(("index", new BsonDocument("keys", rendered))),
            async () =>
            {
                try
                {
                    await (_session is null
                        ? Collection.Database.RunCommandAsync<BsonDocument>(command, cancellationToken: cancellationToken)
                        : Collection.Database.RunCommandAsync<BsonDocument>(_session, command, cancellationToken: cancellationToken));
                    return 1L;
                }
                catch (MongoCommandException e) when (ignoreMissing && IsMissing(e))
                {
                    return 0L;
                }
            },
            dropped => new Dictionary<string, long> { ["dropped"] = dropped });
    }

    /// <summary>Drops every index except <c>_id_</c>, first recording each one's full definition.</summary>
    public async Task DropAllIndexesAsync(CancellationToken cancellationToken = default)
    {
        var indexes = (await ListIndexesAsync(cancellationToken)).Where(i => i.GetValue("name", "").ToString() != "_id_").ToList();
        await RunAsync(
            "dropIndexes",
            () => Statement(("indexes", new BsonArray(indexes.Select(IndexDefinition)))),
            async () =>
            {
                await (_session is null
                    ? Collection.Indexes.DropAllAsync(cancellationToken)
                    : Collection.Indexes.DropAllAsync(_session, cancellationToken));
                return true;
            },
            _ => new Dictionary<string, long> { ["dropped"] = indexes.Count });
    }

    private Task<long> DropAsync(string name, BsonDocument index, bool ignoreMissing, CancellationToken cancellationToken) =>
        RunAsync(
            "dropIndex",
            () => Statement(("index", IndexDefinition(index))),
            async () =>
            {
                try
                {
                    await (_session is null
                        ? Collection.Indexes.DropOneAsync(name, cancellationToken)
                        : Collection.Indexes.DropOneAsync(_session, name, cancellationToken));
                    return 1L;
                }
                catch (MongoCommandException e) when (ignoreMissing && IsMissing(e))
                {
                    return 0L;
                }
            },
            dropped => new Dictionary<string, long> { ["dropped"] = dropped });

    /// <summary>IndexNotFound (27) or NamespaceNotFound (26).</summary>
    private static bool IsMissing(MongoCommandException e) => e.Code is 26 or 27;

    /// <summary>Lists the indexes without the session: <c>listIndexes</c> isn't allowed in a multi-document transaction.</summary>
    private async Task<List<BsonDocument>> ListIndexesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cursor = await Collection.Indexes.ListAsync(cancellationToken);
            return await cursor.ToListAsync(cancellationToken);
        }
        catch (MongoCommandException e) when (e.Code == 26)
        {
            return [];
        }
    }

    /// <summary><c>{ name, keys, options }</c> from a <c>listIndexes</c> entry.</summary>
    private static BsonDocument IndexDefinition(BsonDocument index)
    {
        var definition = new BsonDocument("name", index.GetValue("name", BsonNull.Value));
        if (index.TryGetValue("key", out var keys))
        {
            definition.Add("keys", keys);
        }

        var options = new BsonDocument(index.Where(e => e.Name is not ("v" or "key" or "name" or "ns")));
        if (options.ElementCount > 0)
        {
            definition.Add("options", options);
        }

        return definition;
    }

    private async Task<TResult> RunAsync<TResult>(
        string kind,
        Func<BsonDocument?> statement,
        Func<Task<TResult>> operation,
        Func<TResult, IReadOnlyDictionary<string, long>?> counts)
    {
        var rendered = _captureStatement ? statement() : null;
        var at = _results.Time.GetUtcNow().UtcDateTime;
        var started = Stopwatch.GetTimestamp();
        var result = await operation();
        var elapsed = Stopwatch.GetElapsedTime(started);

        var recordedCounts = counts(result) ?? new Dictionary<string, long>();
        var truncated = rendered is not null && rendered.ToBson().Length > MigrationResults.MaxStatementBytes;
        _results.Record(new TrackedOperation
        {
            Kind = kind,
            Collection = Name,
            Label = Label,
            Statement = truncated ? null : rendered,
            StatementTruncated = truncated,
            Counts = recordedCounts,
            DurationMs = (long)elapsed.TotalMilliseconds,
            At = at,
        });

        if (Label is not null && recordedCounts.Count > 0)
        {
            _results.AddCounts(Label, recordedCounts);
        }

        return result;
    }

    private BsonDocument? UpdateStatement(FilterDefinition<T> filter, UpdateDefinition<T> update, UpdateOptions? options) =>
        Statement(
            ("filter", Render(filter)),
            ("update", Render(update)),
            ("options", WriteOptions(options?.IsUpsert ?? false, options?.Hint, options?.Collation)));

    private BsonValue RenderRequest(WriteModel<T> request) => request switch
    {
        InsertOneModel<T> => new BsonDocument("kind", "insertOne"),
        UpdateOneModel<T> m => Statement(("kind", "updateOne"), ("filter", Render(m.Filter)), ("update", Render(m.Update)), ("options", WriteOptions(m.IsUpsert, m.Hint, m.Collation)))!,
        UpdateManyModel<T> m => Statement(("kind", "updateMany"), ("filter", Render(m.Filter)), ("update", Render(m.Update)), ("options", WriteOptions(m.IsUpsert, m.Hint, m.Collation)))!,
        ReplaceOneModel<T> m => Statement(("kind", "replaceOne"), ("filter", Render(m.Filter)), ("options", WriteOptions(m.IsUpsert, m.Hint, m.Collation)))!,
        DeleteOneModel<T> m => Statement(("kind", "deleteOne"), ("filter", Render(m.Filter)), ("options", WriteOptions(false, m.Hint, m.Collation)))!,
        DeleteManyModel<T> m => Statement(("kind", "deleteMany"), ("filter", Render(m.Filter)), ("options", WriteOptions(false, m.Hint, m.Collation)))!,
        _ => new BsonDocument("kind", request.ModelType.ToString()),
    };

    private RenderArgs<T> Args => new(Collection.DocumentSerializer, Collection.Settings.SerializerRegistry);

    private BsonValue? Render(FilterDefinition<T>? filter) => TryRender(() => filter?.Render(Args));

    private BsonValue? Render(UpdateDefinition<T>? update) => TryRender(() => update?.Render(Args));

    private BsonValue? Render(IndexKeysDefinition<T>? keys) => TryRender(() => keys?.Render(Args));

    /// <summary>A statement that can't be rendered (e.g. an unsupported LINQ expression) isn't worth failing the migration for.</summary>
    private static BsonValue? TryRender(Func<BsonValue?> render)
    {
        try
        {
            return render();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new BsonString($"(couldn't render: {e.Message})");
        }
    }

    private static BsonDocument? WriteOptions(bool upsert, BsonValue? hint, Collation? collation)
    {
        var options = new BsonDocument();
        if (upsert)
        {
            options.Add("upsert", true);
        }

        if (hint is not null)
        {
            options.Add("hint", hint);
        }

        if (collation is not null)
        {
            options.Add("collation", collation.ToBsonDocument());
        }

        return options.ElementCount == 0 ? null : options;
    }

    /// <summary>Recorded under the names <c>createIndexes</c> takes, as collection definitions declare them.</summary>
    private BsonValue? IndexOptions(CreateIndexOptions? options) =>
        options is null ? null : TryRender(() => CollectionRendering.IndexOptions(options, Args) is { ElementCount: > 0 } document ? document : null);

    private static BsonDocument? Statement(params (string Name, BsonValue? Value)[] parts)
    {
        var document = new BsonDocument();
        foreach (var (name, value) in parts)
        {
            if (value is not null)
            {
                document.Add(name, value);
            }
        }

        return document.ElementCount == 0 ? null : document;
    }
}
