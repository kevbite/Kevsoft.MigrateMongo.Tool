using System.Diagnostics;
using Kevsoft.MigrateMongo.Protocol;

namespace Kevsoft.MigrateMongo.Engine;

/// <summary>
/// Plans and executes migrations. Never touches the MongoDB driver directly; all database access goes through
/// <see cref="IMigrationStore"/> and <see cref="IMigrationExecutor"/>.
/// </summary>
public sealed class MigrationEngine
{
    private const int DefaultHistoryLimit = 50;
    private const int DefaultShowHistoryLimit = 10;

    private readonly IMigrationStore _store;
    private readonly IMigrationExecutor _executor;
    private readonly IReadOnlyList<MigrationDescriptor> _migrations;
    private readonly Dictionary<string, MigrationDescriptor> _byId;
    private readonly EngineOptions _options;
    private readonly IMigrationEventSink _events;
    private readonly TimeProvider _time;
    private bool? _supportsTransactions;

    public MigrationEngine(
        IMigrationStore store,
        IMigrationExecutor executor,
        IEnumerable<MigrationDescriptor> migrations,
        EngineOptions options,
        IMigrationEventSink events,
        TimeProvider? time = null)
    {
        _store = store;
        _executor = executor;
        _options = options;
        _events = events;
        _time = time ?? TimeProvider.System;
        _migrations = migrations.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();

        var duplicate = _migrations.GroupBy(m => m.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Migration id '{duplicate.Key}' is defined more than once ({string.Join(", ", duplicate.Select(d => d.Origin))}).");
        }

        _byId = _migrations.ToDictionary(m => m.Id, StringComparer.Ordinal);
    }

    public string RunId { get; } = Guid.NewGuid().ToString("N");

    public Task<CommandResult> ExecuteAsync(string command, CommandArguments arguments, CancellationToken cancellationToken) => command switch
    {
        _ when arguments.DryRun && command is not ("up" or "down") =>
            Task.FromResult(new CommandResult { ExitCode = ExitCodes.Failure, Message = $"--dry-run is only supported by 'up' and 'down', not '{command}'." }),
        "status" => StatusAsync(arguments.Check, cancellationToken),
        "plan" => PlanAsync(cancellationToken),
        "up" when arguments.DryRun => DryRunUpAsync(arguments.To, cancellationToken),
        "up" => UpAsync(arguments.To, OutputOptionsFor(arguments), cancellationToken),
        "down" when arguments.DryRun => DryRunDownAsync(arguments, cancellationToken),
        "down" => DownAsync(arguments, cancellationToken),
        "validate" => ValidateAsync(cancellationToken),
        "baseline" => BaselineAsync(arguments.To ?? "", cancellationToken),
        "mark" when arguments.Applied is null =>
            Task.FromResult(new CommandResult { ExitCode = ExitCodes.Failure, Message = "'mark' needs --applied or --unapplied." }),
        "mark" => MarkAsync(arguments.Id ?? "", arguments.Applied.Value, cancellationToken),
        "unlock" => UnlockAsync(arguments.Force, cancellationToken),
        "history" => HistoryAsync(arguments.Id, arguments.Limit, cancellationToken),
        "show" => ShowAsync(arguments.Id ?? "", arguments.Limit, arguments.NoStatements, cancellationToken),
        _ => Task.FromResult(new CommandResult { ExitCode = ExitCodes.Failure, Message = $"Unknown command '{command}'." }),
    };

    // status, validate, history, show and unlock only read (or just delete the lock), so they don't create the
    // collections' indexes: they work with a read-only database user and leave a database pointed at by mistake untouched.

    public async Task<CommandResult> StatusAsync(bool check, CancellationToken cancellationToken)
    {
        var states = await _store.GetStatesAsync(cancellationToken);
        var status = BuildStatus(states);
        var pending = status.Count(s => s.State == MigrationStates.Pending);
        return new CommandResult
        {
            ExitCode = check && pending > 0 ? ExitCodes.PendingMigrations : ExitCodes.Success,
            Migrations = status,
            Issues = Analyse(states, status),
            Message = pending == 0 ? "Database is up to date." : $"{pending} pending migration(s).",
        };
    }

    public async Task<CommandResult> ValidateAsync(CancellationToken cancellationToken)
    {
        var states = await _store.GetStatesAsync(cancellationToken);
        var issues = Analyse(states, BuildStatus(states));
        var errors = issues.Count(i => i.Severity == "error");
        return new CommandResult
        {
            ExitCode = errors > 0 ? ExitCodes.ValidationFailed : ExitCodes.Success,
            Issues = issues,
            Message = errors > 0 ? $"Validation failed with {errors} error(s)." : "Validation passed.",
        };
    }

    /// <summary>Shows what <c>up</c> would do. Exits with <see cref="ExitCodes.PendingMigrations"/> if there is pending work.</summary>
    public Task<CommandResult> PlanAsync(CancellationToken cancellationToken) => PreviewUpAsync(null, dryRun: false, cancellationToken);

    /// <summary>
    /// Reports what <c>up</c> would apply without changing anything: no lock, no state or history writes and no migration
    /// code runs. Exits with the code a real run would (barring the migrations themselves failing or a lock timeout).
    /// </summary>
    public Task<CommandResult> DryRunUpAsync(string? to, CancellationToken cancellationToken) => PreviewUpAsync(to, dryRun: true, cancellationToken);

