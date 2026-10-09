using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.Protocol;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Engine;

public sealed class MigrationEngineTests
{
    private readonly FakeStore _store = new();
    private readonly RecordingSink _sink = new();

    private MigrationEngine Engine(
        IEnumerable<MigrationDescriptor> migrations,
        FakeExecutor? executor = null,
        Action<EngineOptions>? configure = null)
    {
        var options = new EngineOptions
        {
            LockWaitTimeout = TimeSpan.Zero,
            Provenance = new Provenance { Host = "test-host", User = "tester", Library = "1.0.0", Driver = "3.5.0", Runtime = "10.0.0" },
        };
        configure?.Invoke(options);
        return new MigrationEngine(_store, executor ?? new FakeExecutor(), migrations, options, _sink);
    }

    [Fact]
    public async Task Up_applies_pending_migrations_in_id_order_as_one_batch()
    {
        var executor = new FakeExecutor();
        var engine = Engine([Migrations.Create("20260101000002", "second"), Migrations.Create("20260101000001", "first")], executor);

        var result = await engine.UpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.Applied.ShouldBe(["20260101000001", "20260101000002"]);
        result.Batch.ShouldBe(1);
        executor.Calls.ShouldBe(["Up:20260101000001:tx", "Up:20260101000002:tx"]);
        _store.States.Keys.Order().ShouldBe(["20260101000001", "20260101000002"]);
        _store.States["20260101000001"].State.ShouldBe("applied");
        _store.States["20260101000001"].Batch.ShouldBe(1);
        _store.States["20260101000001"].Transactional.ShouldBeTrue();
        _store.States["20260101000001"].Checksum.ShouldBe("sha256:aaaa");
        _store.States["20260101000001"].AppliedBy.Host.ShouldBe("test-host");
        _store.History.Select(h => (h.MigrationId, h.Event)).ShouldBe([("20260101000001", "applied"), ("20260101000002", "applied")]);
        _store.Lock.ShouldBeNull();
        _store.ReleaseCount.ShouldBe(1);
    }

    [Fact]
    public async Task Up_uses_next_batch_number_for_later_runs()
    {
        await Engine([Migrations.Create("20260101000001", "first")]).UpAsync(null, CancellationToken.None);

        var result = await Engine([Migrations.Create("20260101000001", "first"), Migrations.Create("20260101000002", "second")])
            .UpAsync(null, CancellationToken.None);

        result.Applied.ShouldBe(["20260101000002"]);
        _store.States["20260101000002"].Batch.ShouldBe(2);
    }

    [Fact]
    public async Task Up_with_to_stops_after_the_given_migration()
    {
        var result = await Engine([
            Migrations.Create("20260101000001", "a"),
            Migrations.Create("20260101000002", "b"),
            Migrations.Create("20260101000003", "c"),
        ]).UpAsync("20260101000002", CancellationToken.None);

        result.Applied.ShouldBe(["20260101000001", "20260101000002"]);
        _store.States.ContainsKey("20260101000003").ShouldBeFalse();
    }

