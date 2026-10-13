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
}
