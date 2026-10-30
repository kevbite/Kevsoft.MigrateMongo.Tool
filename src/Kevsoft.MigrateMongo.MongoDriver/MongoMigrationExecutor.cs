using Kevsoft.MigrateMongo.Engine;
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

    public Task ExecuteAsync(IExecutionScope scope, MigrationDescriptor migration, MigrationRunInfo run, CancellationToken cancellationToken)
    {
        var session = ((MongoExecutionScope)scope).Session;
        var logger = loggerFactory.CreateLogger($"Migration.{migration.Id}");
        var context = new MigrationContext(database, session, logger, services, run);
        var instance = migration.Create(services);
        return run.Direction == MigrationDirection.Up
            ? instance.UpAsync(context, cancellationToken)
            : instance.DownAsync(context, cancellationToken);
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