    /// <summary>Reports what <c>down</c> would roll back without changing anything.</summary>
    public async Task<CommandResult> DryRunDownAsync(CommandArguments arguments, CancellationToken cancellationToken)
    {
        var states = await _store.GetStatesAsync(cancellationToken);
        var issues = Analyse(states, BuildStatus(states));
        if (BlockingResult(issues.Where(i => i.Kind is "blocked" or "unsupportedState").ToList()) is { } blocked)
        {
            return blocked;
        }

        if (SelectRollbackTargets(states, arguments, out var targets) is { } invalid)
        {
            return invalid;
        }

        if (targets.Count == 0)
        {
            return new CommandResult { ExitCode = ExitCodes.Success, Plan = [], Message = "Dry run: nothing to roll back." };
        }

        List<PlannedMigration> plan;
        try
        {
            plan = await PlanItemsAsync(targets.Select(t => _byId[t.Id]), "down", cancellationToken);
        }
        catch (MigrationEngineException e)
        {
            return new CommandResult { ExitCode = ExitCodes.Failure, Message = e.Message };
        }

        var lockIssues = new List<ValidationIssue>();
        var holder = await ReportHeldLockAsync(lockIssues, cancellationToken);
        return new CommandResult
        {
            ExitCode = ExitCodes.Success,
            Plan = plan,
            Issues = lockIssues,
            Lock = holder,
            Message = $"Dry run: would roll back {plan.Count} migration(s); nothing was changed.",
        };
    }

    // Read-only on purpose (no InitializeAsync, no lock) so it changes nothing and works with a read-only database user.
    private async Task<CommandResult> PreviewUpAsync(string? to, bool dryRun, CancellationToken cancellationToken)
    {
        if (to is not null && !_byId.ContainsKey(to))
        {
            return new CommandResult { ExitCode = ExitCodes.Failure, Message = $"Migration '{to}' was not found." };
        }

        var states = await _store.GetStatesAsync(cancellationToken);
        var issues = Analyse(states, BuildStatus(states));
        if (BlockingResult(issues) is { } blocked)
        {
            return blocked;
        }

        List<PlannedMigration> plan;
        try
        {
            plan = await PlanItemsAsync(PendingMigrations(states, to), "up", cancellationToken);
        }
        catch (MigrationEngineException e)
        {
            return new CommandResult { ExitCode = ExitCodes.Failure, Issues = issues, Message = e.Message };
        }

        var holder = await ReportHeldLockAsync(issues, cancellationToken);
        var batch = NextBatch(states);
        return new CommandResult
        {
            ExitCode = plan.Count > 0 && !dryRun ? ExitCodes.PendingMigrations : ExitCodes.Success,
            Plan = plan,
            Batch = plan.Count > 0 ? batch : null,
            Issues = issues,
            Lock = holder,
            Message = (plan.Count, dryRun) switch
            {
                (0, true) => "Dry run: nothing to apply; database is up to date.",
                (0, false) => "Nothing to apply; database is up to date.",
                (_, true) => $"Dry run: would apply {plan.Count} migration(s) as batch {batch}; nothing was changed.",
                _ => $"Would apply {plan.Count} migration(s) as batch {batch}.",
            },
        };
    }

    private async Task<List<PlannedMigration>> PlanItemsAsync(IEnumerable<MigrationDescriptor> migrations, string direction, CancellationToken cancellationToken)
    {
        var plan = new List<PlannedMigration>();
        foreach (var migration in migrations)
        {
            plan.Add(new PlannedMigration
            {
                Id = migration.Id,
                Name = migration.Name,
                Direction = direction,
                Transactional = await DecideTransactionalAsync(migration, cancellationToken),
            });
        }

        return plan;
    }

    /// <summary>Adds a warning if another run currently holds the lock, as a real run would have to wait for it.</summary>
    private async Task<LockInfo?> ReportHeldLockAsync(List<ValidationIssue> issues, CancellationToken cancellationToken)
    {
        var holder = await _store.GetLockAsync(cancellationToken);
        if (holder is null || holder.Expired)
        {
            return null;
        }

        issues.Add(Issue("warning", "locked", null,
            $"Migrations are locked by run {holder.RunId} ({holder.Host}) until {holder.ExpiresAt:u}; a real run would wait up to {_options.LockWaitTimeout.TotalSeconds:0}s for it."));
        return holder;
    }

    public Task<CommandResult> UpAsync(string? to, CancellationToken cancellationToken) =>
        UpAsync(to, OutputOptionsFor(null), cancellationToken);

    private async Task<CommandResult> UpAsync(string? to, OutputOptions outputOptions, CancellationToken cancellationToken)
    {
        if (to is not null && !_byId.ContainsKey(to))
        {
            return new CommandResult { ExitCode = ExitCodes.Failure, Message = $"Migration '{to}' was not found." };
        }

        await _store.InitializeAsync(cancellationToken);
        return await WithLockAsync(async ct =>
        {
            var states = await _store.GetStatesAsync(ct);
            var issues = Analyse(states, BuildStatus(states));
            if (BlockingResult(issues) is { } blocked)
            {
                return blocked;
            }

            var pending = PendingMigrations(states, to);
            if (pending.Count == 0)
            {
                return new CommandResult { ExitCode = ExitCodes.Success, Applied = [], Issues = issues, Message = "Nothing to apply; database is up to date." };
            }

            var batch = NextBatch(states);
            var applied = new List<string>();
            foreach (var migration in pending)
            {
                if (LockLost(ct, cancellationToken) is { } lost)
                {
                    lost.Batch = batch;
                    lost.Applied = applied;
                    lost.Issues = issues;
                    return lost;
                }

                var error = await ApplyAsync(migration, batch, outputOptions, ct);
                if (error is not null)
                {
                    return new CommandResult
                    {
                        ExitCode = ExitCodes.Failure,
                        Batch = batch,
                        Applied = applied,
                        Issues = issues,
                        Message = $"Migration {migration.Id} {migration.Name} failed: {error}",
                    };
                }

                applied.Add(migration.Id);
            }

            return new CommandResult
            {
                ExitCode = ExitCodes.Success,
                Batch = batch,
                Applied = applied,
                Issues = issues,
                Message = $"Applied {applied.Count} migration(s) in batch {batch}.",
            };
        }, cancellationToken);
    }

