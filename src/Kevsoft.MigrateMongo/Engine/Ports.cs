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

    /// <summary>A collection's indexes were brought in line with its definition.</summary>
    public const string Reconciled = "reconciled";

    /// <summary>The <see cref="HistoryRecord.Kind"/> of a collection definition's entries.</summary>
    public const string CollectionKind = "collection";
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

/// <summary>A document in the collections state collection: the last declaration <c>up</c> applied for one collection.</summary>
public sealed class StoredCollectionState
{
    /// <summary>The schema version this library writes. Documents with a higher version are refused.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The collection's name.</summary>
    public string Name { get; set; } = "";

    public int V { get; set; } = CurrentSchemaVersion;

    public string Source { get; set; } = "";

    public string Origin { get; set; } = "";

    /// <summary>A hash of the declaration that was last applied.</summary>
    public string Declaration { get; set; } = "";

    /// <summary>
    /// The declaration that one replaced. A run that brings it back soon after (e.g. an instance of the previous build
    /// starting during a rolling deploy) is skipped unless it's run with <c>--allow-superseded</c>.
    /// </summary>
    public string? PreviousDeclaration { get; set; }

    /// <summary>When <see cref="Declaration"/> was first applied, which later runs that only fix drift don't change.</summary>
    public DateTime? ReplacedAt { get; set; }

    /// <summary>When a run last changed something or applied a new declaration.</summary>
    public DateTime ReconciledAt { get; set; }

    public long? DurationMs { get; set; }

    public string RunId { get; set; } = "";

    public Provenance AppliedBy { get; set; } = new();

    /// <summary>Undeclared indexes this tool hid (with <c>prune: hide</c>), and when.</summary>
    public Dictionary<string, DateTime> Hidden { get; set; } = new(StringComparer.Ordinal);

    /// <summary>What the last run that changed something recorded.</summary>
    public MigrationOutputInfo? Output { get; set; }
}

/// <summary>Persistence for state, history and the lock.</summary>
public interface IMigrationStore
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredMigrationState>> GetStatesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredCollectionState>> GetCollectionStatesAsync(CancellationToken cancellationToken);

    /// <summary>Inserts or replaces a collection state document.</summary>
    Task SaveCollectionStateAsync(StoredCollectionState state, CancellationToken cancellationToken);

    /// <summary>Inserts or replaces a state document. When <paramref name="scope"/> is transactional the write is part of it.</summary>
    Task SaveStateAsync(StoredMigrationState state, IExecutionScope? scope, CancellationToken cancellationToken);

    Task DeleteStateAsync(string id, IExecutionScope? scope, CancellationToken cancellationToken);

    Task AppendHistoryAsync(HistoryRecord record, IExecutionScope? scope, CancellationToken cancellationToken);

    Task<IReadOnlyList<HistoryRecord>> GetHistoryAsync(string? migrationId, int limit, CancellationToken cancellationToken);

    /// <summary>Takes the lock if nobody holds it or the current holder's lock expired before <paramref name="now"/>.</summary>
    Task<bool> TryAcquireLockAsync(LockInfo lockInfo, DateTime now, CancellationToken cancellationToken);

    /// <summary>Extends the lock; returns false if <paramref name="runId"/> no longer holds it.</summary>
    Task<bool> RenewLockAsync(string runId, DateTime expiresAt, CancellationToken cancellationToken);

    Task ReleaseLockAsync(string runId, CancellationToken cancellationToken);

    Task<LockInfo?> GetLockAsync(CancellationToken cancellationToken);

    /// <summary>Deletes the lock. With <paramref name="expiredBefore"/> only deletes it if it expired before that time.</summary>
    Task<bool> DeleteLockAsync(DateTime? expiredBefore, CancellationToken cancellationToken);
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

    /// <summary>Works out what bringing a collection in line with its definition would change, without changing anything.</summary>
    Task<CollectionPlan> PlanCollectionAsync(CollectionDescriptor collection, CollectionPolicy policy, CancellationToken cancellationToken);

    /// <summary>
    /// Plans again and applies the plan, unless it's blocked or invalid (then <see cref="CollectionApplyResult.Applied"/> is
    /// false and nothing changes). If a change fails, throws a <see cref="MigrationExecutionException"/> carrying what was
    /// recorded before it failed.
    /// </summary>
    Task<CollectionApplyResult> ApplyCollectionAsync(CollectionDescriptor collection, CollectionPolicy policy, MigrationRunInfo run, OutputOptions options, CancellationToken cancellationToken);
}

