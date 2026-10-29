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
    ILoggerFactory loggerFactory) : IMigrationExecutor
{
    /// <summary>MongoDB 6.0, which added converting an index to unique with <c>collMod</c>.</summary>
    private const int UniqueConversionWireVersion = 17;

    private int? _maxWireVersion;

    public async Task<CollectionPlan> PlanCollectionAsync(CollectionDescriptor collection, CollectionPolicy policy, CancellationToken cancellationToken) =>
        (await PlanIndexesAsync(collection, policy, cancellationToken)).ToCollectionPlan();

    public async Task<CollectionApplyResult> ApplyCollectionAsync(
        CollectionDescriptor collection,
        CollectionPolicy policy,
        MigrationRunInfo run,
        OutputOptions options,
        CancellationToken cancellationToken)
    {
        var plan = await PlanIndexesAsync(collection, policy, cancellationToken);
        if (plan.Invalid || plan.Problems.Count > 0)
        {
            return new CollectionApplyResult { Plan = plan.ToCollectionPlan(), Applied = false };
        }

        var results = new MigrationResults(options.CaptureStatements);
        var context = new MigrationContext(database, null, loggerFactory.CreateLogger($"Collection.{collection.Name}"), services, run, results);
        try
        {
            await IndexApplier.ApplyAsync(context, collection.Name, plan, cancellationToken);
        }
        catch (Exception e)
        {
            throw new MigrationExecutionException(MigrationOutputMapper.From(results, options.MaxResultsBytes, out _), e);
        }

        return new CollectionApplyResult
        {
            Plan = plan.ToCollectionPlan(),
            Applied = true,
            Output = MigrationOutputMapper.From(results, options.MaxResultsBytes, out _),
        };
    }

    private async Task<IndexPlan> PlanIndexesAsync(CollectionDescriptor collection, CollectionPolicy policy, CancellationToken cancellationToken)
    {
        CollectionDeclaration declaration;
        try
        {
            declaration = collection.Declare(services, database);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return IndexPlan.InvalidPlan($"{collection.Name}: {e.Message}");
        }

        List<BsonDocument> existing;
        try
        {
            using var cursor = await database.GetCollection<BsonDocument>(declaration.Name).Indexes.ListAsync(cancellationToken);
            existing = await cursor.ToListAsync(cancellationToken);
        }
        catch (MongoCommandException e) when (e.Code == 26)
        {
            // NamespaceNotFound: the collection doesn't exist yet, so every declared index is created (which creates it).
            existing = [];
        }

        var canConvertToUnique = await MaxWireVersionAsync(cancellationToken) >= UniqueConversionWireVersion;
        return IndexPlanner.Plan(declaration, existing, policy, canConvertToUnique, await FilledCollationsAsync(declaration, cancellationToken));
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
                var explain = await database.RunCommandAsync<BsonDocument>(
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
        session.StartTransaction();
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
