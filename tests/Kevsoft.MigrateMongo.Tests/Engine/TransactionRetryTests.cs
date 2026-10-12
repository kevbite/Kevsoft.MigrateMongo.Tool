using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.Protocol;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Engine;

public sealed class TransactionRetryTests
{
    private readonly FakeStore _store = new();
    private readonly RecordingSink _sink = new();

    private MigrationEngine Engine(MigrationDescriptor migration, FakeExecutor executor, TimeSpan? retryTimeout = null) =>
        new(_store, executor, [migration], new EngineOptions
        {
            LockWaitTimeout = TimeSpan.Zero,
            TransactionRetryDelay = TimeSpan.Zero,
            TransactionRetryTimeout = retryTimeout ?? TimeSpan.FromSeconds(30),
            Provenance = new Provenance { Host = "test-host", User = "tester", Library = "1.0.0", Driver = "3.5.0", Runtime = "10.0.0" },
        }, _sink);

    private static MigrationDescriptor FailingFirst(int failures, Exception error, Action? onRun = null, TransactionMode transaction = TransactionMode.Auto)
    {
        var runs = 0;
        return Migrations.Create("20260101000001", "flaky", transaction: transaction, up: () =>
        {
            onRun?.Invoke();
            return ++runs <= failures ? Task.FromException(error) : Task.CompletedTask;
        });
    }

    private IEnumerable<string> Warnings => _sink.Events.Where(e => e is { Type: EventTypes.Log, Level: "warning" }).Select(e => e.Message!);

    [Fact]
    public async Task Transient_error_in_the_migration_retries_the_whole_transaction()
    {
        var executor = new FakeExecutor();
        var runs = 0;
        var engine = Engine(FailingFirst(2, new FakeTransactionException(TransactionErrorKind.Transient), () => runs++), executor);

        var result = await engine.UpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.Applied.ShouldBe(["20260101000001"]);
        runs.ShouldBe(3);
        executor.Scopes.Count.ShouldBe(3);
        executor.Scopes.ShouldAllBe(s => s.Transactional);
        _store.States["20260101000001"].State.ShouldBe("applied");
        _store.History.Select(h => h.Event).ShouldBe(["applied"]);
        Warnings.Count().ShouldBe(2);
        Warnings.First().ShouldContain("transient transaction error (attempt 1)");
    }

    [Fact]
    public async Task Unknown_commit_result_retries_only_the_commit()
    {
        var commits = 0;
        var executor = new FakeExecutor
        {
            CommitFailure = () => ++commits == 1 ? new FakeTransactionException(TransactionErrorKind.UnknownCommitResult) : null,
        };
        var runs = 0;
        var engine = Engine(FailingFirst(0, new Exception(), () => runs++), executor);

        var result = await engine.UpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        runs.ShouldBe(1);
        commits.ShouldBe(2);
        executor.Scopes.Count.ShouldBe(1);
        _store.States["20260101000001"].State.ShouldBe("applied");
        _store.History.Select(h => h.Event).ShouldBe(["applied"]);
        Warnings.ShouldHaveSingleItem().ShouldContain("commit result is unknown (attempt 1)");
    }

    [Fact]
    public async Task Transient_error_on_commit_retries_the_whole_transaction()
    {
        var commits = 0;
        var executor = new FakeExecutor
        {
            CommitFailure = () => ++commits == 1 ? new FakeTransactionException(TransactionErrorKind.Transient) : null,
        };
        var runs = 0;
        var engine = Engine(FailingFirst(0, new Exception(), () => runs++), executor);

        var result = await engine.UpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        runs.ShouldBe(2);
        executor.Scopes.Count.ShouldBe(2);
        executor.Scopes[0].Aborted.ShouldBeTrue();
        _store.History.Select(h => h.Event).ShouldBe(["applied"]);
    }

    [Fact]
    public async Task Other_errors_are_not_retried()
    {
        var executor = new FakeExecutor();
        var runs = 0;
        var engine = Engine(FailingFirst(1, new InvalidOperationException("Boom."), () => runs++), executor);

        var result = await engine.UpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(1);
        runs.ShouldBe(1);
        _store.States.ShouldBeEmpty();
        _store.History.Select(h => h.Event).ShouldBe(["failed"]);
        Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Non_transactional_migrations_are_not_retried()
    {
        var executor = new FakeExecutor();
        var runs = 0;
        var engine = Engine(FailingFirst(1, new FakeTransactionException(TransactionErrorKind.Transient), () => runs++, TransactionMode.Never), executor);

        var result = await engine.UpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(1);
        runs.ShouldBe(1);
        _store.States["20260101000001"].State.ShouldBe("failed");
    }

    [Fact]
    public async Task Retrying_stops_after_the_retry_timeout()
    {
        var executor = new FakeExecutor();
        var runs = 0;
        var migration = Migrations.Create("20260101000001", "always-transient", up: async () =>
        {
            runs++;
            await Task.Delay(10);
            throw new FakeTransactionException(TransactionErrorKind.Transient);
        });
        var engine = Engine(migration, executor, retryTimeout: TimeSpan.FromMilliseconds(100));

        var result = await engine.UpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(1);
        result.Message!.ShouldContain("Fake Transient error.");
        runs.ShouldBeGreaterThan(1);
        runs.ShouldBeLessThan(50);
        _store.States.ShouldBeEmpty();
        _store.History.Select(h => h.Event).ShouldBe(["failed"]);
    }

    [Fact]
    public async Task Zero_retry_timeout_disables_retries()
    {
        var executor = new FakeExecutor();
        var runs = 0;
        var engine = Engine(FailingFirst(1, new FakeTransactionException(TransactionErrorKind.Transient), () => runs++), executor, retryTimeout: TimeSpan.Zero);

        var result = await engine.UpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(1);
        runs.ShouldBe(1);
    }

    [Fact]
    public async Task Down_retries_transient_errors_too()
    {
        var executor = new FakeExecutor();
        var downs = 0;
        var migration = Migrations.Create("20260101000001", "flaky-down", down: () =>
            ++downs == 1 ? Task.FromException(new FakeTransactionException(TransactionErrorKind.Transient)) : Task.CompletedTask);
        var engine = Engine(migration, executor);
        (await engine.UpAsync(null, CancellationToken.None)).ExitCode.ShouldBe(0);

        var result = await engine.DownAsync(new CommandArguments(), CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.RolledBack.ShouldBe(["20260101000001"]);
        downs.ShouldBe(2);
        _store.States.ShouldBeEmpty();
        _store.History.Select(h => h.Event).ShouldBe(["applied", "rolledBack"]);
    }
}
