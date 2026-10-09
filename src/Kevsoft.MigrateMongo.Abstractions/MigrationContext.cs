using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Kevsoft.MigrateMongo;

/// <summary>
/// Everything a migration needs while running. The driver objects are exposed as-is; this library never
/// calls driver members on your behalf, so your migration code is always compiled against the driver you reference.
/// </summary>
public sealed class MigrationContext(
    IMongoDatabase database,
    IClientSessionHandle? session,
    ILogger logger,
    IServiceProvider services,
    MigrationRunInfo run)
{
    public IMongoDatabase Database { get; } = database;

    /// <summary>
    /// The session for the current transaction, or <c>null</c> when the migration isn't transactional.
    /// Pass it to every operation that should take part in the transaction.
    /// </summary>
    public IClientSessionHandle? Session { get; } = session;

    public ILogger Logger { get; } = logger;

    public IServiceProvider Services { get; } = services;

    public MigrationRunInfo Run { get; } = run;
}
