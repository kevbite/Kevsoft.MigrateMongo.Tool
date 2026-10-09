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

/// <summary>Runs migration code against the database.</summary>
public interface IMigrationExecutor
{
    Task<bool> SupportsTransactionsAsync(CancellationToken cancellationToken);

    Task<IExecutionScope> BeginAsync(bool transactional, CancellationToken cancellationToken);

    Task ExecuteAsync(IExecutionScope scope, MigrationDescriptor migration, MigrationRunInfo run, CancellationToken cancellationToken);
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

    public string? Environment { get; set; }

    public Provenance Provenance { get; set; } = new();
}
