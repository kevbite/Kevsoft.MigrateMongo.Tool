using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.Protocol;

namespace Kevsoft.MigrateMongo.Tests.Engine;

/// <summary>In-memory store that mimics transactional writes (buffered until commit).</summary>
/// <param name="clock">The store's clock, which decides lock expiry as the database server's does (default: UTC now).</param>
internal sealed class FakeStore(Func<DateTime>? clock = null) : IMigrationStore
{
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);

    public Dictionary<string, StoredMigrationState> States { get; } = new(StringComparer.Ordinal);

    public List<HistoryRecord> History { get; } = [];

    public LockInfo? Lock { get; set; }

    public int ReleaseCount { get; private set; }

    public int InitializeCount { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        InitializeCount++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredMigrationState>> GetStatesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StoredMigrationState>>(States.Values.Select(Clone).ToList());

    // Writes fail on a cancelled token, as the driver's do.

    public Task SaveStateAsync(StoredMigrationState state, IExecutionScope? scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var copy = Clone(state);
        Write(scope, () => States[copy.Id] = copy);
        return Task.CompletedTask;
    }

    public Task DeleteStateAsync(string id, IExecutionScope? scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(scope, () => States.Remove(id));
        return Task.CompletedTask;
    }

    public Task AppendHistoryAsync(HistoryRecord record, IExecutionScope? scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(scope, () => History.Add(record));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<HistoryRecord>> GetHistoryAsync(string? migrationId, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<HistoryRecord>>(History.Where(h => migrationId is null || h.MigrationId == migrationId).TakeLast(limit).ToList());

    public Task<LockInfo?> TryAcquireLockAsync(string runId, string? host, TimeSpan ttl, CancellationToken cancellationToken)
    {
        var now = _clock();
        if (Lock is not null && Lock.ExpiresAt >= now)
        {
            return Task.FromResult<LockInfo?>(null);
        }

        Lock = new LockInfo { RunId = runId, Host = host, AcquiredAt = now, ExpiresAt = now + ttl };
        return Task.FromResult<LockInfo?>(Lock);
    }

    /// <summary>Called on each renewal; return an exception to make that renewal fail.</summary>
    public Func<Exception?>? RenewFailure { get; set; }

    public Task<bool> RenewLockAsync(string runId, TimeSpan ttl, CancellationToken cancellationToken)
    {
        if (RenewFailure?.Invoke() is { } failure)
        {
            throw failure;
        }

        if (Lock?.RunId != runId)
        {
            return Task.FromResult(false);
        }

        Lock.ExpiresAt = _clock() + ttl;
        return Task.FromResult(true);
    }

    public Task ReleaseLockAsync(string runId, CancellationToken cancellationToken)
    {
        if (Lock?.RunId == runId)
        {
            Lock = null;
            ReleaseCount++;
        }

        return Task.CompletedTask;
    }

    public Task<LockInfo?> GetLockAsync(CancellationToken cancellationToken)
    {
        if (Lock is not null)
        {
            Lock.Expired = Lock.ExpiresAt < _clock();
        }

        return Task.FromResult(Lock);
    }

    public Task<bool> DeleteLockAsync(bool onlyIfExpired, CancellationToken cancellationToken)
    {
        if (Lock is null || (onlyIfExpired && Lock.ExpiresAt >= _clock()))
        {
            return Task.FromResult(false);
        }

        Lock = null;
        return Task.FromResult(true);
    }

    private static void Write(IExecutionScope? scope, Action write)
    {
        if (scope is FakeScope { Transactional: true } fake)
        {
            fake.Pending.Add(write);
        }
        else
        {
            write();
        }
    }

    private static StoredMigrationState Clone(StoredMigrationState s) => new()
    {
        Id = s.Id,
        V = s.V,
        Name = s.Name,
        Source = s.Source,
        Origin = s.Origin,
        Checksum = s.Checksum,
        State = s.State,
        Direction = s.Direction,
        Batch = s.Batch,
        Transactional = s.Transactional,
        AppliedAt = s.AppliedAt,
        UpdatedAt = s.UpdatedAt,
        DurationMs = s.DurationMs,
        RunId = s.RunId,
        Error = s.Error,
        AppliedBy = s.AppliedBy,
    };
}

internal sealed class FakeScope(bool transactional, Func<Exception?>? commitFailure = null) : IExecutionScope
{
    public bool Transactional { get; } = transactional;

    public List<Action> Pending { get; } = [];

    public bool Aborted { get; private set; }

    public Task CommitAsync(CancellationToken cancellationToken)
    {
        // Like MongoExecutionScope: committing a transaction honours the token; there's nothing to commit otherwise.
        if (Transactional)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (commitFailure?.Invoke() is { } failure)
        {
            throw failure;
        }

        foreach (var write in Pending)
        {
            write();
        }

        Pending.Clear();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Aborted = Pending.Count > 0;
        Pending.Clear();
        return ValueTask.CompletedTask;
    }
}

/// <summary>An error the fake executor classifies by <see cref="Kind"/>, standing in for a labelled MongoDB error.</summary>
internal sealed class FakeTransactionException(TransactionErrorKind kind) : Exception($"Fake {kind} error.")
{
    public TransactionErrorKind Kind { get; } = kind;
}

internal sealed class FakeExecutor(bool supportsTransactions = true) : IMigrationExecutor
{
    public List<string> Calls { get; } = [];

    public List<FakeScope> Scopes { get; } = [];

    /// <summary>Called on each commit; return an exception to make that commit fail.</summary>
    public Func<Exception?>? CommitFailure { get; set; }

    public Task<bool> SupportsTransactionsAsync(CancellationToken cancellationToken) => Task.FromResult(supportsTransactions);

    public Task<IExecutionScope> BeginAsync(bool transactional, CancellationToken cancellationToken)
    {
        var scope = new FakeScope(transactional, CommitFailure);
        Scopes.Add(scope);
        return Task.FromResult<IExecutionScope>(scope);
    }

    public TransactionErrorKind ClassifyTransactionError(Exception exception) =>
        exception is FakeTransactionException fake ? fake.Kind : TransactionErrorKind.None;

    public async Task ExecuteAsync(IExecutionScope scope, MigrationDescriptor migration, MigrationRunInfo run, CancellationToken cancellationToken)
    {
        Calls.Add($"{run.Direction}:{migration.Id}:{(run.Transactional ? "tx" : "notx")}");
        var instance = migration.Create(new EmptyServices());
        var context = new MigrationContext(null!, null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, new EmptyServices(), run);
        if (run.Direction == MigrationDirection.Up)
        {
            await instance.UpAsync(context, cancellationToken);
        }
        else
        {
            await instance.DownAsync(context, cancellationToken);
        }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}

/// <summary>A clock that always says <paramref name="now"/>, e.g. a machine whose clock differs from the database server's.</summary>
internal sealed class FixedTime(DateTime now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(now);
}

internal sealed class RecordingSink : IMigrationEventSink
{
    public List<RunnerEvent> Events { get; } = [];

    public void OnEvent(RunnerEvent runnerEvent) => Events.Add(runnerEvent);
}

internal sealed class TestMigration(Func<Task>? up = null, Func<Task>? down = null) : IMigration
{
    public Task UpAsync(MigrationContext context, CancellationToken cancellationToken) => up?.Invoke() ?? Task.CompletedTask;

    public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) => down?.Invoke() ?? Task.CompletedTask;
}

internal static class Migrations
{
    public static MigrationDescriptor Create(
        string id,
        string name,
        string checksum = "sha256:aaaa",
        TransactionMode transaction = TransactionMode.Auto,
        Func<Task>? up = null,
        Func<Task>? down = null) =>
        new(id, name, "csharp", $"Test.{name}", checksum, transaction, _ => new TestMigration(up, down));
}
