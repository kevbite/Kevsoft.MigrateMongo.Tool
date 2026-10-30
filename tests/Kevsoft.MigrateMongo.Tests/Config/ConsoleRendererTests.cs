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
                Operations = [new OperationRecord { Kind = "updateMany", Counts = [] }, new OperationRecord { Kind = "insertMany", Counts = [], Error = "MongoBulkWriteException: boom" }],
                OperationsTruncated = 1,
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
                3 operation(s) recorded, 1 failed – migrate-mongo show 20261022120000

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
    public void Failed_commits_with_an_unknown_outcome_say_so()
    {
        var text = Render(r => r.Render(new RunnerEvent
        {
            Type = EventTypes.MigrationFailed,
            MigrationId = "20261022120000",
            Name = "tier-customers",
            Error = "MongoException: commit failed",
            Output = new MigrationOutputInfo { Results = Json("""{"processed":4200}"""), Committed = null },
        }));

        text.ShouldBe("""
            ✖ 20261022120000 tier-customers: MongoException: commit failed
                processed  4200
                (commit outcome unknown – check whether the changes were persisted)

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
                new OperationRecord
                {
                    Kind = "insertMany", Collection = "audit", Counts = new Dictionary<string, long> { ["inserted"] = 2 }, DurationMs = 40,
                    Error = "MongoBulkWriteException: A bulk write operation resulted in one or more errors.",
                },
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
              3. insertMany audit  inserted 2  (0.04s)
                   failed: MongoBulkWriteException: A bulk write operation resulted in one or more errors.

            History
             At                   ID              Name            Event    Batch  Run    Host       Error
             2026-10-22 12:31:03  20261022120000  tier-customers  applied  4      run-1  ci-runner

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Plan_shows_each_collections_changes()
    {
        var text = Render(r => r.RenderResult(new CommandResult
        {
            ExitCode = ExitCodes.ValidationFailed,
            Plan = [new PlannedMigration { Id = "20260101000001", Name = "dedupe", Direction = "up", Transactional = true }],
            Collections =
            [
                new CollectionInfo
                {
                    Name = "cases",
                    State = CollectionStates.Blocked,
                    Changes =
                    [
                        new IndexChangeInfo { Action = "create", Index = "companyId_1_status_1", Keys = Json("""{ "companyId": 1, "status": 1 }"""), Detail = "unique" },
                        new IndexChangeInfo { Action = "modify", Index = "deletedAt_1", Detail = "expireAfterSeconds 2592000 → 604800" },
                        new IndexChangeInfo { Action = "rebuild", Index = "by_ref", Detail = "sparse: false → true (drop, then create)", Blocked = true, Reason = "indexes.onRebuild is fail" },
                        new IndexChangeInfo { Action = "hide", Index = "legacy_1", Detail = "not declared" },
                        new IndexChangeInfo { Action = "keep", Index = "adhoc_1", Detail = "not declared (prune: report)" },
                    ],
                },
                new CollectionInfo { Name = "users", State = CollectionStates.InSync },
            ],
            Issues = [new ValidationIssue { Severity = "error", Kind = "collectionBlocked", Message = "cases: rebuilding by_ref is blocked." }],
            Message = "Would apply 1 migration(s) as batch 2 and change 1 collection(s).",
        }));

        text.ShouldBe("""
              ▶ 20260101000001  dedupe   (transaction)
              ⟳ cases
                  + create   companyId_1_status_1   { companyId: 1, status: 1 }  unique
                  ~ modify   deletedAt_1            expireAfterSeconds 2592000 → 604800
                  ↻ rebuild  by_ref                 sparse: false → true (drop, then create)   ✖ blocked: indexes.onRebuild is fail
                  - hide     legacy_1               not declared
                  ! keep     adhoc_1                not declared (prune: report)
              ⟳ users   in sync
            error: cases: rebuilding by_ref is blocked.
            ✖ Would apply 1 migration(s) as batch 2 and change 1 collection(s).

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Up_shows_each_collection_as_it_changes()
    {
        var text = Render(r =>
        {
            r.Render(new RunnerEvent { Type = EventTypes.CollectionStarted, Collection = "cases" });
            r.Render(new RunnerEvent
            {
                Type = EventTypes.CollectionCompleted,
                Collection = "cases",
                DurationMs = 38400,
                Changes = [new IndexChangeInfo { Action = "create", Index = "status_1", Keys = Json("""{ "status": 1 }""") }],
            });
            r.Render(new RunnerEvent { Type = EventTypes.CollectionCompleted, Collection = "users", Changes = [] });
            r.Render(new RunnerEvent { Type = EventTypes.CollectionSkipped, Collection = "orders", Message = "orders: superseded." });
            r.Render(new RunnerEvent { Type = EventTypes.CollectionFailed, Collection = "events", Error = "blocked." });
        });

        text.ShouldBe("""
            ⟳ cases
              + create   status_1   { status: 1 }
            ✔ cases   (38.40s)
            ✔ users   (in sync)
            ⚠ orders: superseded.
            ✖ events: blocked.

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Status_lists_collections_after_the_migrations()
    {
        var text = Render(r => r.RenderResult(new CommandResult
        {
            ExitCode = 0,
            Migrations = [new MigrationStatusInfo { Id = "20260101000001", Name = "seed", State = "applied", InCode = true, Batch = 1, AppliedAt = At }],
            Collections =
            [
                new CollectionInfo { Name = "cases", State = CollectionStates.InSync, ReconciledAt = At, Changes = [new IndexChangeInfo { Action = "keep", Index = "old_1" }] },
                new CollectionInfo
                {
                    Name = "users",
                    State = CollectionStates.Changes,
                    Changes = [new IndexChangeInfo { Action = "create", Index = "a_1" }, new IndexChangeInfo { Action = "create", Index = "b_1" }, new IndexChangeInfo { Action = "hide", Index = "c_1" }],
                },
            ],
            Message = "1 collection(s) out of line with their definitions.",
        }));

        text.ShouldBe("""
             ID              Name  State    Batch  Applied
             20260101000001  seed  applied  1      2026-10-22 12:31:03

             Collection  State    Reconciled           Changes
             cases       in sync  2026-10-22 12:31:03  1 kept
             users       changes                       2 create · 1 hide
            1 collection(s) out of line with their definitions.

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Show_prints_a_collections_indexes_pending_changes_and_history()
    {
        var text = Render(r => r.RenderResult(new CommandResult
        {
            ExitCode = 0,
            ShowCollection = new CollectionInfo
            {
                Name = "cases",
                Source = "csharp",
                Origin = "MyApp.Cases",
                State = CollectionStates.Changes,
                Declaration = "sha256:aaaa",
                ReconciledAt = At,
                User = "kev",
                Host = "ci-runner",
                DurationMs = 38400,
                RunId = "run-1",
                Indexes =
                [
                    new IndexInfo { Name = "_id_", Keys = Json("""{ "_id": 1 }"""), Status = "default" },
                    new IndexInfo { Name = "by_ref", Keys = Json("""{ "reference": 1 }"""), Options = Json("""{ "unique": true }"""), Status = "declared", DeclaredName = "reference_unique" },
                    new IndexInfo { Name = "legacy_1", Keys = Json("""{ "legacy": 1 }"""), Options = Json("""{ "hidden": true }"""), Status = "undeclared", HiddenAt = At },
                    new IndexInfo { Name = "adhoc_x", Keys = Json("""{ "x": -1 }"""), Status = "ignored" },
                ],
                Changes = [new IndexChangeInfo { Action = "create", Index = "status_1", Keys = Json("""{ "status": 1 }""") }],
                Output = new MigrationOutputInfo
                {
                    Committed = true,
                    Operations = [new OperationRecord { Kind = "createIndex", Collection = "cases", DurationMs = 38200, Statement = Json("""{ "keys": { "companyId": 1 } }""") }],
                },
                History = [new HistoryRecord { At = At, MigrationId = "cases", Name = "Cases", Kind = "collection", Event = "reconciled", RunId = "run-1", Host = "ci-runner" }],
            },
        }));

        text.ShouldBe("""
            cases (collection)
              State      changes
              Reconciled 2026-10-22 12:31:03 by kev@ci-runner (38.40s)
              Declared   sha256:aaaa
              Source     csharp MyApp.Cases
              Run        run-1

            Indexes
              _id_      { _id: 1 }
              by_ref    { reference: 1 }  unique  (declared as reference_unique)
              legacy_1  { legacy: 1 }  hidden  undeclared, hidden 2026-10-22 12:31
              adhoc_x   { x: -1 }  ignored

            Pending
              + create   status_1   { status: 1 }

            Last change

            Operations
              1. createIndex cases  (38.20s)
                   keys: { companyId: 1 }

            History
             At                   ID     Name   Event       Batch  Run    Host       Error
             2026-10-22 12:31:03  cases  Cases  reconciled         run-1  ci-runner

            """.ReplaceLineEndings("\n"));
    }
}
