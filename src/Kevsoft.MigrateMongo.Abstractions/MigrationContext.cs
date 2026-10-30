using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Kevsoft.MigrateMongo;

/// <summary>
/// Everything a migration needs while running. The driver objects are exposed as-is, so your migration code is always
/// compiled against the driver you reference. Only <see cref="Track{T}"/> and <see cref="Track(IMongoDatabase, string?, bool)"/>
/// call the driver on your behalf, to record what they run.
/// </summary>
public sealed class MigrationContext
{
    public MigrationContext(
        IMongoDatabase database,
        IClientSessionHandle? session,
        ILogger logger,
        IServiceProvider services,
        MigrationRunInfo run,
        MigrationResults? results = null)
    {
        Database = database;
        Session = session;
        Logger = logger;
        Services = services;
        Run = run;
        Results = results ?? new MigrationResults();
        Results.AttachLogger(logger);
    }

    public IMongoDatabase Database { get; }

    /// <summary>
    /// The session for the current transaction, or <c>null</c> when the migration isn't transactional.
    /// Pass it to every operation that should take part in the transaction.
    /// </summary>
    public IClientSessionHandle? Session { get; }

    public ILogger Logger { get; }

    public IServiceProvider Services { get; }

    public MigrationRunInfo Run { get; }

    /// <summary>
    /// Named values and notes to record with this run, e.g. <c>context.Results.Set("skipped", 3)</c>. They're stored on
    /// the migration's state and history entry and shown by <c>up</c>, <c>status</c> and <c>show</c>.
    /// </summary>
    public MigrationResults Results { get; }

    /// <summary>
    /// Wraps <paramref name="collection"/> so each write and index operation is recorded with its statement, counts and
    /// duration, and passes <see cref="Session"/> automatically. With a <paramref name="label"/>, the counts are also added
    /// to <see cref="Results"/> under it.
    /// </summary>
    /// <param name="collection">The collection to run operations on.</param>
    /// <param name="label">A result key to add the counts to (letters, digits, <c>_</c> and <c>-</c>).</param>
    /// <param name="captureStatement">
    /// Set to false to record only the counts. Statements are stored and shown as they were sent, so turn this off when
    /// filters or updates contain secrets or personal data.
    /// </param>
    public TrackedCollection<T> Track<T>(IMongoCollection<T> collection, string? label = null, bool captureStatement = true) =>
        new(collection, Session, Results, label, captureStatement);

    /// <summary>Wraps <paramref name="database"/> so each command it runs is recorded, and passes <see cref="Session"/> automatically.</summary>
    /// <param name="database">The database to run commands on.</param>
    /// <param name="label">A label shown with the recorded commands.</param>
    /// <param name="captureStatement">
    /// Set to false to leave the commands out. Commands are stored and shown as they were sent, so turn this off for
    /// commands that carry credentials (e.g. <c>createUser</c>'s <c>pwd</c>) or personal data.
    /// </param>
    public TrackedDatabase Track(IMongoDatabase database, string? label = null, bool captureStatement = true) =>
        new(database, Session, Results, label, captureStatement);
}