    public async Task<CommandResult> DownAsync(CommandArguments arguments, CancellationToken cancellationToken)
    {
        await _store.InitializeAsync(cancellationToken);
        return await WithLockAsync(async ct =>
        {
            var states = await _store.GetStatesAsync(ct);
            var issues = Analyse(states, BuildStatus(states));
            if (BlockingResult(issues.Where(i => i.Kind is "blocked" or "unsupportedState").ToList()) is { } blocked)
            {
                return blocked;
            }

            if (SelectRollbackTargets(states, arguments, out var targets) is { } invalid)
            {
                return invalid;
            }

            if (targets.Count == 0)
            {
                return new CommandResult { ExitCode = ExitCodes.Success, RolledBack = [], Message = "Nothing to roll back." };
            }

            var rolledBack = new List<string>();
            foreach (var state in targets)
            {
                if (LockLost(ct, cancellationToken) is { } lost)
                {
                    lost.RolledBack = rolledBack;
                    return lost;
                }

                var error = await RollBackAsync(_byId[state.Id], state, OutputOptionsFor(arguments), ct);
                if (error is not null)
                {
                    return new CommandResult
                    {
                        ExitCode = ExitCodes.Failure,
                        RolledBack = rolledBack,
                        Message = $"Rolling back {state.Id} {state.Name} failed: {error}",
                    };
                }

                rolledBack.Add(state.Id);
            }

            return new CommandResult
            {
                ExitCode = ExitCodes.Success,
                RolledBack = rolledBack,
                Message = $"Rolled back {rolledBack.Count} migration(s).",
            };
        }, cancellationToken);
    }

    public async Task<CommandResult> MarkAsync(string id, bool applied, CancellationToken cancellationToken)
    {
        await _store.InitializeAsync(cancellationToken);
        return await WithLockAsync(async ct =>
        {
            var states = await _store.GetStatesAsync(ct);
            if (BlockingResult(UnsupportedStates(states)) is { } unsupported)
            {
                return unsupported;
            }

            var existing = states.FirstOrDefault(s => s.Id == id);
            _byId.TryGetValue(id, out var migration);
            if (existing is null && migration is null)
            {
                return new CommandResult { ExitCode = ExitCodes.Failure, Message = $"Migration '{id}' was not found." };
            }

            var now = Now;
            if (!applied)
            {
                if (existing is null)
                {
                    return new CommandResult { ExitCode = ExitCodes.Success, Message = $"Migration {id} is already unapplied." };
                }

                await _store.DeleteStateAsync(id, null, ct);
                await _store.AppendHistoryAsync(History(id, existing.Name, HistoryEvents.MarkedUnapplied, "down", existing.Batch, existing.Checksum, null, null), null, ct);
                return new CommandResult { ExitCode = ExitCodes.Success, Message = $"Marked {id} as unapplied." };
            }

            var state = existing ?? new StoredMigrationState { Id = id, Batch = NextBatch(states), AppliedAt = now };
            if (migration is not null)
            {
                state.Name = migration.Name;
                state.Source = migration.Source;
                state.Origin = migration.Origin;
                state.Checksum = migration.Checksum;
            }

            state.V = StoredMigrationState.CurrentSchemaVersion;
            state.State = MigrationStates.Applied;
            state.Direction = "up";
            state.AppliedAt ??= now;
            state.UpdatedAt = now;
            state.Error = null;
            state.RunId = RunId;
            state.AppliedBy = _options.Provenance;
            await _store.SaveStateAsync(state, null, ct);
            await _store.AppendHistoryAsync(History(id, state.Name, HistoryEvents.MarkedApplied, "up", state.Batch, state.Checksum, null, null), null, ct);
            return new CommandResult { ExitCode = ExitCodes.Success, Batch = state.Batch, Message = $"Marked {id} as applied." };
        }, cancellationToken);
    }

    public async Task<CommandResult> BaselineAsync(string to, CancellationToken cancellationToken)
    {
        if (!_byId.ContainsKey(to))
        {
            return new CommandResult { ExitCode = ExitCodes.Failure, Message = $"Migration '{to}' was not found." };
        }

        await _store.InitializeAsync(cancellationToken);
        return await WithLockAsync(async ct =>
        {
            var states = await _store.GetStatesAsync(ct);
            if (BlockingResult(UnsupportedStates(states)) is { } unsupported)
            {
                return unsupported;
            }

            var known = states.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            var targets = _migrations.Where(m => string.CompareOrdinal(m.Id, to) <= 0 && !known.Contains(m.Id)).ToList();
            if (targets.Count == 0)
            {
                return new CommandResult { ExitCode = ExitCodes.Success, Applied = [], Message = "Nothing to baseline." };
            }

            var batch = NextBatch(states);
            foreach (var migration in targets)
            {
                var state = NewState(migration, MigrationStates.Applied, batch, transactional: false);
                state.DurationMs = 0;
                await _store.SaveStateAsync(state, null, ct);
                await _store.AppendHistoryAsync(History(migration.Id, migration.Name, HistoryEvents.Baselined, "up", batch, migration.Checksum, null, null), null, ct);
            }

            return new CommandResult
            {
                ExitCode = ExitCodes.Success,
                Batch = batch,
                Applied = targets.Select(t => t.Id).ToList(),
                Message = $"Baselined {targets.Count} migration(s) in batch {batch}.",
            };
        }, cancellationToken);
    }

