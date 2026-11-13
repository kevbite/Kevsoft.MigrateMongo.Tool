using System.Security.Cryptography;
using System.Text;
using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.Protocol;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kevsoft.MigrateMongo.MongoDriver;

public sealed class MongoMigrationExecutor(
    IMongoClient client,
    IMongoDatabase database,
    IServiceProvider services,
    ILoggerFactory loggerFactory,
    string? stateCollection = null) : IMigrationExecutor
{
    /// <summary>MongoDB 6.0, which added converting an index to unique with <c>collMod</c>.</summary>
    private const int UniqueConversionWireVersion = 17;

    /// <summary>MongoDB 5.1, which added making a non-TTL index a TTL one with <c>collMod</c>.</summary>
    private const int TtlConversionWireVersion = 14;

    /// <summary>
    /// What <c>$listSearchIndexes</c> fails with on a deployment without search: <c>SearchNotEnabled</c> (8.0+), "only allowed
    /// on MongoDB Atlas" (7.0), an unrecognised stage (before 6.0.7), or the command not existing or not being supported.
    /// </summary>
    private static readonly HashSet<int> SearchUnsupportedCodes = [31082, 6047401, 40324, 59, 115];

    private int? _maxWireVersion;

    /// <summary>A collection name that doesn't exist, to explain a view's collation against.</summary>
    private const string CollationProbe = "__migrate_mongo_collation_probe";

    public async Task<CollectionPlan> PlanUndeclaredAsync(UndeclaredPolicy policy, CancellationToken cancellationToken)
    {
        var (_, changes) = await PlanUndeclaredChangesAsync(policy, cancellationToken);
        return new CollectionPlan { Changes = changes };
    }

    public async Task<UndeclaredApplyResult> ApplyUndeclaredAsync(UndeclaredPolicy policy, MigrationRunInfo run, OutputOptions options, CancellationToken cancellationToken)
    {
        var (entries, changes) = await PlanUndeclaredChangesAsync(policy, cancellationToken);
        var result = new UndeclaredApplyResult { Plan = new CollectionPlan { Changes = [] } };
        foreach (var change in changes)
        {
            if (change.Action is not (IndexActions.DropCollection or IndexActions.DropView))
            {
                result.Plan.Changes.Add(change);
                continue;
            }

            var name = change.Index;
            var entry = entries.Single(e => e["name"].AsString == name);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var results = new MigrationResults(options.CaptureStatements);
            var context = new MigrationContext(CollectionsDatabase, null, loggerFactory.CreateLogger($"Collection.{name}"), services, run, results);
            try
            {
                var statement = new BsonDocument { { "command", new BsonDocument("drop", name) }, { "droppedCollection", entry } };
                if (change.Action == IndexActions.DropCollection)
                {
                    // The plan counted roughly; a document written since keeps the collection.
                    var collection = CollectionsDatabase.GetCollection<BsonDocument>(name);
                    var count = await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, new CountOptions { Limit = 1 }, cancellationToken);
                    if (count > 0)
                    {
                        result.Plan.Changes.Add(new IndexChangeInfo
                        {
                            Action = IndexActions.Keep,
                            Index = name,
                            Detail = "not declared",
                            Warning = $"{name} isn't declared but isn't empty, so it isn't dropped.",
                        });
                        continue;
                    }

                    using var cursor = await collection.Indexes.ListAsync(cancellationToken);
                    statement["indexes"] = new BsonArray(await cursor.ToListAsync(cancellationToken));
                }

                await context.Track(context.Database).RunCommandAsync(new BsonDocument("drop", name), recordStatement: statement, cancellationToken: cancellationToken);
                result.Plan.Changes.Add(change);
                result.Drops.Add(new UndeclaredDrop
                {
                    Name = name,
                    Change = change,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Output = MigrationOutputMapper.From(results, options.MaxResultsBytes, out _),
                });
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                result.Drops.Add(new UndeclaredDrop
                {
                    Name = name,
                    Change = change,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Output = MigrationOutputMapper.From(results, options.MaxResultsBytes, out _),
                    Error = e.Message,
                });
            }
        }

        return result;
    }

    private async Task<(List<BsonDocument> Entries, List<IndexChangeInfo> Changes)> PlanUndeclaredChangesAsync(UndeclaredPolicy policy, CancellationToken cancellationToken)
    {
        if (policy.Prune is not ("report" or "drop"))
        {
            return ([], []);
        }

        using var cursor = await CollectionsDatabase.ListCollectionsAsync(cancellationToken: cancellationToken);
        var entries = UndeclaredPlanner.Undeclared(await cursor.ToListAsync(cancellationToken), policy, stateCollection);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in entries.Where(e => !UndeclaredPlanner.IsView(e)))
        {
            var name = entry["name"].AsString;
            counts[name] = await CollectionsDatabase.GetCollection<BsonDocument>(name).EstimatedDocumentCountAsync(cancellationToken: cancellationToken);
        }

        return (entries, UndeclaredPlanner.Plan(entries, policy.Prune, counts));
    }

    // Index changes are planned from the primary's view and written with majority, whatever the connection string says;
    // migrations keep the user's settings.
    private IMongoDatabase CollectionsDatabase =>
        database.WithReadPreference(ReadPreference.Primary).WithWriteConcern(WriteConcern.WMajority);

    public async Task<CollectionPlan> PlanCollectionAsync(CollectionDescriptor collection, CollectionPolicy policy, CancellationToken cancellationToken)
    {
        var (options, index, search) = await PlanAllAsync(collection, policy, cancellationToken);
        return Merge(options, index, search);
    }

    public async Task<CollectionApplyResult> ApplyCollectionAsync(
        CollectionDescriptor collection,
        CollectionPolicy policy,
        MigrationRunInfo run,
        OutputOptions options,
        CancellationToken cancellationToken)
    {
        var (collectionOptions, index, search) = await PlanAllAsync(collection, policy, cancellationToken);
        var plan = Merge(collectionOptions, index, search);
        if (plan.Invalid || plan.Problems.Count > 0)
        {
            return new CollectionApplyResult { Plan = plan, Applied = false };
        }

        var results = new MigrationResults(options.CaptureStatements);
        var context = new MigrationContext(CollectionsDatabase, null, loggerFactory.CreateLogger($"Collection.{collection.Name}"), services, run, results);
        try
        {
            if (collectionOptions is not null)
            {
                await CollectionOptionsApplier.ApplyAsync(context, collectionOptions, cancellationToken);

                // Creating the collection sets the default collation its indexes inherit, and convertToCapped keeps only _id_,
                // so the indexes are planned again against the collection as it is now.
                if (collectionOptions.Changes.Any(c => c.Info.Action is IndexActions.CreateCollection or IndexActions.ConvertToCapped))
                {
                    var declaration = collection.Declare(services, database);
                    var existing = await ListCollectionAsync(declaration.Name, cancellationToken);
                    index = await PlanIndexesAsync(declaration, existing, policy, cancellationToken);
                    search = await PlanSearchAsync(declaration, policy, cancellationToken);
                    plan = Merge(collectionOptions, index, search);
                    if (plan.Problems.Count > 0)
                    {
                        throw new InvalidOperationException(string.Join(" ", plan.Problems));
                    }
                }
            }

            await IndexApplier.ApplyAsync(context, collection.Name, index, cancellationToken);
            if (search is not null)
            {
                await SearchIndexApplier.ApplyAsync(context, collection.Name, search, policy.SearchWaitForReady, policy.SearchWaitTimeout, cancellationToken);
            }
        }
        catch (Exception e)
        {
            throw new MigrationExecutionException(MigrationOutputMapper.From(results, options.MaxResultsBytes, out _), e);
        }

        return new CollectionApplyResult
        {
            Plan = plan,
            Applied = true,
            Output = MigrationOutputMapper.From(results, options.MaxResultsBytes, out _),
        };
    }

    /// <inheritdoc cref="Merge(CollectionOptionsPlan?, IndexPlan, SearchIndexPlan?)"/>
    internal static CollectionPlan Merge(IndexPlan index, SearchIndexPlan? search) => Merge(null, index, search);

    /// <summary>
    /// Combines the collection options plan, the regular-index plan and the optional search-index plan into one
    /// <see cref="CollectionPlan"/>, in the order they're applied. The declaration hash is the index hash alone unless search
    /// indexes, options or a validator are declared, so collections with none keep the exact hash they recorded before those
    /// were supported.
    /// </summary>
    internal static CollectionPlan Merge(CollectionOptionsPlan? options, IndexPlan index, SearchIndexPlan? search)
    {
        var changes = new List<IndexChangeInfo>();
        var problems = new List<string>();
        if (options is not null)
        {
            changes.AddRange(options.Changes.Select(c => c.Info));
            problems.AddRange(options.Problems);
        }

        changes.AddRange(index.Changes.Select(c => c.Info));
        problems.AddRange(index.Problems);
        var indexes = new List<IndexInfo>(index.Indexes);
        if (search is not null)
        {
            changes.AddRange(search.Changes.Select(c => c.Info));
            problems.AddRange(search.Problems);
            indexes.AddRange(search.Indexes);
        }

        var invalid = index.Invalid || (search?.Invalid ?? false);
        var declaration = search is null || invalid ? index.Declaration : CombineDeclaration(index.Declaration, search.Declaration);
        if (!invalid && options is { ViewDefinition: true })
        {
            // A view definition: its hash is the view's alone.
            declaration = options.Declaration;
        }
        else if (!invalid && options is { Declaration.Length: > 0 })
        {
            declaration = CombineDeclaration(declaration, options.Declaration);
        }

        return new CollectionPlan
        {
            Declaration = declaration,
            Changes = changes,
            Problems = problems,
            Invalid = invalid,
            Indexes = indexes,
            Hidden = new Dictionary<string, DateTime>(index.Hidden, StringComparer.Ordinal),
            Pending = search?.Pending.ToList() ?? [],
            Options = options?.Existing is { } existing ? MigrationOutputMapper.ToJson(existing) : null,
        };
    }

    private static string CombineDeclaration(string first, string second) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(first + "\n" + second))).ToLowerInvariant();

    private async Task<(CollectionOptionsPlan? Options, IndexPlan Index, SearchIndexPlan? Search)> PlanAllAsync(
        CollectionDescriptor collection,
        CollectionPolicy policy,
        CancellationToken cancellationToken)
    {
        CollectionDeclaration declaration;
        try
        {
            declaration = collection.Declare(services, database);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (null, IndexPlan.InvalidPlan($"{collection.Name}: {e.Message}"), null);
        }

        var existing = await ListCollectionAsync(declaration.Name, cancellationToken);
        if (declaration.View is { } view)
        {
            BsonDocument? filled = null;
            if (existing is not null && view.Collation is { } collation && collation.GetValue("locale", "simple") != "simple")
            {
                // A view won't explain a query with a collation other than its own, so a name that doesn't exist is used.
                filled = await ExplainCollationAsync(CollationProbe, collation, cancellationToken);
            }

            return (ViewPlanner.Plan(declaration.Name, view, existing, filled), new IndexPlan(), null);
        }

        var options = await PlanOptionsAsync(declaration, existing, policy, cancellationToken);
        if (options.IsView)
        {
            // A view has no indexes to plan.
            return (options, new IndexPlan(), null);
        }

        var index = await PlanIndexesAsync(declaration, existing, policy, cancellationToken);
        var search = await PlanSearchAsync(declaration, policy, cancellationToken);
        return (options, index, search);
    }

    /// <summary>The collection's <c>listCollections</c> entry, or null when it doesn't exist.</summary>
    private async Task<BsonDocument?> ListCollectionAsync(string name, CancellationToken cancellationToken)
    {
        using var cursor = await CollectionsDatabase.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", name) }, cancellationToken);
        return await cursor.FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<CollectionOptionsPlan> PlanOptionsAsync(
        CollectionDeclaration declaration,
        BsonDocument? existing,
        CollectionPolicy policy,
        CancellationToken cancellationToken)
    {
        var wireVersion = await MaxWireVersionAsync(cancellationToken);
        BsonDocument? filledCollation = null;
        if (existing is not null && declaration.Options.GetValue("collation", null) is BsonDocument collation && collation.GetValue("locale", "simple") != "simple")
        {
            filledCollation = await ExplainCollationAsync(declaration.Name, collation, cancellationToken);
        }

        InvalidDocumentCount? invalid = null;
        if (CollectionOptionsPlanner.ValidatorToCount(declaration, existing, policy) is { } validator)
        {
            invalid = await CountInvalidAsync(declaration.Name, validator, cancellationToken);
        }

        return CollectionOptionsPlanner.Plan(declaration, existing, policy, wireVersion, filledCollation, invalid);
    }

    /// <summary>The existing documents that fail a validator: <c>countDocuments({ $nor: [validator] })</c>, and a few <c>_id</c>s.</summary>
    private async Task<InvalidDocumentCount> CountInvalidAsync(string name, BsonDocument validator, CancellationToken cancellationToken)
    {
        var collection = CollectionsDatabase.GetCollection<BsonDocument>(name);
        var filter = new BsonDocument("$nor", new BsonArray { validator });
        var count = await collection.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
        if (count == 0)
        {
            return new InvalidDocumentCount(0, []);
        }

        var samples = await collection.Find(filter).Project(new BsonDocument("_id", 1)).Limit(3).ToListAsync(cancellationToken);
        return new InvalidDocumentCount(count, samples.Select(s => s.GetValue("_id", BsonNull.Value)).ToList());
    }

    /// <summary>
    /// Plans the collection's search indexes, or null when none are declared (so search indexes are never listed or pruned
    /// for a collection that doesn't declare any). On a deployment without Atlas search support, returns a plan whose single
    /// problem blocks the change.
    /// </summary>
    private async Task<SearchIndexPlan?> PlanSearchAsync(CollectionDeclaration declaration, CollectionPolicy policy, CancellationToken cancellationToken)
    {
        if (declaration.SearchIndexes.Count == 0)
        {
            return null;
        }

        List<BsonDocument> existing;
        try
        {
            using var cursor = await CollectionsDatabase.GetCollection<BsonDocument>(declaration.Name).SearchIndexes.ListAsync(null, null, cancellationToken);
            existing = await cursor.ToListAsync(cancellationToken);
        }
        catch (MongoCommandException e) when (e.Code == 26)
        {
            // NamespaceNotFound: the collection doesn't exist yet, so every declared search index is created.
            existing = [];
        }
        catch (MongoCommandException e) when (SearchUnsupportedCodes.Contains(e.Code))
        {
            // Only these errors mean the deployment has no search; any other (auth, network) is reported as it is.
            var plan = new SearchIndexPlan();
            plan.Problems.Add(
                $"{declaration.Name}: this deployment doesn't support Atlas search indexes ({e.Message}). " +
                "Search indexes need MongoDB Atlas or a local Atlas deployment (mongodb-atlas-local).");
            return plan;
        }

        return SearchIndexPlanner.Plan(declaration, existing, policy);
    }

    private async Task<IndexPlan> PlanIndexesAsync(CollectionDeclaration declaration, BsonDocument? existingCollection, CollectionPolicy policy, CancellationToken cancellationToken)
    {
        List<BsonDocument> existing;
        if (existingCollection is null)
        {
            existing = [];
        }
        else
        {
            try
            {
                using var cursor = await CollectionsDatabase.GetCollection<BsonDocument>(declaration.Name).Indexes.ListAsync(cancellationToken);
                existing = await cursor.ToListAsync(cancellationToken);
            }
            catch (MongoCommandException e) when (e.Code == 26)
            {
                // NamespaceNotFound: dropped since it was listed, so every declared index is created.
                existing = [];
            }
        }

        // A collection that's about to be created gets the declared default collation, which its indexes inherit.
        var defaultCollation = existingCollection is null
            ? declaration.Options.GetValue("collation", null) as BsonDocument
            : existingCollection.GetValue("options", null) is BsonDocument options && options.GetValue("collation", null) is BsonDocument collation ? collation : null;
        var wireVersion = await MaxWireVersionAsync(cancellationToken);
        return IndexPlanner.Plan(
            declaration,
            existing,
            policy,
            canConvertToUnique: wireVersion >= UniqueConversionWireVersion,
            canAddTtl: wireVersion >= TtlConversionWireVersion,
            await FilledCollationsAsync(declaration, cancellationToken),
            defaultCollation,
            TimeSeriesIndex(existingCollection));
    }

    /// <summary>The index MongoDB 6.3+ creates on a time series collection's metaField and timeField, e.g. <c>sensor_1_at_1</c>.</summary>
    private static string? TimeSeriesIndex(BsonDocument? existingCollection) =>
        existingCollection?.GetValue("options", null) is BsonDocument options &&
        options.GetValue("timeseries", null) is BsonDocument timeSeries &&
        timeSeries.GetValue("metaField", null) is BsonString { Value: var meta } &&
        timeSeries.GetValue("timeField", null) is BsonString { Value: var time }
            ? $"{meta}_1_{time}_1"
            : null;

    /// <summary>
    /// Each declared collation as the server fills it in for an index, which depends on the locale (e.g. <c>da</c> puts upper
    /// case first). Explaining a query with the collation shows it without changing anything. One the server won't explain,
    /// e.g. an invalid locale that creating the index will report, is left out.
    /// </summary>
    private async Task<Dictionary<string, BsonDocument>> FilledCollationsAsync(CollectionDeclaration declaration, CancellationToken cancellationToken)
    {
        var filled = new Dictionary<string, BsonDocument>(StringComparer.Ordinal);
        foreach (var collation in declaration.Indexes.Select(i => i.Options.GetValue("collation", null)).OfType<BsonDocument>())
        {
            var key = IndexPlanner.CollationKey(collation);
            if (filled.ContainsKey(key) || collation.GetValue("locale", "simple") == "simple")
            {
                continue;
            }

            if (await ExplainCollationAsync(declaration.Name, collation, cancellationToken) is { } full)
            {
                filled[key] = full;
            }
        }

        return filled;
    }

    /// <summary>A collation as the server fills it in, from explaining a query with it; null if the server won't explain it.</summary>
    private async Task<BsonDocument?> ExplainCollationAsync(string collection, BsonDocument collation, CancellationToken cancellationToken)
    {
        try
        {
            var explain = await CollectionsDatabase.RunCommandAsync<BsonDocument>(
                new BsonDocument
                {
                    { "explain", new BsonDocument { { "find", collection }, { "filter", new BsonDocument() }, { "collation", collation } } },
                    { "verbosity", "queryPlanner" },
                },
                cancellationToken: cancellationToken);
            return IndexPlanner.ExplainedCollation(explain, collation);
        }
        catch (MongoCommandException)
        {
            // Compared by the fields it sets instead.
            return null;
        }
    }
    private async Task<int> MaxWireVersionAsync(CancellationToken cancellationToken)
    {
        if (_maxWireVersion is null)
        {
            var hello = await database.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: cancellationToken);
            _maxWireVersion = hello.TryGetValue("maxWireVersion", out var version) && version.IsNumeric ? version.ToInt32() : 0;
        }

        return _maxWireVersion.Value;
    }

    public async Task<bool> SupportsTransactionsAsync(CancellationToken cancellationToken)
    {
        var hello = await database.RunCommandAsync<BsonDocument>(new BsonDocument("hello", 1), cancellationToken: cancellationToken);
        var isReplicaSet = hello.Contains("setName");
        var isMongos = hello.TryGetValue("msg", out var msg) && msg.IsString && msg.AsString == "isdbgrid";
        return isReplicaSet || isMongos;
    }

    public async Task<IExecutionScope> BeginAsync(bool transactional, CancellationToken cancellationToken)
    {
        if (!transactional)
        {
            return new MongoExecutionScope(null);
        }

        var session = await client.StartSessionAsync(cancellationToken: cancellationToken);
        // The migration's changes and its state document commit together, so commit them to a majority, and read from
        // the primary (transactions require it) even if the connection string prefers secondaries.
        session.StartTransaction(new TransactionOptions(readPreference: ReadPreference.Primary, writeConcern: WriteConcern.WMajority));
        return new MongoExecutionScope(session);
    }

    public TransactionErrorKind ClassifyTransactionError(Exception exception)
    {
        // Migrations may wrap driver exceptions, so look through inner exceptions too.
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is MongoException mongo)
            {
                if (mongo.HasErrorLabel("TransientTransactionError"))
                {
                    return TransactionErrorKind.Transient;
                }

                if (mongo.HasErrorLabel("UnknownTransactionCommitResult"))
                {
                    return TransactionErrorKind.UnknownCommitResult;
                }
            }

            if (current is AggregateException { InnerExceptions.Count: 1 } aggregate)
            {
                return ClassifyTransactionError(aggregate.InnerExceptions[0]);
            }
        }

        return TransactionErrorKind.None;
    }

    public async Task<MigrationOutputInfo?> ExecuteAsync(IExecutionScope scope, MigrationDescriptor migration, MigrationRunInfo run, OutputOptions options, CancellationToken cancellationToken)
    {
        var session = ((MongoExecutionScope)scope).Session;
        var logger = loggerFactory.CreateLogger($"Migration.{migration.Id}");
        var results = new MigrationResults(options.CaptureStatements);
        var context = new MigrationContext(database, session, logger, services, run, results);
        try
        {
            var instance = migration.Create(services);
            await (run.Direction == MigrationDirection.Up
                ? instance.UpAsync(context, cancellationToken)
                : instance.DownAsync(context, cancellationToken));
        }
        catch (Exception e)
        {
            throw new MigrationExecutionException(MigrationOutputMapper.From(results, options.MaxResultsBytes, out _), e);
        }

        var output = MigrationOutputMapper.From(results, options.MaxResultsBytes, out var tooLarge);
        return tooLarge is null
            ? output
            : throw new MigrationExecutionException(output, new InvalidOperationException(tooLarge));
    }
}

public sealed class MongoExecutionScope(IClientSessionHandle? session) : IExecutionScope
{
    private bool _committed;

    public IClientSessionHandle? Session { get; } = session;

    public bool Transactional => Session is not null;

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (Session is not null)
        {
            await Session.CommitTransactionAsync(cancellationToken);
        }

        _committed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Session is null)
        {
            return;
        }

        try
        {
            if (!_committed && Session.IsInTransaction)
            {
                await Session.AbortTransactionAsync(CancellationToken.None);
            }
        }
        catch (MongoException)
        {
            // The transaction may already have been aborted by the server.
        }
        finally
        {
            Session.Dispose();
        }
    }
}
