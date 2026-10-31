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

    /// <summary>Connect through <c>tunnel</c> without verifying the SSH server's host key.</summary>
    public bool InsecureSkipHostKeyCheck { get; init; }

    /// <summary><c>init --project</c> path.</summary>
    public string? Project { get; init; }

    /// <summary><c>init --auth</c>: the authentication to set the project up for (<c>aws</c>), if any.</summary>
    public string? Auth { get; init; }

    /// <summary><c>create &lt;name&gt;</c>.</summary>
    public string? Name { get; init; }

    /// <summary><c>create --minimal</c>: use the minimal built-in template instead of the default or <c>migrationTemplate</c>.</summary>
    public bool Minimal { get; init; }

    /// <summary><c>bundle --runtime</c>: the runtime identifier to publish for (default: the current one).</summary>
    public string? RuntimeIdentifier { get; init; }

    /// <summary><c>bundle --output</c>: the directory to write the bundle to.</summary>
    public string? Output { get; init; }
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
        "status", "plan", "up", "down", "validate", "baseline", "mark", "unlock", "history", "show",
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
        root.Options.Add(insecure);
        if (includeToolCommands)
        {
            root.Options.Add(noBuild);
            root.Options.Add(runner);
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
            InsecureSkipHostKeyCheck = p.GetValue(insecure),
        };

        void Add(Command command, Func<ParseResult, CliInvocation> build)
        {
            command.SetAction((p, ct) => handler(build(p), ct));
            root.Subcommands.Add(command);
        }

        if (includeToolCommands)
        {
            var projectOption = new Option<string?>("--project", "-p") { Description = "Where to create the migrations project.", DefaultValueFactory = _ => "Migrations" };
            var authOption = new Option<string?>("--auth") { Description = "Set the project up for an authentication mechanism: aws (MongoDB AWS IAM, e.g. Atlas with an IAM role)." };
            authOption.AcceptOnlyFromAmong("aws");
            Add(new Command("init", "Create a config file and a migrations project.") { projectOption, authOption },
                p => new CliInvocation { Command = "init", Project = p.GetValue(projectOption), Auth = p.GetValue(authOption), ConfigFile = p.GetValue(file), Json = p.GetValue(json) });

            var nameArgument = new Argument<string>("name") { Description = "Name of the migration, e.g. AddEmailIndexToUsers." };
            var minimal = new Option<bool>("--minimal") { Description = "Use the minimal template, without the results and tracking examples." };
            Add(new Command("create", "Create a new migration file.") { nameArgument, minimal },
                p => new CliInvocation { Command = "create", Name = p.GetValue(nameArgument), Minimal = p.GetValue(minimal), ConfigFile = p.GetValue(file), Environment = p.GetValue(env), Json = p.GetValue(json) });

            var runtime = new Option<string?>("--runtime", "-r") { Description = "Runtime identifier to bundle for, e.g. linux-x64 (default: this machine's)." };
            var output = new Option<string>("--output", "-o") { Description = "Directory to write the bundle to.", DefaultValueFactory = _ => "bundle" };
            Add(new Command("bundle", "Publish the migrations project as a self-contained single-file executable for machines without .NET.") { runtime, output },
                p => new CliInvocation
                {
                    Command = "bundle",
                    RuntimeIdentifier = p.GetValue(runtime),
                    Output = p.GetValue(output),
                    ConfigFile = p.GetValue(file),
                    Environment = p.GetValue(env),
                    Json = p.GetValue(json),
                });

            Add(new Command("version", "Show the tool version."), p => new CliInvocation { Command = "version", Json = p.GetValue(json) });
        }

        var check = new Option<bool>("--check") { Description = "Exit with code 2 if there are pending migrations or collection changes." };
        Add(new Command("status", "Show the state of every migration and collection definition.") { check },
            p => Common(p, "status", new CommandArguments { Check = p.GetValue(check) }));

        var planCollections = new CollectionOptions();
        var plan = new Command("plan", "Show what 'up' would do. Exits with code 2 if there is pending work.");
        planCollections.AddTo(plan);
        Add(plan, p => Common(p, "plan", planCollections.Apply(p, new CommandArguments())));

        var upTo = new Option<string?>("--to") { Description = "Stop after this migration id." };
        var upDryRun = new Option<bool>("--dry-run") { Description = "Show what would be applied without changing anything (no lock, no writes, no migration code runs)." };
        var upNoCapture = NoCaptureStatementsOption();
        var upCollections = new CollectionOptions();
        var up = new Command("up", "Apply pending migrations, then bring collections in line with their definitions.") { upTo, upDryRun, upNoCapture };
        upCollections.AddTo(up);
        Add(up, p => Common(p, "up", upCollections.Apply(p, new CommandArguments
        {
            To = p.GetValue(upTo),
            DryRun = p.GetValue(upDryRun),
            NoCaptureStatements = p.GetValue(upNoCapture),
        })));

        var downTo = new Option<string?>("--to") { Description = "Roll back every migration after this id (exclusive)." };
        var count = new Option<int?>("--count", "-n") { Description = "Number of migrations to roll back (default 1)." };
        var batch = new Option<bool>("--batch") { Description = "Roll back the whole last batch." };
        var downDryRun = new Option<bool>("--dry-run") { Description = "Show what would be rolled back without changing anything." };
        var downNoCapture = NoCaptureStatementsOption();
        var down = new Command("down", "Roll back applied migrations.") { downTo, count, batch, downDryRun, downNoCapture };
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
        Add(down, p => Common(p, "down", new CommandArguments
        {
            To = p.GetValue(downTo),
            Count = p.GetValue(count),
            Batch = p.GetValue(batch),
            DryRun = p.GetValue(downDryRun),
            NoCaptureStatements = p.GetValue(downNoCapture),
        }));

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
        var history = new Command("history", "Show the audit log.") { historyId, limit };
        history.Validators.Add(r =>
        {
            if (r.GetValue(limit) is < 1)
            {
                r.AddError("--limit must be at least 1.");
            }
        });
        Add(history, p => Common(p, "history", new CommandArguments { Id = p.GetValue(historyId), Limit = p.GetValue(limit) }));

        var showId = new Argument<string>("id") { Description = "Migration id, or a unique prefix of one (e.g. its timestamp)." };
        var showHistory = new Option<int?>("--history") { Description = "Number of history entries to show (default 10, 0 for none)." };
        var noStatements = new Option<bool>("--no-statements") { Description = "Leave out the recorded filters, updates and commands." };
        var show = new Command("show", "Show one migration in detail: its state, results, notes, operations and history.") { showId, showHistory, noStatements };
        show.Validators.Add(r =>
        {
            if (r.GetValue(showHistory) is < 0)
            {
                r.AddError("--history can't be negative.");
            }
        });
        Add(show, p => Common(p, "show", new CommandArguments { Id = p.GetValue(showId), Limit = p.GetValue(showHistory), NoStatements = p.GetValue(noStatements) }));

        return root;
    }

    private static Option<bool> NoCaptureStatementsOption() =>
        new("--no-capture-statements") { Description = "Don't record the filters, updates and commands run through context.Track(...) (overrides results.captureStatements)." };

    /// <summary>The collection definition options shared by <c>up</c> and <c>plan</c>.</summary>
    private sealed class CollectionOptions
    {
        private readonly Option<string?> _only = new("--only") { Description = "Only apply migrations, or only collection definitions (default: both)." };
        private readonly Option<string[]> _allowRebuild = new("--allow-rebuild")
        {
            Description = "Let this index (collection.index) be dropped and created again even when onRebuild is fail. Repeat for more.",
            AllowMultipleArgumentsPerToken = false,
        };

        private readonly Option<string[]> _allowUnmatched = new("--allow-unmatched")
        {
            Description = "Change this collection even though none of its existing indexes match the declared ones. Repeat for more.",
            AllowMultipleArgumentsPerToken = false,
        };

        private readonly Option<bool> _allowSuperseded = new("--allow-superseded") { Description = "Apply collection definitions that a newer run has already replaced (e.g. when rolling back on purpose)." };

        public CollectionOptions() => _only.AcceptOnlyFromAmong(OnlyScopes.Migrations, OnlyScopes.Collections);

        public void AddTo(Command command)
        {
            command.Options.Add(_only);
            command.Options.Add(_allowRebuild);
            command.Options.Add(_allowUnmatched);
            command.Options.Add(_allowSuperseded);
        }

        public CommandArguments Apply(ParseResult p, CommandArguments arguments)
        {
            arguments.Only = p.GetValue(_only);
            arguments.AllowRebuild = p.GetValue(_allowRebuild) is { Length: > 0 } rebuild ? [.. rebuild] : null;
            arguments.AllowUnmatched = p.GetValue(_allowUnmatched) is { Length: > 0 } unmatched ? [.. unmatched] : null;
            arguments.AllowSuperseded = p.GetValue(_allowSuperseded);
            return arguments;
        }
    }
}