    public async Task<CommandResult> UnlockAsync(bool force, CancellationToken cancellationToken)
    {
        var current = await _store.GetLockAsync(cancellationToken);
        if (current is null)
        {
            return new CommandResult { ExitCode = ExitCodes.Success, Message = "No lock is held." };
        }

        if (!force && !current.Expired)
        {
            return new CommandResult
            {
                ExitCode = ExitCodes.Failure,
                Lock = current,
                Message = $"The lock is held by run {current.RunId} ({current.Host}) and expires at {current.ExpiresAt:u}. Use --force to remove it anyway.",
            };
        }

        var removed = await _store.DeleteLockAsync(onlyIfExpired: !force, cancellationToken);
        return new CommandResult
        {
            ExitCode = removed ? ExitCodes.Success : ExitCodes.Failure,
            Lock = current,
            Message = removed ? $"Removed lock held by run {current.RunId} ({current.Host})." : "The lock changed while removing it; try again.",
        };
    }

    public async Task<CommandResult> HistoryAsync(string? id, int? limit, CancellationToken cancellationToken)
    {
        if (limit is < 1)
        {
            return new CommandResult { ExitCode = ExitCodes.Failure, Message = "--limit must be at least 1." };
        }

        var history = await _store.GetHistoryAsync(id, limit ?? DefaultHistoryLimit, cancellationToken);
        return new CommandResult { ExitCode = ExitCodes.Success, History = history.ToList() };
    }

