using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.Protocol;

namespace Kevsoft.MigrateMongo.Tests.Engine;

/// <summary>In-memory store that mimics transactional writes (buffered until commit).</summary>
internal sealed class FakeStore : IMigrationStore
{
    public Dictionary<string, StoredMigrationState> States { get; } = new(StringComparer.Ordinal);

    public List<HistoryRecord> History { get; } = [];

    public LockInfo? Lock { get; set; }

    public int ReleaseCount { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<StoredMigrationState>> GetStatesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StoredMigrationState>>(States.Values.Select(Clone).ToList());

    public Task SaveStateAsync(StoredMigrationState state, IExecutionScope? scope, CancellationToken cancellationToken)
    {
        var copy = Clone(state);
        Write(scope, () => States[copy.Id] = copy);
        return Task.CompletedTask;
    }

    public Task DeleteStateAsync(string id, IExecutionScope? scope, CancellationToken cancellationToken)
    {
        Write(scope, () => States.Remove(id));
        return Task.CompletedTask;
    }

    public Task AppendHistoryAsync(HistoryRecord record, IExecutionScope? scope, CancellationToken cancellationToken)
    {
        Write(scope, () => History.Add(record));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<HistoryRecord>> GetHistoryAsync(string? migrationId, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<HistoryRecord>>(History.Where(h => migrationId is null || h.MigrationId == migrationId).TakeLast(limit).ToList());

    public Task<bool> TryAcquireLockAsync(LockInfo lockInfo, DateTime now, CancellationToken cancellationToken)
    {
        if (Lock is null || Lock.ExpiresAt < now)
        {
            Lock = lockInfo;
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public Task<bool> RenewLockAsync(string runId, DateTime expiresAt, CancellationToken cancellationToken)
    {
        if (Lock?.RunId != runId)
        {
            return Task.FromResult(false);
        }

        Lock.ExpiresAt = expiresAt;
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

    public Task<LockInfo?> GetLockAsync(CancellationToken cancellationToken) => Task.FromResult(Lock);

    public Task<bool> DeleteLockAsync(DateTime? expiredBefore, CancellationToken cancellationToken)
    {
        if (Lock is null || (expiredBefore is not null && Lock.ExpiresAt >= expiredBefore))
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
        Id = s.Id, V = s.V, Name = s.Name, Source = s.Source, Origin = s.Origin, Checksum = s.Checksum, State = s.State,
        Direction = s.Direction, Batch = s.Batch, Transactional = s.Transactional, AppliedAt = s.AppliedAt, UpdatedAt = s.UpdatedAt,
        DurationMs = s.DurationMs, RunId = s.RunId, Error = s.Error, AppliedBy = s.AppliedBy,
    };
}

internal sealed class FakeScope(bool transactional) : IExecutionScope
{
    public bool Transactional { get; } = transactional;

    public List<Action> Pending { get; } = [];

    public Task CommitAsync(CancellationToken cancellationToken)
    {
        foreach (var write in Pending)
        {
            write();
        }

        Pending.Clear();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Pending.Clear();
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeExecutor(bool supportsTransactions = true) : IMigrationExecutor
{
    public List<string> Calls { get; } = [];

    public Task<bool> SupportsTransactionsAsync(CancellationToken cancellationToken) => Task.FromResult(supportsTransactions);

    public Task<IExecutionScope> BeginAsync(bool transactional, CancellationToken cancellationToken) =>
        Task.FromResult<IExecutionScope>(new FakeScope(transactional));

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
