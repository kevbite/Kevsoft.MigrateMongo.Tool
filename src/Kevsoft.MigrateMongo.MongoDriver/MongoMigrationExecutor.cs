using Kevsoft.MigrateMongo.Engine;
using System.Security.Cryptography;
using System.Text;
using Kevsoft.MigrateMongo.Protocol;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kevsoft.MigrateMongo.MongoDriver;

public sealed class MongoMigrationExecutor(
    IMongoClient client,
    IMongoDatabase database,
    IServiceProvider services,
    ILoggerFactory loggerFactory) : IMigrationExecutor
{
    /// <summary>MongoDB 6.0, which added converting an index to unique with <c>collMod</c>.</summary>
    private const int UniqueConversionWireVersion = 17;

    /// <summary>MongoDB 5.1, which added making a non-TTL index a TTL one with <c>collMod</c>.</summary>
    private const int TtlConversionWireVersion = 14;

    private int? _maxWireVersion;

    // Index changes are planned from the primary's view and written with majority, whatever the connection string says;
    // migrations keep the user's settings.
    private IMongoDatabase CollectionsDatabase =>
        database.WithReadPreference(ReadPreference.Primary).WithWriteConcern(WriteConcern.WMajority);

    public async Task<CollectionPlan> PlanCollectionAsync(CollectionDescriptor collection, CollectionPolicy policy, CancellationToken cancellationToken)
    {
        var (index, search) = await PlanAllAsync(collection, policy, cancellationToken);
        return Merge(index, search);
    }

    public async Task<CollectionApplyResult> ApplyCollectionAsync(
        CollectionDescriptor collection,
        CollectionPolicy policy,
        MigrationRunInfo run,
        OutputOptions options,
        CancellationToken cancellationToken)
    {
        var (index, search) = await PlanAllAsync(collection, policy, cancellationToken);
        var plan = Merge(index, search);
        if (plan.Invalid || plan.Problems.Count > 0)
        {
            return new CollectionApplyResult { Plan = plan, Applied = false };
        }

        var results = new MigrationResults(options.CaptureStatements);
        var context = new MigrationContext(CollectionsDatabase, null, loggerFactory.CreateLogger($"Collection.{collection.Name}"), services, run, results);
        try
        {
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

    /// <summary>
    /// Combines the regular-index plan with the optional search-index plan into one <see cref="CollectionPlan"/>. The
    /// declaration hash is the index hash alone unless search indexes are declared, so collections with none keep the exact
    /// hash they recorded before search support existed.
    /// </summary>
    private static CollectionPlan Merge(IndexPlan index, SearchIndexPlan? search)
    {
        var changes = index.Changes.Select(c => c.Info).ToList();
        var problems = new List<string>(index.Problems);
        var indexes = new List<IndexInfo>(index.Indexes);
        if (search is not null)
        {
            changes.AddRange(search.Changes.Select(c => c.Info));
            problems.AddRange(search.Problems);
            indexes.AddRange(search.Indexes);
        }

        return new CollectionPlan
        {
            Declaration = search is null || index.Invalid ? index.Declaration : CombineDeclaration(index.Declaration, search.Declaration),
            Changes = changes,
            Problems = problems,
            Invalid = index.Invalid,
            Indexes = indexes,
            Hidden = new Dictionary<string, DateTime>(index.Hidden, StringComparer.Ordinal),
        };
    }

    private static string CombineDeclaration(string indexHash, string searchHash) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(indexHash + "\n" + searchHash))).ToLowerInvariant();

    private async Task<(IndexPlan Index, SearchIndexPlan? Search)> PlanAllAsync(CollectionDescriptor collection, CollectionPolicy policy, CancellationToken cancellationToken)
    {
        CollectionDeclaration declaration;
        try
        {
            declaration = collection.Declare(services, database);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (IndexPlan.InvalidPlan($"{collection.Name}: {e.Message}"), null);
        }

        var index = await PlanIndexesAsync(declaration, policy, cancellationToken);
        var search = await PlanSearchAsync(declaration, policy, cancellationToken);
        return (index, search);
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
        catch (MongoException e)
        {
            // $listSearchIndexes isn't recognised off Atlas; declaring search indexes there can't be applied.
            var plan = new SearchIndexPlan();
            plan.Problems.Add(
                $"{declaration.Name}: this deployment doesn't support Atlas search indexes ({e.Message}). " +
                "Search indexes need MongoDB Atlas or a local Atlas deployment (mongodb-atlas-local).");
            return plan;
        }

        return SearchIndexPlanner.Plan(declaration, existing, policy);
    }

    private async Task<IndexPlan> PlanIndexesAsync(CollectionDeclaration declaration, CollectionPolicy policy, CancellationToken cancellationToken)
    {
        List<BsonDocument> existing;
        try
        {
            using var cursor = await CollectionsDatabase.GetCollection<BsonDocument>(declaration.Name).Indexes.ListAsync(cancellationToken);
            existing = await cursor.ToListAsync(cancellationToken);
        }
        catch (MongoCommandException e) when (e.Code == 26)
        {
            // NamespaceNotFound: the collection doesn't exist yet, so every declared index is created (which creates it).
            existing = [];
        }

        var wireVersion = await MaxWireVersionAsync(cancellationToken);
        return IndexPlanner.Plan(
            declaration,
            existing,
            policy,
            canConvertToUnique: wireVersion >= UniqueConversionWireVersion,
            canAddTtl: wireVersion >= TtlConversionWireVersion,
            await FilledCollationsAsync(declaration, cancellationToken),
            await DefaultCollationAsync(declaration.Name, cancellationToken));
    }

    /// <summary>The collection's default collation, which the server gives an index created without one.</summary>
    private async Task<BsonDocument?> DefaultCollationAsync(string collection, CancellationToken cancellationToken)
    {
        using var cursor = await CollectionsDatabase.ListCollectionsAsync(new ListCollectionsOptions { Filter = new BsonDocument("name", collection) }, cancellationToken);
        var info = await cursor.FirstOrDefaultAsync(cancellationToken);
        return info?.GetValue("options", null) is BsonDocument options && options.GetValue("collation", null) is BsonDocument collation ? collation : null;
    }

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

            try
            {
                var explain = await CollectionsDatabase.RunCommandAsync<BsonDocument>(
                    new BsonDocument
                    {
                        { "explain", new BsonDocument { { "find", declaration.Name }, { "filter", new BsonDocument() }, { "collation", collation } } },
                        { "verbosity", "queryPlanner" },
                    },
                    cancellationToken: cancellationToken);
                if (IndexPlanner.ExplainedCollation(explain, collation) is { } full)
                {
                    filled[key] = full;
                }
            }
            catch (MongoCommandException)
            {
                // Compared by the fields it sets instead.
            }
        }

        return filled;
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
