using System.Text.Json;
using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.Protocol;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Engine;

public sealed class MigrationOutputTests
{
    private readonly FakeStore _store = new();
    private readonly RecordingSink _sink = new();

    private MigrationEngine Engine(IEnumerable<MigrationDescriptor> migrations, FakeExecutor executor, Action<EngineOptions>? configure = null)
    {
        var options = new EngineOptions
        {
            LockWaitTimeout = TimeSpan.Zero,
            TransactionRetryDelay = TimeSpan.Zero,
            Provenance = new Provenance { Host = "test-host", User = "tester", Tool = "0.2.0", Library = "1.0.0", Driver = "3.5.0", Runtime = "10.0.0" },
        };
        configure?.Invoke(options);
        return new MigrationEngine(_store, executor, migrations, options, _sink);
    }

    private static MigrationOutputInfo Output(string results) => new()
    {
        Results = JsonDocument.Parse(results).RootElement.Clone(),
        Operations = [new OperationRecord { Kind = "updateMany", Collection = "orders", Statement = JsonDocument.Parse("""{"filter":{"a":1}}""").RootElement.Clone(), Counts = [] }],
    };

    [Fact]
    public async Task Applied_output_is_stored_on_the_state_and_history_and_emitted()
    {
        var executor = new FakeExecutor { Output = (_, _) => Output("""{"skipped":3}""") };

        await Engine([Migrations.Create("20260101000001", "first")], executor).UpAsync(null, CancellationToken.None);

        var state = _store.States["20260101000001"].Output.ShouldNotBeNull();
        state.Results!.Value.GetRawText().ShouldBe("""{"skipped":3}""");
        state.Committed.ShouldBe(true);
        _store.History.Single().Output.ShouldBeSameAs(state);
        _sink.Events.Single(e => e.Type == EventTypes.MigrationCompleted).Output.ShouldBeSameAs(state);
    }

    [Fact]
    public async Task Status_shows_results_without_operations()
    {
        var executor = new FakeExecutor { Output = (_, _) => Output("""{"skipped":3}""") };
        var engine = Engine([Migrations.Create("20260101000001", "first")], executor);
        await engine.UpAsync(null, CancellationToken.None);

        var status = await engine.ExecuteAsync("status", new CommandArguments(), CancellationToken.None);

        var output = status.Migrations!.Single().Output.ShouldNotBeNull();
        output.Results!.Value.GetRawText().ShouldBe("""{"skipped":3}""");
        output.Operations.ShouldBeNull();
    }

    [Fact]
    public async Task A_failed_transaction_keeps_its_output_on_history_marked_as_not_committed()
    {
        var executor = new FakeExecutor { Output = (_, _) => Output("""{"processed":4200}""") };
        var migration = Migrations.Create("20260101000001", "first", up: () => throw new InvalidOperationException("boom"));

        var result = await Engine([migration], executor).UpAsync(null, CancellationToken.None);

        result.Message.ShouldBe("Migration 20260101000001 first failed: InvalidOperationException: boom");
        _store.States.ShouldBeEmpty();
        var failed = _store.History.Single();
        failed.Event.ShouldBe(HistoryEvents.Failed);
        failed.Error.ShouldBe("InvalidOperationException: boom");
        failed.Output!.Results!.Value.GetRawText().ShouldBe("""{"processed":4200}""");
        failed.Output.Committed.ShouldBe(false);
        _sink.Events.Single(e => e.Type == EventTypes.MigrationFailed).Output!.Committed.ShouldBe(false);
    }

    [Fact]
    public async Task A_commit_with_an_unknown_outcome_marks_the_output_as_unknown()
    {
        var executor = new FakeExecutor
        {
            Output = (_, _) => Output("""{"processed":4200}"""),
            CommitFailure = () => new FakeTransactionException(TransactionErrorKind.UnknownCommitResult),
        };

        await Engine([Migrations.Create("20260101000001", "first")], executor, o => o.TransactionRetryTimeout = TimeSpan.Zero)
            .UpAsync(null, CancellationToken.None);

        var failed = _store.History.Single();
        failed.Event.ShouldBe(HistoryEvents.Failed);
        failed.Output!.Committed.ShouldBeNull();
        _sink.Events.Single(e => e.Type == EventTypes.MigrationFailed).Output!.Committed.ShouldBeNull();
    }