/// <summary>The settings a collection plan is worked out with.</summary>
public sealed class CollectionPolicy
{
    /// <summary><c>report</c>, <c>hide</c> or <c>drop</c>, unless the definition sets its own.</summary>
    public string Prune { get; init; } = "report";

    /// <summary><c>allow</c> or <c>fail</c>. A definition can make it stricter, not looser.</summary>
    public string OnRebuild { get; init; } = "allow";

    public TimeSpan DropHiddenAfter { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Index names that may be rebuilt even when <see cref="OnRebuild"/> is <c>fail</c>.</summary>
    public IReadOnlySet<string> AllowRebuild { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Change the collection even though none of its existing indexes match the declared ones.</summary>
    public bool AllowUnmatched { get; init; }

    /// <summary>Undeclared indexes this tool hid earlier, and when.</summary>
    public IReadOnlyDictionary<string, DateTime> Hidden { get; init; } = new Dictionary<string, DateTime>(StringComparer.Ordinal);

    public DateTime Now { get; init; }
}

/// <summary>What bringing a collection in line with its definition would change.</summary>
public sealed class CollectionPlan
{
    /// <summary>A hash of what the definition declares (empty when <see cref="Invalid"/>).</summary>
    public string Declaration { get; init; } = "";

    /// <summary>The changes in the order they'd be made, plus <c>keep</c> entries for undeclared indexes left alone.</summary>
    public List<IndexChangeInfo> Changes { get; init; } = [];

    /// <summary>Why the plan can't be applied: an invalid definition, a blocked rebuild, or no existing index matching.</summary>
    public List<string> Problems { get; init; } = [];

    /// <summary>True when the definition itself couldn't be rendered or is inconsistent.</summary>
    public bool Invalid { get; init; }

    /// <summary>The indexes on the server and how each relates to the definition.</summary>
    public List<IndexInfo> Indexes { get; init; } = [];

    /// <summary>The undeclared indexes this tool will have hidden once the plan is applied, and when each was hidden.</summary>
    public Dictionary<string, DateTime> Hidden { get; init; } = new(StringComparer.Ordinal);

    public bool HasChanges => Changes.Any(c => c.Action != IndexActions.Keep);
}

public sealed class CollectionApplyResult
{
    /// <summary>The plan that was applied, or that blocked applying.</summary>
    public required CollectionPlan Plan { get; init; }

    public bool Applied { get; init; }

    /// <summary>What the changes recorded (null if nothing ran).</summary>
    public MigrationOutputInfo? Output { get; init; }
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

    /// <summary><c>report</c>, <c>hide</c> or <c>drop</c>: what happens to indexes a collection definition doesn't declare.</summary>
    public string IndexesPrune { get; set; } = "report";

    /// <summary><c>allow</c> or <c>fail</c>: whether an index may be dropped and created again.</summary>
    public string IndexesOnRebuild { get; set; } = "allow";

    /// <summary>With <c>prune: hide</c>, how long an undeclared index stays hidden before it's dropped.</summary>
    public TimeSpan IndexesDropHiddenAfter { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How long after a collection definition is replaced a run that brings back the previous one is skipped as an older
    /// build (e.g. during a rolling deploy). <see cref="TimeSpan.Zero"/> turns the check off.
    /// </summary>
    public TimeSpan IndexesSupersededWindow { get; set; } = TimeSpan.FromHours(1);

    public Provenance Provenance { get; set; } = new();
}
