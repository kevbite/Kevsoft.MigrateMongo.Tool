using Kevsoft.MigrateMongo.Protocol;

namespace Kevsoft.MigrateMongo.Engine;

public static class MigrationStates
{
    public const string Applied = "applied";
    public const string Applying = "applying";
    public const string RollingBack = "rollingBack";
    public const string Failed = "failed";
    public const string Pending = "pending";
}

public static class HistoryEvents
{
    public const string Applied = "applied";
    public const string RolledBack = "rolledBack";
    public const string Failed = "failed";
    public const string MarkedApplied = "markedApplied";
    public const string MarkedUnapplied = "markedUnapplied";
    public const string Baselined = "baselined";
}

/// <summary>Who/what applied a migration.</summary>
public sealed class Provenance
{
    public string Host { get; set; } = "";

    public string User { get; set; } = "";

    public string? Tool { get; set; }

    public string Library { get; set; } = "";

    public string Driver { get; set; } = "";

    public string Runtime { get; set; } = "";
}

/// <summary>A document in the state collection (one per migration that isn't pending).</summary>
public sealed class StoredMigrationState
{
    /// <summary>The schema version this library writes. Documents with a higher version are refused.</summary>
    public const int CurrentSchemaVersion = 1;

    public string Id { get; set; } = "";

    public int V { get; set; } = CurrentSchemaVersion;

    public string Name { get; set; } = "";

    public string Source { get; set; } = "";

    public string Origin { get; set; } = "";

    public string Checksum { get; set; } = "";

    public string State { get; set; } = "";

    /// <summary>Direction of the last operation (<c>up</c> or <c>down</c>).</summary>
    public string Direction { get; set; } = "up";

    public int Batch { get; set; }

    public bool Transactional { get; set; }

    public DateTime? AppliedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public long? DurationMs { get; set; }

    public string RunId { get; set; } = "";

    public string? Error { get; set; }

    public Provenance AppliedBy { get; set; } = new();

    /// <summary>What the last run recorded (results, notes and operations), if anything.</summary>
    public MigrationOutputInfo? Output { get; set; }
}

/// <summary>Persistence for state, history and the lock.</summary>
public interface IMigrationStore
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredMigrationState>> GetStatesAsync(CancellationToken cancellationToken);

    /// <summary>Inserts or replaces a state document. When <paramref name="scope"/> is transactional the write is part of it.</summary>
    Task SaveStateAsync(StoredMigrationState state, IExecutionScope? scope, CancellationToken cancellationToken);

    Task DeleteStateAsync(string id, IExecutionScope? scope, CancellationToken cancellationToken);

    Task AppendHistoryAsync(HistoryRecord record, IExecutionScope? scope, CancellationToken cancellationToken);

    Task<IReadOnlyList<HistoryRecord>> GetHistoryAsync(string? migrationId, int limit, CancellationToken cancellationToken);

    /// <summary>The distinct migration ids in the history that start with <paramref name="prefix"/>.</summary>
    Task<IReadOnlyList<string>> GetHistoryIdsAsync(string prefix, CancellationToken cancellationToken);

    // Lock expiry is decided by the store's (database server's) clock, so runs on machines whose clocks disagree still
    // agree on whether a lock has expired.

    /// <summary>
    /// Takes the lock for <paramref name="ttl"/> if nobody holds it or the holder's lock has expired.
    /// Returns the lock as stored, or null if another run holds it.
    /// </summary>
    Task<LockInfo?> TryAcquireLockAsync(string runId, string? host, TimeSpan ttl, CancellationToken cancellationToken);

    /// <summary>Extends the lock to <paramref name="ttl"/> from now; returns false if <paramref name="runId"/> no longer holds it.</summary>
    Task<bool> RenewLockAsync(string runId, TimeSpan ttl, CancellationToken cancellationToken);

    Task ReleaseLockAsync(string runId, CancellationToken cancellationToken);

    /// <summary>The current lock, with <see cref="LockInfo.Expired"/> set.</summary>
    Task<LockInfo?> GetLockAsync(CancellationToken cancellationToken);

    /// <summary>Deletes the lock. With <paramref name="onlyIfExpired"/> only deletes it if it has expired.</summary>
    Task<bool> DeleteLockAsync(bool onlyIfExpired, CancellationToken cancellationToken);
}

/// <summary>An open unit of work, optionally transactional.</summary>
public interface IExecutionScope : IAsyncDisposable
{
    bool Transactional { get; }

    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>How an exception thrown while running or committing a transaction should be handled.</summary>
public enum TransactionErrorKind
{
    /// <summary>Not retryable.</summary>
    None,

    /// <summary>The whole transaction can be retried (MongoDB's <c>TransientTransactionError</c> label).</summary>
    Transient,

    /// <summary>The commit outcome is unknown; the commit can be retried (<c>UnknownTransactionCommitResult</c> label).</summary>
    UnknownCommitResult,
}

/// <summary>Runs migration code against the database.</summary>
public interface IMigrationExecutor
{
    Task<bool> SupportsTransactionsAsync(CancellationToken cancellationToken);

    Task<IExecutionScope> BeginAsync(bool transactional, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the migration and returns what it recorded (null if nothing). If it fails, throws a
    /// <see cref="MigrationExecutionException"/> carrying what it recorded before failing.
    /// </summary>
    Task<MigrationOutputInfo?> ExecuteAsync(IExecutionScope scope, MigrationDescriptor migration, MigrationRunInfo run, OutputOptions options, CancellationToken cancellationToken);

    TransactionErrorKind ClassifyTransactionError(Exception exception);
}

/// <summary>How a migration's results are recorded.</summary>
/// <param name="CaptureStatements">Record the statements run through <c>context.Track(...)</c>.</param>
/// <param name="MaxResultsBytes">The largest the named results may be, in BSON bytes.</param>
public sealed record OutputOptions(bool CaptureStatements, int MaxResultsBytes);

/// <summary>A migration failed; <see cref="Output"/> is what it recorded before failing.</summary>
public sealed class MigrationExecutionException(MigrationOutputInfo? output, Exception innerException)
    : Exception(innerException.Message, innerException)
{
    public MigrationOutputInfo? Output { get; } = output;
}

public interface IMigrationEventSink
{
    void OnEvent(RunnerEvent runnerEvent);
}

public sealed class EngineOptions
{
    public TimeSpan LockTtl { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan LockWaitTimeout { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan LockPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary><c>fail</c>, <c>warn</c> or <c>ignore</c>.</summary>
    public string OnChecksumMismatch { get; set; } = "fail";

    /// <summary><c>auto</c>, <c>always</c> or <c>never</c>.</summary>
    public string Transaction { get; set; } = "auto";

    public bool AllowOutOfOrder { get; set; }

    /// <summary>How long a transactional migration keeps retrying transient transaction errors (the driver's default is 120s).</summary>
    public TimeSpan TransactionRetryTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>Delay before the first retry; doubles on each attempt up to 5 seconds.</summary>
    public TimeSpan TransactionRetryDelay { get; set; } = TimeSpan.FromMilliseconds(100);

    public string? Environment { get; set; }

    /// <summary>Record the statements run through <c>context.Track(...)</c> (can be turned off per run).</summary>
    public bool CaptureStatements { get; set; } = true;

    /// <summary>The largest a migration's named results may be, in BSON bytes.</summary>
    public int ResultsMaxBytes { get; set; } = 64 * 1024;

    public Provenance Provenance { get; set; } = new();
}
