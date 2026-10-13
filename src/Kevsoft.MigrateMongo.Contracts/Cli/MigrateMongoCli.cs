using System.CommandLine;
using Kevsoft.MigrateMongo.Protocol;

namespace Kevsoft.MigrateMongo.Cli;

/// <summary>A parsed command line, shared by the tool and the standalone runner.</summary>
public sealed class CliInvocation
{
    public string Command { get; init; } = "";

    public CommandArguments Arguments { get; init; } = new();

    public string? ConfigFile { get; init; }

    public string? Environment { get; init; }

    public bool Json { get; init; }

    public bool NoBuild { get; init; }

    public string? Runner { get; init; }

    /// <summary>Connect through <c>tunnel</c> without verifying the SSH server's host key (tool only).</summary>
    public bool InsecureSkipHostKeyCheck { get; init; }

    /// <summary><c>init --project</c> path.</summary>
    public string? Project { get; init; }

    /// <summary><c>create &lt;name&gt;</c>.</summary>
    public string? Name { get; init; }
}

/// <summary>
/// The command line definition. The tool and the standalone runner use the same definition so they accept the same
/// commands and options.
/// </summary>
public static class MigrateMongoCli
{
    /// <summary>Commands that need the migrations (and run inside the runner).</summary>
    public static readonly IReadOnlySet<string> RunnerCommands = new HashSet<string>(StringComparer.Ordinal)
    {
        "status", "plan", "up", "down", "validate", "baseline", "mark", "unlock", "history",
    };

