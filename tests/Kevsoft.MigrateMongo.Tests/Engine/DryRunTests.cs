using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.Protocol;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Engine;

public sealed class DryRunTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);

    private readonly FakeStore _store = new(() => Now);
    private readonly RecordingSink _sink = new();
    private readonly FakeExecutor _executor = new();

    private static readonly MigrationDescriptor[] Three =
    [
        Migrations.Create("20260101000001", "first", transaction: TransactionMode.Never),
        Migrations.Create("20260101000002", "second"),
        Migrations.Create("20260101000003", "third"),
    ];

    private MigrationEngine Engine(IEnumerable<MigrationDescriptor> migrations, FakeExecutor? executor = null, Action<EngineOptions>? configure = null)
    {
        var options = new EngineOptions
        {
            LockWaitTimeout = TimeSpan.FromSeconds(30),
            Provenance = new Provenance { Host = "test-host", User = "tester", Library = "1.0.0", Driver = "3.5.0", Runtime = "10.0.0" },
        };
        configure?.Invoke(options);
        return new MigrationEngine(_store, executor ?? _executor, migrations, options, _sink, new FixedTime(Now));
    }

    private void Applied(string id, string name, int batch, string checksum = "sha256:aaaa", string state = "applied") =>
        _store.States[id] = new StoredMigrationState { Id = id, Name = name, Batch = batch, Checksum = checksum, State = state, RunId = "earlier" };

    private void ShouldHaveChangedNothing()
    {
        _executor.Calls.ShouldBeEmpty();
        _executor.Scopes.ShouldBeEmpty();
        _store.InitializeCount.ShouldBe(0);
        _store.ReleaseCount.ShouldBe(0);
        _store.History.ShouldBeEmpty();
        _sink.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Up_dry_run_lists_pending_migrations_without_changing_anything()
    {
        var result = await Engine(Three).ExecuteAsync("up", new CommandArguments { DryRun = true }, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.Plan!.Select(p => (p.Id, p.Name, p.Direction, p.Transactional)).ShouldBe([
            ("20260101000001", "first", "up", false),
            ("20260101000002", "second", "up", true),
            ("20260101000003", "third", "up", true),
        ]);
        result.Batch.ShouldBe(1);
        result.Applied.ShouldBeNull();
        result.Issues.ShouldBeEmpty();
        result.Lock.ShouldBeNull();
        result.Message.ShouldBe("Dry run: would apply 3 migration(s) as batch 1; nothing was changed.");
        _store.States.ShouldBeEmpty();
        _store.Lock.ShouldBeNull();
        ShouldHaveChangedNothing();
    }

    [Fact]
    public async Task Up_dry_run_respects_to_and_uses_the_next_batch()
    {
        Applied("20260101000001", "first", 4);

        var result = await Engine(Three).ExecuteAsync("up", new CommandArguments { DryRun = true, To = "20260101000002" }, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.Plan!.Select(p => p.Id).ShouldBe(["20260101000002"]);
        result.Batch.ShouldBe(5);
        result.Message.ShouldBe("Dry run: would apply 1 migration(s) as batch 5; nothing was changed.");
        _store.States.Keys.ShouldBe(["20260101000001"]);
        ShouldHaveChangedNothing();
    }

    [Fact]
    public async Task Up_dry_run_with_an_unknown_to_fails_like_a_real_run()
    {
        var result = await Engine(Three).DryRunUpAsync("20990101000000", CancellationToken.None);

        result.ExitCode.ShouldBe(1);
        result.Message.ShouldBe("Migration '20990101000000' was not found.");
        ShouldHaveChangedNothing();
    }

    [Fact]
    public async Task Up_dry_run_with_nothing_pending_succeeds()
    {
        Applied("20260101000001", "first", 1);

        var result = await Engine([Three[0]]).DryRunUpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.Plan.ShouldBeEmpty();
        result.Batch.ShouldBeNull();
        result.Message.ShouldBe("Dry run: nothing to apply; database is up to date.");
        ShouldHaveChangedNothing();
    }

    [Fact]
    public async Task Up_dry_run_reports_a_failed_migration_that_blocks_a_real_run()
    {
        Applied("20260101000001", "first", 1, state: "failed");

        var result = await Engine(Three).DryRunUpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(1);
        result.Plan.ShouldBeNull();
        result.Issues!.Single().Kind.ShouldBe("blocked");
        ShouldHaveChangedNothing();
    }

    [Fact]
    public async Task Up_dry_run_reports_validation_errors_with_the_validation_exit_code()
    {
        Applied("20260101000001", "first", 1, checksum: "sha256:changed");

        var result = await Engine(Three).DryRunUpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(4);
        result.Issues!.Single().Kind.ShouldBe("checksumMismatch");
        ShouldHaveChangedNothing();
    }

    [Fact]
    public async Task Up_dry_run_fails_when_a_migration_needs_a_transaction_the_server_cannot_provide()
    {
        var executor = new FakeExecutor(supportsTransactions: false);
        var migrations = new[] { Migrations.Create("20260101000001", "needs-tx", transaction: TransactionMode.Always) };

        var result = await Engine(migrations, executor).DryRunUpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(1);
        result.Message.ShouldBe("Migration 20260101000001 needs-tx requires a transaction, but the MongoDB deployment doesn't support transactions (a replica set or sharded cluster is required).");
        executor.Calls.ShouldBeEmpty();
        _store.InitializeCount.ShouldBe(0);
    }

    [Fact]
    public async Task Up_dry_run_warns_when_another_run_holds_the_lock_and_leaves_it_alone()
    {
        var held = new LockInfo { RunId = "other-run", Host = "ci-agent-7", AcquiredAt = Now.AddMinutes(-1), ExpiresAt = Now.AddMinutes(4) };
        _store.Lock = held;

        var result = await Engine(Three).DryRunUpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.Lock.ShouldBeSameAs(held);
        var issue = result.Issues.ShouldHaveSingleItem();
        issue.Severity.ShouldBe("warning");
        issue.Kind.ShouldBe("locked");
        issue.Message.ShouldBe("Migrations are locked by run other-run (ci-agent-7) until 2026-10-13 09:04:00Z; a real run would wait up to 30s for it.");
        _store.Lock.ShouldBeSameAs(held);
        _store.Lock!.ExpiresAt.ShouldBe(Now.AddMinutes(4));
        ShouldHaveChangedNothing();
    }

    [Fact]
    public async Task Up_dry_run_ignores_an_expired_lock()
    {
        _store.Lock = new LockInfo { RunId = "crashed-run", Host = "old-agent", AcquiredAt = Now.AddHours(-1), ExpiresAt = Now.AddMinutes(-55) };

        var result = await Engine(Three).DryRunUpAsync(null, CancellationToken.None);

        result.Issues.ShouldBeEmpty();
        result.Lock.ShouldBeNull();
        _store.Lock.ShouldNotBeNull();
    }

    [Fact]
    public async Task Plan_still_exits_with_pending_and_does_not_initialize_the_store()
    {
        var result = await Engine(Three).ExecuteAsync("plan", new CommandArguments(), CancellationToken.None);

        result.ExitCode.ShouldBe(2);
        result.Plan!.Count.ShouldBe(3);
        result.Message.ShouldBe("Would apply 3 migration(s) as batch 1.");
        ShouldHaveChangedNothing();
    }

    [Fact]
    public async Task Down_dry_run_batch_lists_the_last_batch_newest_first_without_changing_anything()
    {
        Applied("20260101000001", "first", 1);
        Applied("20260101000002", "second", 2);
        Applied("20260101000003", "third", 2);

        var result = await Engine(Three).ExecuteAsync("down", new CommandArguments { DryRun = true, Batch = true }, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.Plan!.Select(p => (p.Id, p.Name, p.Direction, p.Transactional)).ShouldBe([
            ("20260101000003", "third", "down", true),
            ("20260101000002", "second", "down", true),
        ]);
        result.RolledBack.ShouldBeNull();
        result.Message.ShouldBe("Dry run: would roll back 2 migration(s); nothing was changed.");
        _store.States.Keys.Order().ShouldBe(["20260101000001", "20260101000002", "20260101000003"]);
        _store.States.Values.ShouldAllBe(s => s.State == "applied");
        ShouldHaveChangedNothing();
    }

    [Theory]
    [InlineData(null, null, new[] { "20260101000003" })]
    [InlineData(2, null, new[] { "20260101000003", "20260101000002" })]
    [InlineData(null, "20260101000001", new[] { "20260101000003", "20260101000002" })]
    public async Task Down_dry_run_selects_the_same_migrations_as_a_real_run(int? count, string? to, string[] expected)
    {
        Applied("20260101000001", "first", 1);
        Applied("20260101000002", "second", 1);
        Applied("20260101000003", "third", 1);

        var result = await Engine(Three).ExecuteAsync("down", new CommandArguments { DryRun = true, Count = count, To = to }, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.Plan!.Select(p => p.Id).ShouldBe(expected);
        ShouldHaveChangedNothing();
    }

    [Fact]
    public async Task Down_dry_run_with_nothing_applied_succeeds()
    {
        var result = await Engine(Three).ExecuteAsync("down", new CommandArguments { DryRun = true }, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.Plan.ShouldBeEmpty();
        result.Message.ShouldBe("Dry run: nothing to roll back.");
        ShouldHaveChangedNothing();
    }

    [Fact]
    public async Task Down_dry_run_fails_when_the_migration_is_no_longer_in_the_code()
    {
        Applied("20260101000009", "deleted", 1);

        var result = await Engine(Three).ExecuteAsync("down", new CommandArguments { DryRun = true }, CancellationToken.None);

        result.ExitCode.ShouldBe(1);
        result.Message.ShouldBe("Cannot roll back 20260101000009 deleted: the migration is no longer in the code.");
        ShouldHaveChangedNothing();
    }

    [Fact]
    public async Task Down_dry_run_warns_when_the_lock_is_held()
    {
        Applied("20260101000001", "first", 1);
        _store.Lock = new LockInfo { RunId = "other-run", Host = "ci-agent-7", AcquiredAt = Now, ExpiresAt = Now.AddMinutes(5) };

        var result = await Engine(Three).ExecuteAsync("down", new CommandArguments { DryRun = true }, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.Issues.ShouldHaveSingleItem().Kind.ShouldBe("locked");
        result.Lock!.RunId.ShouldBe("other-run");
    }

    [Theory]
    [InlineData("baseline")]
    [InlineData("mark")]
    [InlineData("unlock")]
    [InlineData("plan")]
    public async Task Dry_run_is_refused_for_other_commands(string command)
    {
        var result = await Engine(Three).ExecuteAsync(command, new CommandArguments { DryRun = true, To = "20260101000003", Id = "20260101000001", Applied = true }, CancellationToken.None);

        result.ExitCode.ShouldBe(1);
        result.Message.ShouldBe($"--dry-run is only supported by 'up' and 'down', not '{command}'.");
        _store.States.ShouldBeEmpty();
        ShouldHaveChangedNothing();
    }
}
