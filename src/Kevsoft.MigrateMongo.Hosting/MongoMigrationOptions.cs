namespace Kevsoft.MigrateMongo.Hosting;

/// <summary>What to do when an applied migration's code has changed since it was applied.</summary>
public enum ChecksumMismatchBehavior
{
    Fail,
    Warn,
    Ignore,
}

/// <summary>
/// Settings for running migrations in-app. They mirror the <c>migrate-mongo.yaml</c> settings, so keep them the same as the
/// config the CLI uses for the same database.
/// </summary>
public sealed class MongoMigrationOptions
{
    /// <summary>
    /// Connection string for the migrations. When <c>null</c>, the <c>IMongoClient</c> registered in the service collection is
    /// used instead.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>The database to migrate. Required.</summary>
    public string DatabaseName { get; set; } = "";

    /// <summary>The state collection (<c>stateCollection</c>); history and the lock use <c>{name}.history</c> and <c>{name}.lock</c>.</summary>
    public string StateCollection { get; set; } = "_migrations";

    /// <summary>How long the lock is held before it must be renewed (<c>lockTtl</c>).</summary>
    public TimeSpan LockTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long to wait for another instance (or the CLI) to release the lock (<c>lockWaitTimeout</c>).</summary>
    public TimeSpan LockWaitTimeout { get; set; } = TimeSpan.FromMinutes(1);

    public ChecksumMismatchBehavior OnChecksumMismatch { get; set; } = ChecksumMismatchBehavior.Fail;

    /// <summary>The project-wide default for migrations whose <c>[Migration]</c> uses <see cref="TransactionMode.Auto"/>.</summary>
    public TransactionMode Transaction { get; set; } = TransactionMode.Auto;

    public bool AllowOutOfOrder { get; set; }

    /// <summary>How long a transactional migration keeps retrying transient transaction errors (<see cref="TimeSpan.Zero"/> disables retries).</summary>
    public TimeSpan TransactionRetryTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>Recorded with each run, e.g. the hosting environment name.</summary>
    public string? Environment { get; set; }

    /// <summary>
    /// Record the statements (filters, updates, index keys, …) run through <c>context.Track(...)</c>, like
    /// <c>results.captureStatements</c>. Counts and durations are always recorded.
    /// </summary>
    public bool CaptureStatements { get; set; } = true;

    /// <summary>The largest a migration's named results may be, in BSON bytes (<c>results.maxBytes</c>); more fails the migration.</summary>
    public int ResultsMaxBytes { get; set; } = 64 * 1024;

    /// <summary>What happens to indexes a collection definition doesn't declare (<c>indexes.prune</c>).</summary>
    public IndexPrune IndexPrune { get; set; } = IndexPrune.Report;

    /// <summary>Whether an index may be dropped and created again (<c>indexes.onRebuild</c>).</summary>
    public IndexRebuild IndexRebuild { get; set; } = IndexRebuild.Allow;

    /// <summary>With <see cref="IndexPrune.Hide"/>, how long an undeclared index stays hidden before it's dropped (<c>indexes.dropHiddenAfter</c>).</summary>
    public TimeSpan DropHiddenIndexesAfter { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// An SSH tunnel to connect through, like the <c>tunnel</c> config section. Needs <see cref="ConnectionString"/> and a
    /// <see cref="TunnelProvider"/>. The tunnel is open only while migrations run.
    /// </summary>
    public Config.TunnelConfig? Tunnel { get; set; }

    /// <summary>Opens <see cref="Tunnel"/>: reference <c>Kevsoft.MigrateMongo.Tunnel</c> and use <c>new SshTunnelProvider()</c>.</summary>
    public Config.IMongoTunnelProvider? TunnelProvider { get; set; }

    internal void Validate()
    {
        var errors = new List<string>();
        if (Tunnel is not null && ConnectionString is null)
        {
            errors.Add($"{nameof(Tunnel)} needs {nameof(ConnectionString)}; a registered IMongoClient can't be tunnelled.");
        }

        if (Tunnel is not null && TunnelProvider is null)
        {
            errors.Add($"{nameof(Tunnel)} needs a {nameof(TunnelProvider)}: add the Kevsoft.MigrateMongo.Tunnel package and set `TunnelProvider = new SshTunnelProvider()`.");
        }

        if (string.IsNullOrWhiteSpace(DatabaseName))
        {
            errors.Add($"{nameof(DatabaseName)} is required.");
        }

        if (string.IsNullOrWhiteSpace(StateCollection))
        {
            errors.Add($"{nameof(StateCollection)} is required.");
        }

        if (LockTtl <= TimeSpan.Zero)
        {
            errors.Add($"{nameof(LockTtl)} must be positive.");
        }

        if (LockWaitTimeout < TimeSpan.Zero)
        {
            errors.Add($"{nameof(LockWaitTimeout)} can't be negative.");
        }

        if (TransactionRetryTimeout < TimeSpan.Zero)
        {
            errors.Add($"{nameof(TransactionRetryTimeout)} can't be negative.");
        }

        if (ResultsMaxBytes < 1)
        {
            errors.Add($"{nameof(ResultsMaxBytes)} must be positive.");
        }

        if (DropHiddenIndexesAfter < TimeSpan.Zero)
        {
            errors.Add($"{nameof(DropHiddenIndexesAfter)} can't be negative.");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid Kevsoft.MigrateMongo options: " + string.Join(" ", errors));
        }
    }
}