    /// <summary>Full detail for one migration: its state, everything its last run recorded, and its recent history.</summary>
    /// <param name="id">The migration id, or a unique prefix of it.</param>
    /// <param name="historyLimit">How many history entries to include (default 10, 0 for none).</param>
    /// <param name="noStatements">Leave the recorded statements out.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<CommandResult> ShowAsync(string id, int? historyLimit, bool noStatements, CancellationToken cancellationToken)
    {
        var states = await _store.GetStatesAsync(cancellationToken);

        // A rolled-back migration whose code has gone is only in the history.
        var historyIds = id.Length > 0 ? await _store.GetHistoryIdsAsync(id, cancellationToken) : [];
        var known = _byId.Keys.Concat(states.Select(s => s.Id)).Concat(historyIds).Distinct(StringComparer.Ordinal).ToList();
        var resolved = known.Contains(id, StringComparer.Ordinal) ? id : null;
        if (resolved is null)
        {
            var candidates = known.Where(k => k.StartsWith(id, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (id.Length > 0 && candidates.Count > 1)
            {
                return new CommandResult { ExitCode = ExitCodes.Failure, Message = $"'{id}' matches more than one migration: {string.Join(", ", candidates)}." };
            }

            resolved = id.Length > 0 && candidates.Count == 1 ? candidates[0] : null;
        }

        if (resolved is null)
        {
            return new CommandResult { ExitCode = ExitCodes.Failure, Message = $"Migration '{id}' was not found." };
        }

        // The store treats a limit of 0 as no limit, so --history 0 doesn't query at all.
        var limit = historyLimit ?? DefaultShowHistoryLimit;
        var history = limit > 0 ? await _store.GetHistoryAsync(resolved, limit, cancellationToken) : [];
        var state = states.FirstOrDefault(s => s.Id == resolved);
        _byId.TryGetValue(resolved, out var migration);
        MigrationStatusInfo status;
        if (state is null && migration is null)
        {
            var latest = history.Count > 0 ? history : await _store.GetHistoryAsync(resolved, 1, cancellationToken);
            status = new MigrationStatusInfo { Id = resolved, Name = latest[^1].Name, State = MigrationStates.Pending };
        }
        else
        {
            status = BuildStatus(state is null ? [] : [state]).First(s => s.Id == resolved);
        }

        status.Output = noStatements ? WithoutStatements(state?.Output) : state?.Output;

        return new CommandResult
        {
            ExitCode = ExitCodes.Success,
            Show = new ShowResult
            {
                Migration = status,
                Source = state?.Source ?? migration?.Source,
                Origin = state?.Origin ?? migration?.Origin,
                Direction = state?.Direction,
                DurationMs = state?.DurationMs,
                CodeChecksum = migration?.Checksum,
                User = state?.AppliedBy.User,
                Tool = state?.AppliedBy.Tool,
                Library = state?.AppliedBy.Library,
                Driver = state?.AppliedBy.Driver,
                History = noStatements ? history.Select(h => { h.Output = WithoutStatements(h.Output); return h; }).ToList() : history.ToList(),
            },
        };
    }

    private static MigrationOutputInfo? WithoutOperations(MigrationOutputInfo? output) => output is null ? null : new MigrationOutputInfo
    {
        Results = output.Results,
        Notes = output.Notes,
        NotesTruncated = output.NotesTruncated,
        Committed = output.Committed,
    };

    private static MigrationOutputInfo? WithoutStatements(MigrationOutputInfo? output) => output is null ? null : new MigrationOutputInfo
    {
        Results = output.Results,
        Notes = output.Notes,
        NotesTruncated = output.NotesTruncated,
        Operations = output.Operations?.Select(o => new OperationRecord
        {
            Kind = o.Kind,
            Collection = o.Collection,
            Label = o.Label,
            Counts = o.Counts,
            Error = o.Error,
            DurationMs = o.DurationMs,
            At = o.At,
        }).ToList(),
        OperationsTruncated = output.OperationsTruncated,
        Committed = output.Committed,
    };

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private List<MigrationStatusInfo> BuildStatus(IReadOnlyList<StoredMigrationState> states)
    {
        var stateById = states.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var ids = _byId.Keys.Concat(stateById.Keys).Distinct().OrderBy(i => i, StringComparer.Ordinal);
        var result = new List<MigrationStatusInfo>();
        foreach (var id in ids)
        {
            _byId.TryGetValue(id, out var migration);
            stateById.TryGetValue(id, out var state);
            result.Add(new MigrationStatusInfo
            {
                Id = id,
                Name = migration?.Name ?? state!.Name,
                State = state?.State ?? MigrationStates.Pending,
                InCode = migration is not null,
                Batch = state?.Batch,
                AppliedAt = state?.AppliedAt,
                Checksum = state?.Checksum ?? migration?.Checksum,
                ChecksumMismatch = migration is not null && state is { State: MigrationStates.Applied } && state.Checksum != migration.Checksum,
                Transactional = state?.Transactional,
                Error = state?.Error,
                RunId = state?.RunId,
                Host = state?.AppliedBy.Host,
                Output = WithoutOperations(state?.Output),
            });
        }

        return result;
    }

    private List<ValidationIssue> Analyse(IReadOnlyList<StoredMigrationState> states, List<MigrationStatusInfo> status)
    {
        var issues = UnsupportedStates(states);
        foreach (var state in states.Where(s => s.State is MigrationStates.Applying or MigrationStates.RollingBack or MigrationStates.Failed))
        {
            var what = state.State == MigrationStates.Failed ? $"failed ({state.Error})" : $"in state '{state.State}' from run {state.RunId} on {state.AppliedBy.Host}";
            issues.Add(Issue("error", "blocked", state.Id,
                $"{state.Id} {state.Name} is {what}. Inspect the data, then run `mark {state.Id} --applied` or `mark {state.Id} --unapplied`."));
        }

        if (_options.OnChecksumMismatch != "ignore")
        {
            var severity = _options.OnChecksumMismatch == "warn" ? "warning" : "error";
            foreach (var item in status.Where(s => s.ChecksumMismatch))
            {
                issues.Add(Issue(severity, "checksumMismatch", item.Id,
                    $"{item.Id} {item.Name} was modified after being applied (applied {item.Checksum}, current {_byId[item.Id].Checksum}). " +
                    $"Create a new migration instead, or run `mark {item.Id} --applied` to accept the change."));
            }
        }

        foreach (var item in status.Where(s => !s.InCode && s.State == MigrationStates.Applied))
        {
            issues.Add(Issue("error", "missing", item.Id,
                $"{item.Id} {item.Name} is applied but no longer exists in the code. Restore it, or run `mark {item.Id} --unapplied`."));
        }

        if (!_options.AllowOutOfOrder)
        {
            var latestApplied = states.Where(s => s.State == MigrationStates.Applied).Select(s => s.Id).DefaultIfEmpty("").Max(StringComparer.Ordinal)!;
            foreach (var item in status.Where(s => s.State == MigrationStates.Pending && string.CompareOrdinal(s.Id, latestApplied) < 0))
            {
                issues.Add(Issue("error", "outOfOrder", item.Id,
                    $"{item.Id} {item.Name} is pending but is older than the latest applied migration {latestApplied}. " +
                    "Give it a newer id, or set allowOutOfOrder: true."));
            }
        }

        return issues;
    }

    /// <summary>
    /// State written by a newer schema version. Every command that writes refuses to run while there is any, so an
    /// older version can't overwrite or misread what a newer one recorded.
    /// </summary>
    private static List<ValidationIssue> UnsupportedStates(IReadOnlyList<StoredMigrationState> states) => states
        .Where(s => s.V > StoredMigrationState.CurrentSchemaVersion)
        .Select(s => Issue("error", "unsupportedState", s.Id,
            $"State for {s.Id} was written by a newer version of Kevsoft.MigrateMongo (schema v{s.V}). Upgrade the library."))
        .ToList();

    private static CommandResult? BlockingResult(List<ValidationIssue> issues)
    {
        var errors = issues.Where(i => i.Severity == "error").ToList();
        if (errors.Count == 0)
        {
            return null;
        }

        return new CommandResult
        {
            ExitCode = errors.Any(e => e.Kind is "blocked" or "unsupportedState") ? ExitCodes.Failure : ExitCodes.ValidationFailed,
            Issues = issues,
            Message = string.Join(Environment.NewLine, errors.Select(e => e.Message)),
        };
    }

    private List<MigrationDescriptor> PendingMigrations(IReadOnlyList<StoredMigrationState> states, string? to)
    {
        var known = states.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        return _migrations
            .Where(m => !known.Contains(m.Id) && (to is null || string.CompareOrdinal(m.Id, to) <= 0))
            .ToList();
    }

    private static int NextBatch(IReadOnlyList<StoredMigrationState> states) =>
        states.Count == 0 ? 1 : states.Max(s => s.Batch) + 1;

    /// <summary>Picks what <c>down</c> rolls back, newest first.</summary>
    /// <returns>An error result if the arguments can't be satisfied, otherwise null.</returns>
    private CommandResult? SelectRollbackTargets(IReadOnlyList<StoredMigrationState> states, CommandArguments arguments, out List<StoredMigrationState> targets)
    {
        var applied = states.Where(s => s.State == MigrationStates.Applied)
            .OrderByDescending(s => s.Id, StringComparer.Ordinal)
            .ToList();

        if (arguments.To is not null)
        {
            if (!_byId.ContainsKey(arguments.To) && applied.All(s => s.Id != arguments.To))
            {
                targets = [];
                return new CommandResult { ExitCode = ExitCodes.Failure, Message = $"Migration '{arguments.To}' was not found." };
            }

            targets = applied.Where(s => string.CompareOrdinal(s.Id, arguments.To) > 0).ToList();
        }
        else if (arguments.Batch)
        {
            var lastBatch = applied.Count == 0 ? 0 : applied.Max(s => s.Batch);
            targets = applied.Where(s => s.Batch == lastBatch).ToList();
        }
        else
        {
            targets = applied.Take(arguments.Count ?? 1).ToList();
        }

        var missing = targets.FirstOrDefault(t => !_byId.ContainsKey(t.Id));
        return missing is null
            ? null
            : new CommandResult
            {
                ExitCode = ExitCodes.Failure,
                Message = $"Cannot roll back {missing.Id} {missing.Name}: the migration is no longer in the code.",
            };
    }

    private async Task<bool> DecideTransactionalAsync(MigrationDescriptor migration, CancellationToken cancellationToken)
    {
        var mode = migration.Transaction != TransactionMode.Auto
            ? migration.Transaction
            : _options.Transaction switch
            {
                "always" => TransactionMode.Always,
                "never" => TransactionMode.Never,
                _ => TransactionMode.Auto,
            };

        if (mode == TransactionMode.Never)
        {
            return false;
        }

        _supportsTransactions ??= await _executor.SupportsTransactionsAsync(cancellationToken);
        if (mode == TransactionMode.Always && !_supportsTransactions.Value)
        {
            throw new MigrationEngineException(
                $"Migration {migration.Id} {migration.Name} requires a transaction, but the MongoDB deployment doesn't support transactions (a replica set or sharded cluster is required).");
        }

        return _supportsTransactions.Value;
    }

    /// <returns>The error message, or null on success.</returns>
    private async Task<string?> ApplyAsync(MigrationDescriptor migration, int batch, OutputOptions outputOptions, CancellationToken cancellationToken)
    {
        bool transactional;
        try
        {
            transactional = await DecideTransactionalAsync(migration, cancellationToken);
        }
        catch (MigrationEngineException e)
        {
            Emit(EventTypes.MigrationFailed, migration, "up", null, null, e.Message);
            return e.Message;
        }

        Emit(EventTypes.MigrationStarted, migration, "up", transactional, null, null);
        var run = new MigrationRunInfo(RunId, batch, MigrationDirection.Up, transactional, _options.Environment);
        var stopwatch = Stopwatch.StartNew();

        if (!transactional)
        {
            await _store.SaveStateAsync(NewState(migration, MigrationStates.Applying, batch, false), null, cancellationToken);
        }

        MigrationOutputInfo? output = null;
        try
        {
            await RunInScopeAsync(migration, transactional, async scope =>
            {
                output = null;
                output = Committed(await _executor.ExecuteAsync(scope, migration, run, outputOptions, cancellationToken), true);
                var state = NewState(migration, MigrationStates.Applied, batch, transactional);
                state.DurationMs = stopwatch.ElapsedMilliseconds;
                state.Output = output;
                var record = RecordingToken(scope, cancellationToken);
                await _store.SaveStateAsync(state, scope, record);
                await _store.AppendHistoryAsync(History(migration.Id, migration.Name, HistoryEvents.Applied, "up", batch, migration.Checksum, state.DurationMs, null, output), scope, record);
            }, cancellationToken);
        }
        catch (Exception e)
        {
            var error = Describe(e);
            output = Committed((e as MigrationExecutionException)?.Output ?? output, CommittedAfterFailure(e, transactional));
            if (!transactional)
            {
                var failed = NewState(migration, MigrationStates.Failed, batch, false);
                failed.AppliedAt = null;
                failed.Error = error;
                failed.DurationMs = stopwatch.ElapsedMilliseconds;
                failed.Output = output;
                await _store.SaveStateAsync(failed, null, CancellationToken.None);
            }

            await _store.AppendHistoryAsync(History(migration.Id, migration.Name, HistoryEvents.Failed, "up", batch, migration.Checksum, stopwatch.ElapsedMilliseconds, error, output), null, CancellationToken.None);
            Emit(EventTypes.MigrationFailed, migration, "up", transactional, stopwatch.ElapsedMilliseconds, error, output, e);
            return error;
        }

        Emit(EventTypes.MigrationCompleted, migration, "up", transactional, stopwatch.ElapsedMilliseconds, null, output);
        return null;
    }

    private async Task<string?> RollBackAsync(MigrationDescriptor migration, StoredMigrationState state, OutputOptions outputOptions, CancellationToken cancellationToken)
    {
        bool transactional;
        try
        {
            transactional = await DecideTransactionalAsync(migration, cancellationToken);
        }
        catch (MigrationEngineException e)
        {
            Emit(EventTypes.MigrationFailed, migration, "down", null, null, e.Message);
            return e.Message;
        }

        Emit(EventTypes.MigrationStarted, migration, "down", transactional, null, null);
        var run = new MigrationRunInfo(RunId, state.Batch, MigrationDirection.Down, transactional, _options.Environment);
        var stopwatch = Stopwatch.StartNew();

        if (!transactional)
        {
            state.State = MigrationStates.RollingBack;
            state.Direction = "down";
            state.RunId = RunId;
            state.UpdatedAt = Now;
            await _store.SaveStateAsync(state, null, cancellationToken);
        }

        MigrationOutputInfo? output = null;
        try
        {
            await RunInScopeAsync(migration, transactional, async scope =>
            {
                output = null;
                output = Committed(await _executor.ExecuteAsync(scope, migration, run, outputOptions, cancellationToken), true);
                var record = RecordingToken(scope, cancellationToken);
                await _store.DeleteStateAsync(migration.Id, scope, record);
                await _store.AppendHistoryAsync(History(migration.Id, migration.Name, HistoryEvents.RolledBack, "down", state.Batch, state.Checksum, stopwatch.ElapsedMilliseconds, null, output), scope, record);
            }, cancellationToken);
        }
        catch (Exception e)
        {
            var error = Describe(e);
            output = Committed((e as MigrationExecutionException)?.Output ?? output, CommittedAfterFailure(e, transactional));
            if (!transactional)
            {
                state.State = MigrationStates.Failed;
                state.Error = error;
                state.UpdatedAt = Now;
                state.Output = output;
                await _store.SaveStateAsync(state, null, CancellationToken.None);
            }

            await _store.AppendHistoryAsync(History(migration.Id, migration.Name, HistoryEvents.Failed, "down", state.Batch, state.Checksum, stopwatch.ElapsedMilliseconds, error, output), null, CancellationToken.None);
            Emit(EventTypes.MigrationFailed, migration, "down", transactional, stopwatch.ElapsedMilliseconds, error, output, e);
            return error;
        }

        Emit(EventTypes.MigrationCompleted, migration, "down", transactional, stopwatch.ElapsedMilliseconds, null, output);
        return null;
    }

    private static MigrationOutputInfo? Committed(MigrationOutputInfo? output, bool? committed)
    {
        if (output is not null)
        {
            output.Committed = committed;
        }

        return output;
    }

    /// <summary>
    /// Whether a failed migration's changes were persisted: always without a transaction, never when the transaction was
    /// rolled back, and unknown (null) when its commit failed with <c>UnknownTransactionCommitResult</c> and wasn't
    /// retried successfully, since that commit may have been applied.
    /// </summary>
    private bool? CommittedAfterFailure(Exception exception, bool transactional) =>
        !transactional ? true
        : _executor.ClassifyTransactionError(exception) == TransactionErrorKind.UnknownCommitResult ? null
        : false;

    private OutputOptions OutputOptionsFor(CommandArguments? arguments) =>
        new(_options.CaptureStatements && arguments?.NoCaptureStatements != true, _options.ResultsMaxBytes);

    /// <summary>
    /// The token for recording a migration whose code has returned. Outside a transaction its changes are already made,
    /// so they're recorded even if cancellation was requested meanwhile; inside one, cancelling still rolls it all back.
    /// </summary>
    private static CancellationToken RecordingToken(IExecutionScope scope, CancellationToken cancellationToken) =>
        scope.Transactional ? cancellationToken : CancellationToken.None;

    /// <summary>
    /// Runs <paramref name="body"/> in a new scope and commits it. Transactions are retried like the driver's
    /// <c>WithTransaction</c>: the whole transaction on <c>TransientTransactionError</c>, and just the commit on
    /// <c>UnknownTransactionCommitResult</c>, until <see cref="EngineOptions.TransactionRetryTimeout"/> has elapsed.
    /// </summary>
    private async Task RunInScopeAsync(MigrationDescriptor migration, bool transactional, Func<IExecutionScope, Task> body, CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();
        for (var attempt = 1; ; attempt++)
        {
            // The scope is disposed (aborting a failed transaction) before waiting to retry.
            await using (var scope = await _executor.BeginAsync(transactional, cancellationToken))
            {
                try
                {
                    await body(scope);
                    await CommitAsync(scope, migration, started, cancellationToken);
                    return;
                }
                catch (Exception e) when (transactional && CanRetry(e, TransactionErrorKind.Transient, started, cancellationToken))
                {
                    Log("warning", $"Migration {migration.Id} {migration.Name} hit a transient transaction error (attempt {attempt}); retrying the transaction. {e.Message}");
                }
            }

            await DelayBeforeRetryAsync(attempt, cancellationToken);
        }
    }

    private async Task CommitAsync(IExecutionScope scope, MigrationDescriptor migration, long started, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await scope.CommitAsync(cancellationToken);
                return;
            }
            catch (Exception e) when (scope.Transactional && CanRetry(e, TransactionErrorKind.UnknownCommitResult, started, cancellationToken))
            {
                Log("warning", $"Migration {migration.Id} {migration.Name}: the commit result is unknown (attempt {attempt}); retrying the commit. {e.Message}");
            }

            await DelayBeforeRetryAsync(attempt, cancellationToken);
        }
    }

