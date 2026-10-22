using System.Text.Json;
using Kevsoft.MigrateMongo.Cli;
using Kevsoft.MigrateMongo.Protocol;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Config;

public sealed class ConsoleRendererTests
{
    [Fact]
    public void Plan_shows_the_direction_and_transaction_of_each_migration()
    {
        var output = new StringWriter();

        new ConsoleRenderer(output, color: false).RenderResult(new CommandResult
        {
            ExitCode = 0,
            Plan =
            [
                new PlannedMigration { Id = "20260101000003", Name = "third", Direction = "down", Transactional = true },
                new PlannedMigration { Id = "20260101000002", Name = "second", Direction = "down", Transactional = false },
            ],
            Message = "Dry run: would roll back 2 migration(s); nothing was changed.",
        });

        output.ToString().ReplaceLineEndings("\n").ShouldBe("""
              ◀ 20260101000003  third   (transaction)
              ◀ 20260101000002  second   (no transaction)
            Dry run: would roll back 2 migration(s); nothing was changed.

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Lock_warnings_are_shown_with_a_successful_dry_run()
    {
        var output = new StringWriter();

        new ConsoleRenderer(output, color: false).RenderResult(new CommandResult
        {
            ExitCode = 0,
            Plan = [new PlannedMigration { Id = "20260101000001", Name = "first", Direction = "up", Transactional = true }],
            Issues = [new ValidationIssue { Severity = "warning", Kind = "locked", Message = "Migrations are locked by run abc (ci) until 2026-10-13 09:04:00Z; a real run would wait up to 30s for it." }],
            Message = "Dry run: would apply 1 migration(s) as batch 1; nothing was changed.",
        });

        output.ToString().ReplaceLineEndings("\n").ShouldBe("""
              ▶ 20260101000001  first   (transaction)
            warning: Migrations are locked by run abc (ci) until 2026-10-13 09:04:00Z; a real run would wait up to 30s for it.
            Dry run: would apply 1 migration(s) as batch 1; nothing was changed.

            """.ReplaceLineEndings("\n"));
    }

    private static readonly DateTime At = new(2026, 10, 22, 12, 31, 3, DateTimeKind.Utc);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Render(Action<ConsoleRenderer> render)
    {
        var output = new StringWriter();
        render(new ConsoleRenderer(output, color: false));
        return output.ToString().ReplaceLineEndings("\n");
    }

    [Fact]
    public void Completed_migrations_list_their_results_and_point_at_show_for_operations()
    {
        var text = Render(r => r.Render(new RunnerEvent
        {
            Type = EventTypes.MigrationCompleted,
            MigrationId = "20261022120000",
            Name = "tier-customers",
            DurationMs = 840,
            Transactional = true,
            Output = new MigrationOutputInfo
            {
                Results = Json("""{"skipped":3,"archive":{"matched":1520,"modified":{"$numberLong":"1518"}},"ids":[1,2],"cutoff":{"$date":"2026-10-01T00:00:00Z"},"tier":"gold"}"""),
                Operations = [new OperationRecord { Kind = "updateMany", Counts = [] }],
                OperationsTruncated = 2,
                Committed = true,
            },
        }));

        text.ShouldBe("""
            ✔ 20261022120000 tier-customers   (0.84s, transaction)
                skipped  3
                archive  matched 1520 · modified 1518
                ids      [2 items]
                cutoff   2026-10-01T00:00:00Z
                tier     gold
                3 operation(s) recorded – migrate-mongo show 20261022120000

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Failed_transactions_say_their_results_were_not_persisted()
    {
        var text = Render(r => r.Render(new RunnerEvent
        {
            Type = EventTypes.MigrationFailed,
            MigrationId = "20261022120000",
            Name = "tier-customers",
            Error = "InvalidOperationException: boom",
            Output = new MigrationOutputInfo { Results = Json("""{"processed":4200}"""), Committed = false },
        }));

        text.ShouldBe("""
            ✖ 20261022120000 tier-customers: InvalidOperationException: boom
                processed  4200
                (transaction aborted – changes not persisted)

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Status_gets_a_results_column_only_when_a_migration_has_results()
    {
        var migrations = new List<MigrationStatusInfo>
        {
            new() { Id = "20260101000001", Name = "first", State = "applied", Batch = 1, AppliedAt = At, InCode = true, Output = new MigrationOutputInfo { Results = Json("""{"skipped":3,"note":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}"""), Committed = true } },
            new() { Id = "20260101000002", Name = "second", State = "pending", InCode = true },
        };

        Render(r => r.RenderResult(new CommandResult { ExitCode = 0, Migrations = migrations })).ShouldBe("""
             ID              Name    State    Batch  Applied              Results
             20260101000001  first   applied  1      2026-10-22 12:31:03  skipped: 3, note: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa…
             20260101000002  second  pending

            """.ReplaceLineEndings("\n"));

        migrations[0].Output = null;
        Render(r => r.RenderResult(new CommandResult { ExitCode = 0, Migrations = migrations })).ShouldBe("""
             ID              Name    State    Batch  Applied
             20260101000001  first   applied  1      2026-10-22 12:31:03
             20260101000002  second  pending

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Show_prints_the_details_results_notes_operations_and_history()
    {
        var output = new MigrationOutputInfo
        {
            Results = Json("""{"skipped":{"$numberInt":"2"},"tier":"gold"}"""),
            Notes = [new ResultNoteInfo { At = At, Level = "warning", Message = "2 customers were left untiered." }],
            NotesTruncated = 1,
            Operations =
            [
                new OperationRecord
                {
                    Kind = "updateMany", Collection = "orders", Label = "archive",
                    Statement = Json("""{"filter":{"status":"complete"},"update":{"$set":{"archived":true}}}"""),
                    Counts = new Dictionary<string, long> { ["matched"] = 1520, ["modified"] = 1518 }, DurationMs = 611,
                },
                new OperationRecord { Kind = "createIndex", Collection = "users", StatementTruncated = true, Counts = [], DurationMs = 198 },
            ],
            Committed = true,
        };
        var show = new ShowResult
        {
            Migration = new MigrationStatusInfo
            {
                Id = "20261022120000",
                Name = "tier-customers",
                State = "applied",
                Batch = 4,
                AppliedAt = At,
                Host = "ci-runner",
                Checksum = "sha256:aaaa",
                InCode = true,
                RunId = "run-1",
                Transactional = true,
                Output = output,
            },
            Direction = "up",
            User = "kev",
            DurationMs = 840,
            Source = "csharp",
            Origin = "MyApp.TierCustomers",
            CodeChecksum = "sha256:bbbb",
            Tool = "0.2.0",
            Library = "0.2.0",
            Driver = "3.5.0",
            History = [new HistoryRecord { At = At, MigrationId = "20261022120000", Name = "tier-customers", Event = "applied", Batch = 4, RunId = "run-1", Host = "ci-runner" }],
        };

        Render(r => r.RenderResult(new CommandResult { ExitCode = 0, Show = show })).ShouldBe("""
            20261022120000 tier-customers
              State     applied (batch 4, up, transaction)
              Applied   2026-10-22 12:31:03 by kev@ci-runner (0.84s)
              Checksum  sha256:aaaa (modified: the code is sha256:bbbb)
              Source    csharp MyApp.TierCustomers
              Versions  tool 0.2.0, library 0.2.0, driver 3.5.0
              Run       run-1

            Results
              skipped  2
              tier     "gold"

            Notes
              12:31:03  warning  2 customers were left untiered.
              (+1 more not recorded)

            Operations
              1. updateMany orders [archive]  matched 1520 · modified 1518  (0.61s)
                   filter: { status: "complete" }
                   update: { $set: { archived: true } }
              2. createIndex users  (0.20s)
                   (statement too large to record)

            History
             At                   ID              Name            Event    Batch  Run    Host       Error
             2026-10-22 12:31:03  20261022120000  tier-customers  applied  4      run-1  ci-runner

            """.ReplaceLineEndings("\n"));
    }
}