    [Fact]
    public async Task A_failed_migration_without_a_transaction_keeps_its_output_on_the_failed_state_as_committed()
    {
        var executor = new FakeExecutor { Output = (_, _) => Output("""{"processed":10}""") };
        var migration = Migrations.Create("20260101000001", "first", transaction: TransactionMode.Never, up: () => throw new InvalidOperationException("boom"));

        await Engine([migration], executor).UpAsync(null, CancellationToken.None);

        var state = _store.States["20260101000001"];
        state.State.ShouldBe(MigrationStates.Failed);
        state.Output!.Committed.ShouldBe(true);
        state.Output.Results!.Value.GetRawText().ShouldBe("""{"processed":10}""");
        _store.History.Single().Output!.Committed.ShouldBe(true);
    }

    [Fact]
    public async Task Only_the_last_transaction_attempt_is_kept()
    {
        var attempts = 0;
        var executor = new FakeExecutor { Output = (_, _) => Output($$"""{"attempt":{{++attempts}}}""") };
        var migration = Migrations.Create("20260101000001", "first", up: () =>
            attempts == 1 ? throw new FakeTransactionException(TransactionErrorKind.Transient) : Task.CompletedTask);

        var result = await Engine([migration], executor).UpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        _store.States["20260101000001"].Output!.Results!.Value.GetRawText().ShouldBe("""{"attempt":2}""");
        _store.History.Single().Output!.Results!.Value.GetRawText().ShouldBe("""{"attempt":2}""");
    }

    [Fact]
    public async Task Rolled_back_output_goes_on_the_history_entry()
    {
        var executor = new FakeExecutor
        {
            Output = (_, run) => Output(run.Direction == MigrationDirection.Up ? """{"archived":5}""" : """{"unarchived":5}"""),
        };
        var engine = Engine([Migrations.Create("20260101000001", "first")], executor);
        await engine.UpAsync(null, CancellationToken.None);

        await engine.ExecuteAsync("down", new CommandArguments(), CancellationToken.None);

        _store.States.ShouldBeEmpty();
        var rolledBack = _store.History.Last();
        rolledBack.Event.ShouldBe(HistoryEvents.RolledBack);
        rolledBack.Output!.Results!.Value.GetRawText().ShouldBe("""{"unarchived":5}""");
        rolledBack.Output.Committed.ShouldBe(true);
    }

    [Fact]
    public async Task Statement_capture_follows_the_options_and_can_be_turned_off_per_run()
    {
        var executor = new FakeExecutor();
        var engine = Engine([Migrations.Create("20260101000001", "first")], executor, o => o.ResultsMaxBytes = 1000);

        await engine.ExecuteAsync("up", new CommandArguments { NoCaptureStatements = true }, CancellationToken.None);
        await engine.ExecuteAsync("down", new CommandArguments(), CancellationToken.None);

        executor.OutputOptions.ShouldBe([new OutputOptions(false, 1000), new OutputOptions(true, 1000)]);
    }