    [Fact]
    public async Task Up_with_nothing_pending_succeeds()
    {
        var result = await Engine([]).UpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.Applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task Failing_non_transactional_migration_is_recorded_as_failed_and_blocks_later_runs()
    {
        var migrations = new[]
        {
            Migrations.Create("20260101000001", "ok", transaction: TransactionMode.Never),
            Migrations.Create("20260101000002", "boom", transaction: TransactionMode.Never, up: () => throw new InvalidOperationException("kaboom")),
            Migrations.Create("20260101000003", "never-run", transaction: TransactionMode.Never),
        };

        var result = await Engine(migrations).UpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(1);
        result.Applied.ShouldBe(["20260101000001"]);
        result.Message!.ShouldContain("kaboom");
        _store.States["20260101000002"].State.ShouldBe("failed");
        _store.States["20260101000002"].Error.ShouldBe("InvalidOperationException: kaboom");
        _store.States.ContainsKey("20260101000003").ShouldBeFalse();
        _store.History.Last().Event.ShouldBe("failed");
        _sink.Events.ShouldContain(e => e.Type == "migrationFailed" && e.MigrationId == "20260101000002");

        var again = await Engine(migrations).UpAsync(null, CancellationToken.None);
        again.ExitCode.ShouldBe(1);
        again.Issues!.ShouldContain(i => i.Kind == "blocked" && i.MigrationId == "20260101000002");
    }

    [Fact]
    public async Task Failing_transactional_migration_leaves_no_state()
    {
        var result = await Engine([Migrations.Create("20260101000001", "boom", up: () => throw new InvalidOperationException("kaboom"))])
            .UpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(1);
        _store.States.ShouldBeEmpty();
        _store.History.Single().Event.ShouldBe("failed");
        _store.History.Single().Error.ShouldBe("InvalidOperationException: kaboom");
    }

    [Fact]
    public async Task Non_transactional_migration_is_written_as_applying_before_it_runs()
    {
        string? stateDuringRun = null;
        var migration = Migrations.Create("20260101000001", "observe", transaction: TransactionMode.Never, up: () =>
        {
            stateDuringRun = _store.States["20260101000001"].State;
            return Task.CompletedTask;
        });

        await Engine([migration]).UpAsync(null, CancellationToken.None);

        stateDuringRun.ShouldBe("applying");
        _store.States["20260101000001"].State.ShouldBe("applied");
        _store.States["20260101000001"].Transactional.ShouldBeFalse();
    }

    [Fact]
    public async Task Transaction_auto_falls_back_to_no_transaction_when_unsupported()
    {
        var executor = new FakeExecutor(supportsTransactions: false);

        await Engine([Migrations.Create("20260101000001", "a")], executor).UpAsync(null, CancellationToken.None);

        executor.Calls.ShouldBe(["Up:20260101000001:notx"]);
    }

    [Fact]
    public async Task Transaction_always_fails_when_unsupported()
    {
        var executor = new FakeExecutor(supportsTransactions: false);

        var result = await Engine([Migrations.Create("20260101000001", "a", transaction: TransactionMode.Always)], executor)
            .UpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(1);
        executor.Calls.ShouldBeEmpty();
        result.Message!.ShouldContain("requires a transaction");
    }

    [Fact]
    public async Task Project_transaction_never_disables_transactions()
    {
        var executor = new FakeExecutor();

        await Engine([Migrations.Create("20260101000001", "a")], executor, o => o.Transaction = "never").UpAsync(null, CancellationToken.None);

        executor.Calls.ShouldBe(["Up:20260101000001:notx"]);
    }

    [Fact]
    public async Task Checksum_mismatch_fails_up_with_validation_exit_code()
    {
        await Engine([Migrations.Create("20260101000001", "a", checksum: "sha256:old")]).UpAsync(null, CancellationToken.None);

        var result = await Engine([Migrations.Create("20260101000001", "a", checksum: "sha256:new"), Migrations.Create("20260101000002", "b")])
            .UpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(4);
        result.Issues!.ShouldContain(i => i.Kind == "checksumMismatch" && i.MigrationId == "20260101000001");
        _store.States.ContainsKey("20260101000002").ShouldBeFalse();
    }

    [Fact]
    public async Task Checksum_mismatch_only_warns_when_configured()
    {
        await Engine([Migrations.Create("20260101000001", "a", checksum: "sha256:old")]).UpAsync(null, CancellationToken.None);

        var result = await Engine(
                [Migrations.Create("20260101000001", "a", checksum: "sha256:new"), Migrations.Create("20260101000002", "b")],
                configure: o => o.OnChecksumMismatch = "warn")
            .UpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.Applied.ShouldBe(["20260101000002"]);
        result.Issues!.Single().Severity.ShouldBe("warning");
    }

    [Fact]
    public async Task Out_of_order_pending_migration_fails_unless_allowed()
    {
        await Engine([Migrations.Create("20260101000002", "b")]).UpAsync(null, CancellationToken.None);
        var migrations = new[] { Migrations.Create("20260101000001", "a"), Migrations.Create("20260101000002", "b") };

        var refused = await Engine(migrations).UpAsync(null, CancellationToken.None);
        refused.ExitCode.ShouldBe(4);
        refused.Issues!.Single().Kind.ShouldBe("outOfOrder");

        var allowed = await Engine(migrations, configure: o => o.AllowOutOfOrder = true).UpAsync(null, CancellationToken.None);
        allowed.ExitCode.ShouldBe(0);
        allowed.Applied.ShouldBe(["20260101000001"]);
    }

    [Fact]
    public async Task State_written_by_a_newer_schema_version_is_refused()
    {
        _store.States["20260101000001"] = new StoredMigrationState { Id = "20260101000001", V = 2, State = "applied", Batch = 1 };

        var result = await Engine([Migrations.Create("20260101000001", "a")]).UpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(1);
        result.Issues!.ShouldContain(i => i.Kind == "unsupportedState");
    }

    [Fact]
    public async Task Up_times_out_when_another_run_holds_the_lock()
    {
        _store.Lock = new LockInfo { RunId = "other", Host = "other-host", AcquiredAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(5) };

        var result = await Engine([Migrations.Create("20260101000001", "a")]).UpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(3);
        result.Lock!.RunId.ShouldBe("other");
        _store.States.ShouldBeEmpty();
        _store.Lock!.RunId.ShouldBe("other");
    }

    [Fact]
    public async Task Up_takes_over_an_expired_lock()
    {
        _store.Lock = new LockInfo { RunId = "dead", AcquiredAt = DateTime.UtcNow.AddHours(-1), ExpiresAt = DateTime.UtcNow.AddMinutes(-1) };

        var result = await Engine([Migrations.Create("20260101000001", "a")]).UpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        _store.Lock.ShouldBeNull();
    }

    [Fact]
    public async Task Down_rolls_back_the_last_migration_by_default()
    {
        var executor = new FakeExecutor();
        var migrations = new[] { Migrations.Create("20260101000001", "a"), Migrations.Create("20260101000002", "b") };
        await Engine(migrations).UpAsync(null, CancellationToken.None);

        var result = await Engine(migrations, executor).DownAsync(new CommandArguments(), CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.RolledBack.ShouldBe(["20260101000002"]);
        executor.Calls.ShouldBe(["Down:20260101000002:tx"]);
        _store.States.Keys.ShouldBe(["20260101000001"]);
        _store.History.Last().Event.ShouldBe("rolledBack");
    }

    [Fact]
    public async Task Down_batch_rolls_back_the_whole_last_batch_newest_first()
    {
        var first = new[] { Migrations.Create("20260101000001", "a") };
        var all = first.Concat([Migrations.Create("20260101000002", "b"), Migrations.Create("20260101000003", "c")]).ToArray();
        await Engine(first).UpAsync(null, CancellationToken.None);
        await Engine(all).UpAsync(null, CancellationToken.None);
        var executor = new FakeExecutor();

        var result = await Engine(all, executor).DownAsync(new CommandArguments { Batch = true }, CancellationToken.None);

        result.RolledBack.ShouldBe(["20260101000003", "20260101000002"]);
        executor.Calls.ShouldBe(["Down:20260101000003:tx", "Down:20260101000002:tx"]);
        _store.States.Keys.ShouldBe(["20260101000001"]);
    }

    [Fact]
    public async Task Down_to_rolls_back_everything_after_the_given_id()
    {
        var all = new[] { Migrations.Create("20260101000001", "a"), Migrations.Create("20260101000002", "b"), Migrations.Create("20260101000003", "c") };
        await Engine(all).UpAsync(null, CancellationToken.None);

        var result = await Engine(all).DownAsync(new CommandArguments { To = "20260101000001" }, CancellationToken.None);

        result.RolledBack.ShouldBe(["20260101000003", "20260101000002"]);
        _store.States.Keys.ShouldBe(["20260101000001"]);
    }

    [Fact]
    public async Task Down_count_rolls_back_that_many()
    {
        var all = new[] { Migrations.Create("20260101000001", "a"), Migrations.Create("20260101000002", "b"), Migrations.Create("20260101000003", "c") };
        await Engine(all).UpAsync(null, CancellationToken.None);

        var result = await Engine(all).DownAsync(new CommandArguments { Count = 2 }, CancellationToken.None);

        result.RolledBack.ShouldBe(["20260101000003", "20260101000002"]);
    }

    [Fact]
    public async Task Failing_non_transactional_down_marks_the_migration_failed()
    {
        var ok = Migrations.Create("20260101000001", "a", transaction: TransactionMode.Never);
        await Engine([ok]).UpAsync(null, CancellationToken.None);
        var failingDown = Migrations.Create("20260101000001", "a", transaction: TransactionMode.Never, down: () => throw new InvalidOperationException("nope"));

        var result = await Engine([failingDown]).DownAsync(new CommandArguments(), CancellationToken.None);

        result.ExitCode.ShouldBe(1);
        _store.States["20260101000001"].State.ShouldBe("failed");
        _store.States["20260101000001"].Direction.ShouldBe("down");
    }

    [Fact]
    public async Task Failing_transactional_down_keeps_the_migration_applied()
    {
        await Engine([Migrations.Create("20260101000001", "a")]).UpAsync(null, CancellationToken.None);
        var failingDown = Migrations.Create("20260101000001", "a", down: () => throw new InvalidOperationException("nope"));

        var result = await Engine([failingDown]).DownAsync(new CommandArguments(), CancellationToken.None);

        result.ExitCode.ShouldBe(1);
        _store.States["20260101000001"].State.ShouldBe("applied");
    }

    [Fact]
    public async Task Plan_reports_pending_work_with_exit_code_2_without_changing_anything()
    {
        var executor = new FakeExecutor();
        var result = await Engine([Migrations.Create("20260101000001", "a"), Migrations.Create("20260101000002", "b", transaction: TransactionMode.Never)], executor)
            .PlanAsync(CancellationToken.None);

        result.ExitCode.ShouldBe(2);
        result.Batch.ShouldBe(1);
        result.Plan!.Select(p => (p.Id, p.Transactional)).ShouldBe([("20260101000001", true), ("20260101000002", false)]);
        executor.Calls.ShouldBeEmpty();
        _store.States.ShouldBeEmpty();
        _store.Lock.ShouldBeNull();
    }

    [Fact]
    public async Task Status_lists_code_and_database_migrations()
    {
        await Engine([Migrations.Create("20260101000001", "a"), Migrations.Create("20260101000002", "removed")]).UpAsync(null, CancellationToken.None);

        var result = await Engine([Migrations.Create("20260101000001", "a", checksum: "sha256:changed"), Migrations.Create("20260101000003", "c")])
            .StatusAsync(check: true, CancellationToken.None);

        result.ExitCode.ShouldBe(2);
        result.Migrations!.Select(m => (m.Id, m.State, m.InCode, m.ChecksumMismatch)).ShouldBe([
            ("20260101000001", "applied", true, true),
            ("20260101000002", "applied", false, false),
            ("20260101000003", "pending", true, false),
        ]);
    }

    [Fact]
    public async Task Validate_reports_missing_migrations()
    {
        await Engine([Migrations.Create("20260101000001", "a")]).UpAsync(null, CancellationToken.None);

        var result = await Engine([]).ValidateAsync(CancellationToken.None);

        result.ExitCode.ShouldBe(4);
        result.Issues!.Single().Kind.ShouldBe("missing");
    }

    [Fact]
    public async Task Mark_applied_resolves_a_failed_migration_and_accepts_its_checksum()
    {
        var failing = Migrations.Create("20260101000001", "a", transaction: TransactionMode.Never, up: () => throw new InvalidOperationException("x"));
        await Engine([failing]).UpAsync(null, CancellationToken.None);

        var result = await Engine([Migrations.Create("20260101000001", "a", checksum: "sha256:fixed")]).MarkAsync("20260101000001", true, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        _store.States["20260101000001"].State.ShouldBe("applied");
        _store.States["20260101000001"].Checksum.ShouldBe("sha256:fixed");
        _store.States["20260101000001"].Error.ShouldBeNull();
        _store.History.Last().Event.ShouldBe("markedApplied");
    }

    [Fact]
    public async Task Mark_unapplied_removes_state_so_it_runs_again()
    {
        var migration = Migrations.Create("20260101000001", "a");
        await Engine([migration]).UpAsync(null, CancellationToken.None);

        await Engine([migration]).MarkAsync("20260101000001", false, CancellationToken.None);

        _store.States.ShouldBeEmpty();
        _store.History.Last().Event.ShouldBe("markedUnapplied");
        (await Engine([migration]).UpAsync(null, CancellationToken.None)).Applied.ShouldBe(["20260101000001"]);
    }

    [Fact]
    public async Task Baseline_records_migrations_as_applied_without_running_them()
    {
        var executor = new FakeExecutor();
        var all = new[] { Migrations.Create("20260101000001", "a"), Migrations.Create("20260101000002", "b"), Migrations.Create("20260101000003", "c") };

        var result = await Engine(all, executor).BaselineAsync("20260101000002", CancellationToken.None);

        result.Applied.ShouldBe(["20260101000001", "20260101000002"]);
        executor.Calls.ShouldBeEmpty();
        _store.States.Values.Select(s => (s.Id, s.State, s.Batch)).OrderBy(s => s.Id).ShouldBe([("20260101000001", "applied", 1), ("20260101000002", "applied", 1)]);
        _store.History.Select(h => h.Event).ShouldBe(["baselined", "baselined"]);
    }

    [Fact]
    public async Task Unlock_refuses_an_unexpired_lock_unless_forced()
    {
        _store.Lock = new LockInfo { RunId = "other", AcquiredAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(5) };

        (await Engine([]).UnlockAsync(force: false, CancellationToken.None)).ExitCode.ShouldBe(1);
        _store.Lock.ShouldNotBeNull();

        (await Engine([]).UnlockAsync(force: true, CancellationToken.None)).ExitCode.ShouldBe(0);
        _store.Lock.ShouldBeNull();
    }

    [Fact]
    public async Task Unlock_removes_an_expired_lock()
    {
        _store.Lock = new LockInfo { RunId = "dead", AcquiredAt = DateTime.UtcNow.AddHours(-1), ExpiresAt = DateTime.UtcNow.AddMinutes(-1) };

        (await Engine([]).UnlockAsync(force: false, CancellationToken.None)).ExitCode.ShouldBe(0);
        _store.Lock.ShouldBeNull();
    }

    [Fact]
    public void Duplicate_migration_ids_are_rejected()
    {
        Should.Throw<InvalidOperationException>(() => Engine([Migrations.Create("20260101000001", "a"), Migrations.Create("20260101000001", "b")]));
    }
}