    private bool CanRetry(Exception exception, TransactionErrorKind kind, long started, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        _executor.ClassifyTransactionError(exception) == kind &&
        _time.GetElapsedTime(started) < _options.TransactionRetryTimeout;

    private Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromTicks(Math.Min(_options.TransactionRetryDelay.Ticks * (1L << Math.Min(attempt - 1, 16)), TimeSpan.FromSeconds(5).Ticks));
        return delay > TimeSpan.Zero ? Task.Delay(delay, _time, cancellationToken) : Task.CompletedTask;
    }

    private void Log(string level, string message) =>
        _events.OnEvent(new RunnerEvent { Type = EventTypes.Log, Level = level, Message = message });

    private async Task<CommandResult> WithLockAsync(Func<CancellationToken, Task<CommandResult>> action, CancellationToken cancellationToken)
    {
        var acquired = await AcquireLockAsync(cancellationToken);
        if (acquired is null)
        {
            var holder = await _store.GetLockAsync(cancellationToken);
            return new CommandResult
            {
                ExitCode = ExitCodes.LockTimeout,
                Lock = holder,
                Message = $"Timed out after {_options.LockWaitTimeout.TotalSeconds:0}s waiting for the migration lock" +
                          (holder is null ? "." : $" held by run {holder.RunId} ({holder.Host}), which expires at {holder.ExpiresAt:u}."),
            };
        }

        // The heartbeat runs until the action returns, not until cancellation is requested: migration code that doesn't
        // stop straight away is still changing the database, and the lock must not expire under it.
        using var lockLost = new CancellationTokenSource();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lockLost.Token);
        using var finished = new CancellationTokenSource();
        var heartbeat = HeartbeatAsync(lockLost, acquired.Value.Requested, finished.Token);
        try
        {
            return await action(cancelled.Token);
        }
        catch (MigrationEngineException e)
        {
            return new CommandResult { ExitCode = ExitCodes.Failure, Message = e.Message };
        }
        finally
        {
            await finished.CancelAsync();
            await heartbeat;
            await _store.ReleaseLockAsync(RunId, CancellationToken.None);
        }
    }

    /// <returns>
    /// The lock and when the request that took it was sent (a <see cref="TimeProvider.GetTimestamp"/>), or null if the wait
    /// timed out.
    /// </returns>
    private async Task<(LockInfo Lock, long Requested)?> AcquireLockAsync(CancellationToken cancellationToken)
    {
        var waiting = _time.GetTimestamp();
        string? reportedHolder = null;
        while (true)
        {
            var requested = _time.GetTimestamp();
            if (await _store.TryAcquireLockAsync(RunId, _options.Provenance.Host, _options.LockTtl, cancellationToken) is { } acquired)
            {
                _events.OnEvent(new RunnerEvent { Type = EventTypes.LockAcquired, Message = $"Acquired lock (run {RunId})." });
                return (acquired, requested);
            }

            if (_time.GetElapsedTime(waiting) >= _options.LockWaitTimeout)
            {
                return null;
            }

            var holder = await _store.GetLockAsync(cancellationToken);
            if (holder is not null && holder.RunId != reportedHolder)
            {
                reportedHolder = holder.RunId;
                _events.OnEvent(new RunnerEvent
                {
                    Type = EventTypes.LockWaiting,
                    Message = $"Waiting for lock held by run {holder.RunId} ({holder.Host}), which expires at {holder.ExpiresAt:u}.",
                });
            }

            var remaining = _options.LockWaitTimeout - _time.GetElapsedTime(waiting);
            var delay = remaining < _options.LockPollInterval ? remaining : _options.LockPollInterval;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _time, cancellationToken);
            }
        }
    }

    /// <param name="lockLost">Cancelled by the heartbeat when the lock is lost.</param>
    /// <param name="renewed">When the request that took the lock was sent (a <see cref="TimeProvider.GetTimestamp"/>).</param>
    /// <param name="finished">Cancelled when the work holding the lock has finished.</param>
    private async Task HeartbeatAsync(CancellationTokenSource lockLost, long renewed, CancellationToken finished)
    {
        var interval = _options.LockTtl / 3;
        while (!finished.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, _time, finished);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var requested = _time.GetTimestamp();
            try
            {
                if (await _store.RenewLockAsync(RunId, _options.LockTtl, finished))
                {
                    renewed = requested;
                    continue;
                }

                _events.OnEvent(new RunnerEvent { Type = EventTypes.Log, Level = "error", Message = "Lost the migration lock; cancelling." });
                await lockLost.CancelAsync();
                return;
            }
            catch (OperationCanceledException) when (finished.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // Transient errors (elections, network blips) are retried while the lock is still ours; stop the run
                // before it could expire and be taken by someone else. The server set the expiry to LockTtl after it
                // received the last renewal, so timing from when that renewal was sent is safe whatever its clock says.
                if (_time.GetElapsedTime(renewed) + interval >= _options.LockTtl)
                {
                    _events.OnEvent(new RunnerEvent { Type = EventTypes.Log, Level = "error", Message = $"Couldn't renew the migration lock before it expires ({e.Message}); cancelling." });
                    await lockLost.CancelAsync();
                    return;
                }

                _events.OnEvent(new RunnerEvent { Type = EventTypes.Log, Level = "warning", Message = $"Couldn't renew the migration lock ({e.Message}); retrying." });
            }
        }
    }

    /// <summary>
    /// Stops before the next migration once the lock is lost (rather than relying on driver calls noticing), so another
    /// run that takes over the lock can't run migrations alongside this one. Caller cancellation still throws.
    /// </summary>
    private static CommandResult? LockLost(CancellationToken lockToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return lockToken.IsCancellationRequested
            ? new CommandResult { ExitCode = ExitCodes.Failure, Message = "Lost the migration lock; stopped before the next migration." }
            : null;
    }

    private StoredMigrationState NewState(MigrationDescriptor migration, string state, int batch, bool transactional)
    {
        var now = Now;
        return new StoredMigrationState
        {
            Id = migration.Id,
            V = StoredMigrationState.CurrentSchemaVersion,
            Name = migration.Name,
            Source = migration.Source,
            Origin = migration.Origin,
            Checksum = migration.Checksum,
            State = state,
            Direction = "up",
            Batch = batch,
            Transactional = transactional,
            AppliedAt = state == MigrationStates.Applied ? now : null,
            UpdatedAt = now,
            RunId = RunId,
            AppliedBy = _options.Provenance,
        };
    }

    private HistoryRecord History(string id, string name, string historyEvent, string direction, int? batch, string? checksum, long? durationMs, string? error, MigrationOutputInfo? output = null) => new()
    {
        MigrationId = id,
        Name = name,
        Event = historyEvent,
        Direction = direction,
        RunId = RunId,
        Batch = batch,
        At = Now,
        DurationMs = durationMs,
        Checksum = checksum,
        Error = error,
        Host = _options.Provenance.Host,
        Output = output,
    };

    // A failure's exception, with its stack trace, goes in the event; state and history keep the short error.
    private void Emit(string type, MigrationDescriptor migration, string direction, bool? transactional, long? durationMs, string? error, MigrationOutputInfo? output = null, Exception? exception = null)
    {
        var cause = exception is null ? null : Cause(exception);
        _events.OnEvent(new RunnerEvent
        {
            Type = type,
            MigrationId = migration.Id,
            Name = migration.Name,
            Direction = direction,
            Transactional = transactional,
            DurationMs = durationMs,
            Error = error,
            // A cancellation's stack trace says nothing about the migration.
            Details = cause is null or OperationCanceledException ? null : cause.ToString(),
            Exception = cause,
            Output = output,
        });
    }

    private static ValidationIssue Issue(string severity, string kind, string? id, string message) =>
        new() { Severity = severity, Kind = kind, MigrationId = id, Message = message };

    /// <summary>The exception that made a migration fail, without the executor's wrapper around it.</summary>
    private static Exception Cause(Exception e) => e is MigrationExecutionException { InnerException: { } inner } ? Cause(inner) : e;

    private static string Describe(Exception e) => Cause(e) switch
    {
        OperationCanceledException => "The operation was cancelled.",
        var cause => $"{cause.GetType().Name}: {cause.Message}",
    };
}

public sealed class MigrationEngineException(string message) : Exception(message);