    [Fact]
    public async Task Show_returns_the_state_output_and_history_for_a_unique_prefix()
    {
        var executor = new FakeExecutor { Output = (_, _) => Output("""{"skipped":3}""") };
        var engine = Engine([Migrations.Create("20260101000001", "first"), Migrations.Create("20260202000001", "second")], executor);
        await engine.UpAsync(null, CancellationToken.None);

        var result = await engine.ExecuteAsync("show", new CommandArguments { Id = "202601" }, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        var show = result.Show.ShouldNotBeNull();
        show.Migration.Id.ShouldBe("20260101000001");
        show.Migration.State.ShouldBe(MigrationStates.Applied);
        show.Migration.Output!.Operations!.Single().Statement!.Value.GetRawText().ShouldBe("""{"filter":{"a":1}}""");
        show.Source.ShouldBe("csharp");
        show.Origin.ShouldBe("Test.first");
        show.CodeChecksum.ShouldBe("sha256:aaaa");
        show.User.ShouldBe("tester");
        show.Tool.ShouldBe("0.2.0");
        show.History.Select(h => h.Event).ShouldBe([HistoryEvents.Applied]);
    }

    [Fact]
    public async Task Show_can_leave_statements_out()
    {
        var executor = new FakeExecutor { Output = (_, _) => Output("""{"skipped":3}""") };
        var engine = Engine([Migrations.Create("20260101000001", "first")], executor);
        await engine.UpAsync(null, CancellationToken.None);

        var result = await engine.ExecuteAsync("show", new CommandArguments { Id = "20260101000001", NoStatements = true }, CancellationToken.None);

        var show = result.Show.ShouldNotBeNull();
        show.Migration.Output!.Operations!.Single().Statement.ShouldBeNull();
        show.History.Single().Output!.Operations!.Single().Statement.ShouldBeNull();
        _store.States["20260101000001"].Output!.Operations!.Single().Statement.ShouldNotBeNull();
    }

    [Fact]
    public async Task Show_finds_a_rolled_back_migration_through_its_history()
    {
        var executor = new FakeExecutor { Output = (_, _) => Output("""{"n":1}""") };
        var engine = Engine([Migrations.Create("20260101000001", "first")], executor);
        await engine.UpAsync(null, CancellationToken.None);
        await engine.ExecuteAsync("down", new CommandArguments(), CancellationToken.None);

        var show = (await engine.ExecuteAsync("show", new CommandArguments { Id = "20260101000001" }, CancellationToken.None)).Show.ShouldNotBeNull();

        show.Migration.State.ShouldBe(MigrationStates.Pending);
        show.Migration.Output.ShouldBeNull();
        show.History.Select(h => h.Event).ShouldBe([HistoryEvents.Applied, HistoryEvents.RolledBack]);
    }

    [Fact]
    public async Task Show_finds_a_rolled_back_migration_whose_code_has_gone_by_prefix()
    {
        var first = Engine([Migrations.Create("20260101000001", "first")], new FakeExecutor());
        await first.UpAsync(null, CancellationToken.None);
        await first.ExecuteAsync("down", new CommandArguments(), CancellationToken.None);
        var engine = Engine([Migrations.Create("20260202000001", "second")], new FakeExecutor());

        var show = (await engine.ExecuteAsync("show", new CommandArguments { Id = "202601" }, CancellationToken.None)).Show.ShouldNotBeNull();
        var ambiguous = await engine.ExecuteAsync("show", new CommandArguments { Id = "2026" }, CancellationToken.None);

        show.Migration.Id.ShouldBe("20260101000001");
        show.Migration.Name.ShouldBe("first");
        show.Migration.InCode.ShouldBeFalse();
        show.History.Select(h => h.Event).ShouldBe([HistoryEvents.Applied, HistoryEvents.RolledBack]);
        ambiguous.Message.ShouldBe("'2026' matches more than one migration: 20260101000001, 20260202000001.");
    }

    [Fact]
    public async Task Show_with_a_history_of_0_leaves_the_history_out()
    {
        var first = Engine([Migrations.Create("20260101000001", "first")], new FakeExecutor());
        await first.UpAsync(null, CancellationToken.None);
        await first.ExecuteAsync("down", new CommandArguments(), CancellationToken.None);
        var engine = Engine([], new FakeExecutor());

        var show = (await engine.ExecuteAsync("show", new CommandArguments { Id = "20260101000001", Limit = 0 }, CancellationToken.None)).Show.ShouldNotBeNull();

        show.Migration.Name.ShouldBe("first");
        show.History.ShouldBeEmpty();
    }

    [Fact]
    public async Task Show_lists_the_last_10_history_entries_unless_asked_for_more()
    {
        var engine = Engine([Migrations.Create("20260101000001", "first")], new FakeExecutor());
        for (var i = 0; i < 6; i++)
        {
            await engine.UpAsync(null, CancellationToken.None);
            await engine.ExecuteAsync("down", new CommandArguments(), CancellationToken.None);
        }

        (await engine.ExecuteAsync("show", new CommandArguments { Id = "20260101000001" }, CancellationToken.None)).Show!.History.Count.ShouldBe(10);
        (await engine.ExecuteAsync("show", new CommandArguments { Id = "20260101000001", Limit = 12 }, CancellationToken.None)).Show!.History.Count.ShouldBe(12);
    }

    [Theory]
    [InlineData("2026", "'2026' matches more than one migration: 20260101000001, 20260202000001.")]
    [InlineData("2027", "Migration '2027' was not found.")]
    public async Task Show_reports_ambiguous_and_unknown_ids(string id, string message)
    {
        var engine = Engine([Migrations.Create("20260101000001", "first"), Migrations.Create("20260202000001", "second")], new FakeExecutor());

        var result = await engine.ExecuteAsync("show", new CommandArguments { Id = id }, CancellationToken.None);

        result.ExitCode.ShouldBe(1);
        result.Message.ShouldBe(message);
    }
}