    public static RootCommand Create(
        string description,
        bool includeToolCommands,
        Func<CliInvocation, CancellationToken, Task<int>> handler)
    {
        var file = new Option<string?>("--file", "-f") { Description = "Path to the config file (default: migrate-mongo.yaml).", Recursive = true };
        var env = new Option<string?>("--env", "-e") { Description = "Environment from the config file to use.", Recursive = true };
        var json = new Option<bool>("--json") { Description = "Write machine-readable JSON lines instead of text.", Recursive = true };
        var noBuild = new Option<bool>("--no-build") { Description = "Don't build the migrations project first.", Recursive = true };
        var runner = new Option<string?>("--runner") { Description = "Run a pre-built runner (a .dll or a bundle executable) instead of building the project.", Recursive = true };
        var insecure = new Option<bool>("--insecure-skip-host-key-check") { Description = "Don't verify the SSH tunnel server's host key (not recommended).", Recursive = true };

        var root = new RootCommand(description);
        root.Options.Add(file);
        root.Options.Add(env);
        root.Options.Add(json);
        if (includeToolCommands)
        {
            root.Options.Add(noBuild);
            root.Options.Add(runner);
            root.Options.Add(insecure);
        }

        CliInvocation Common(ParseResult p, string command, CommandArguments? arguments = null) => new()
        {
            Command = command,
            Arguments = arguments ?? new CommandArguments(),
            ConfigFile = p.GetValue(file),
            Environment = p.GetValue(env),
            Json = p.GetValue(json),
            NoBuild = includeToolCommands && p.GetValue(noBuild),
            Runner = includeToolCommands ? p.GetValue(runner) : null,
            InsecureSkipHostKeyCheck = includeToolCommands && p.GetValue(insecure),
        };

        void Add(Command command, Func<ParseResult, CliInvocation> build)
        {
            command.SetAction((p, ct) => handler(build(p), ct));
            root.Subcommands.Add(command);
        }

        if (includeToolCommands)
        {
            var projectOption = new Option<string?>("--project", "-p") { Description = "Where to create the migrations project.", DefaultValueFactory = _ => "Migrations" };
            Add(new Command("init", "Create a config file and a migrations project.") { projectOption },
                p => new CliInvocation { Command = "init", Project = p.GetValue(projectOption), ConfigFile = p.GetValue(file), Json = p.GetValue(json) });

            var nameArgument = new Argument<string>("name") { Description = "Name of the migration, e.g. AddEmailIndexToUsers." };
            Add(new Command("create", "Create a new migration file.") { nameArgument },
                p => new CliInvocation { Command = "create", Name = p.GetValue(nameArgument), ConfigFile = p.GetValue(file), Environment = p.GetValue(env), Json = p.GetValue(json) });

            Add(new Command("version", "Show the tool version."), p => new CliInvocation { Command = "version", Json = p.GetValue(json) });
        }

        var check = new Option<bool>("--check") { Description = "Exit with code 2 if there are pending migrations." };
        Add(new Command("status", "Show the state of every migration.") { check },
            p => Common(p, "status", new CommandArguments { Check = p.GetValue(check) }));

        Add(new Command("plan", "Show what 'up' would do. Exits with code 2 if there is pending work."), p => Common(p, "plan"));

        var upTo = new Option<string?>("--to") { Description = "Stop after this migration id." };
        var upDryRun = new Option<bool>("--dry-run") { Description = "Show what would be applied without changing anything (no lock, no writes, no migration code runs)." };
        Add(new Command("up", "Apply pending migrations.") { upTo, upDryRun },
            p => Common(p, "up", new CommandArguments { To = p.GetValue(upTo), DryRun = p.GetValue(upDryRun) }));

        var downTo = new Option<string?>("--to") { Description = "Roll back every migration after this id (exclusive)." };
        var count = new Option<int?>("--count", "-n") { Description = "Number of migrations to roll back (default 1)." };
        var batch = new Option<bool>("--batch") { Description = "Roll back the whole last batch." };
        var downDryRun = new Option<bool>("--dry-run") { Description = "Show what would be rolled back without changing anything." };
        var down = new Command("down", "Roll back applied migrations.") { downTo, count, batch, downDryRun };
        down.Validators.Add(r =>
        {
            var selected = (r.GetValue(downTo) is not null ? 1 : 0) + (r.GetValue(count) is not null ? 1 : 0) + (r.GetValue(batch) ? 1 : 0);
            if (selected > 1)
            {
                r.AddError("Use only one of --to, --count or --batch.");
            }

            if (r.GetValue(count) is < 1)
            {
                r.AddError("--count must be at least 1.");
            }
        });
        Add(down, p => Common(p, "down", new CommandArguments { To = p.GetValue(downTo), Count = p.GetValue(count), Batch = p.GetValue(batch), DryRun = p.GetValue(downDryRun) }));

        Add(new Command("validate", "Check applied migrations against the code (checksums, missing and out-of-order migrations)."), p => Common(p, "validate"));

        var baselineTo = new Option<string>("--to") { Description = "Record every migration up to and including this id as applied.", Required = true };
        Add(new Command("baseline", "Record migrations as applied without running them.") { baselineTo },
            p => Common(p, "baseline", new CommandArguments { To = p.GetValue(baselineTo) }));

        var markId = new Argument<string>("id") { Description = "Migration id." };
        var applied = new Option<bool>("--applied") { Description = "Record the migration as applied (also accepts its current checksum)." };
        var unapplied = new Option<bool>("--unapplied") { Description = "Remove the migration's state so it runs again." };
        var mark = new Command("mark", "Manually set a migration's state.") { markId, applied, unapplied };
        mark.Validators.Add(r =>
        {
            if (r.GetValue(applied) == r.GetValue(unapplied))
            {
                r.AddError("Specify exactly one of --applied or --unapplied.");
            }
        });
        Add(mark, p => Common(p, "mark", new CommandArguments { Id = p.GetValue(markId), Applied = p.GetValue(applied) }));

        var force = new Option<bool>("--force") { Description = "Remove the lock even if it hasn't expired." };
        Add(new Command("unlock", "Remove an expired (or, with --force, any) migration lock.") { force },
            p => Common(p, "unlock", new CommandArguments { Force = p.GetValue(force) }));

        var historyId = new Argument<string?>("id") { Description = "Only show this migration.", Arity = ArgumentArity.ZeroOrOne };
        var limit = new Option<int?>("--limit") { Description = "Maximum entries to show (default 50)." };
        Add(new Command("history", "Show the audit log.") { historyId, limit },
            p => Common(p, "history", new CommandArguments { Id = p.GetValue(historyId), Limit = p.GetValue(limit) }));

        return root;
    }
}
