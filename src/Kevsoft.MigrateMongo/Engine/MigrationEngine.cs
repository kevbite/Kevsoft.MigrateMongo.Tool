using System.Diagnostics;
using Kevsoft.MigrateMongo.Protocol;

namespace Kevsoft.MigrateMongo.Engine;

/// <summary>
/// Plans and executes migrations. Never touches the MongoDB driver directly; all database access goes through
/// <see cref="IMigrationStore"/> and <see cref="IMigrationExecutor"/>.
/// </summary>
public sealed partial class MigrationEngine
{
    private const int DefaultHistoryLimit = 50;
    private const int DefaultShowHistoryLimit = 10;

    private readonly IMigrationStore _store;
    private readonly IMigrationExecutor _executor;
    private readonly IReadOnlyList<MigrationDescriptor> _migrations;
    private readonly Dictionary<string, MigrationDescriptor> _byId;
    private readonly List<CollectionDescriptor> _collections;
    private readonly Dictionary<string, CollectionDescriptor> _collectionsByName;
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
        TimeProvider? time = null,
        IEnumerable<CollectionDescriptor>? collections = null)
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

        _collections = (collections ?? []).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        var duplicateCollection = _collections.GroupBy(c => c.Name).FirstOrDefault(g => g.Count() > 1);
        if (duplicateCollection is not null)
        {
            throw new InvalidOperationException(
                $"Collection '{duplicateCollection.Key}' is defined more than once ({string.Join(", ", duplicateCollection.Select(d => d.Origin))}).");
        }

        _collectionsByName = _collections.ToDictionary(c => c.Name, StringComparer.Ordinal);
    }

    public string RunId { get; } = Guid.NewGuid().ToString("N");

    public Task<CommandResult> ExecuteAsync(string command, CommandArguments arguments, CancellationToken cancellationToken) => command switch
    {
        _ when arguments.DryRun && command is not ("up" or "down") =>
            Task.FromResult(new CommandResult { ExitCode = ExitCodes.Failure, Message = $"--dry-run is only supported by 'up' and 'down', not '{command}'." }),
        _ when arguments.Only is not (null or OnlyScopes.Migrations or OnlyScopes.Collections) =>
            Task.FromResult(new CommandResult { ExitCode = ExitCodes.Failure, Message = $"--only must be '{OnlyScopes.Migrations}' or '{OnlyScopes.Collections}', not '{arguments.Only}'." }),
        "status" => StatusAsync(arguments.Check, cancellationToken),
        "plan" => PreviewUpAsync(null, dryRun: false, arguments, cancellationToken),
        "up" when arguments.DryRun => PreviewUpAsync(arguments.To, dryRun: true, arguments, cancellationToken),
        "up" => UpAsync(arguments.To, OutputOptionsFor(arguments), arguments, cancellationToken),
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

    public async Task<CommandResult> StatusAsync(bool check, CancellationToken cancellationToken)
    {
        await _store.InitializeAsync(cancellationToken);
        var states = await _store.GetStatesAsync(cancellationToken);
        var status = BuildStatus(states);
        var pending = status.Count(s => s.State == MigrationStates.Pending);
        var issues = Analyse(states, status);
        var collections = (await PreviewCollectionsAsync(new CommandArguments(), migrationsRunFirst: pending > 0, issues, cancellationToken))?.Select(c => c.Info).ToList();
        var changing = collections?.Count(c => c.State is CollectionStates.Changes or CollectionStates.Blocked) ?? 0;
        return new CommandResult
        {
            ExitCode = check && pending + changing > 0 ? ExitCodes.PendingMigrations : ExitCodes.Success,
            Migrations = status,
            Collections = collections,
            Issues = issues,
            Message = (pending, changing) switch
            {
                (0, 0) => "Database is up to date.",
                (_, 0) => $"{pending} pending migration(s).",
                (0, _) => $"{changing} collection(s) out of line with their definitions.",
                _ => $"{pending} pending migration(s); {changing} collection(s) out of line with their definitions.",
            },
        };
    }

    public async Task<CommandResult> ValidateAsync(CancellationToken cancellationToken)
    {
        await _store.InitializeAsync(cancellationToken);
        var states = await _store.GetStatesAsync(cancellationToken);
        var issues = Analyse(states, BuildStatus(states));
        await ValidateCollectionsAsync(issues, cancellationToken);
        var errors = issues.Count(i => i.Severity == "error");
        return new CommandResult
        {
            ExitCode = errors > 0 ? ExitCodes.ValidationFailed : ExitCodes.Success,
            Issues = issues,
            Message = errors > 0 ? $"Validation failed with {errors} error(s)." : "Validation passed.",
        };
    }

    /// <summary>Shows what <c>up</c> would do. Exits with <see cref="ExitCodes.PendingMigrations"/> if there is pending work.</summary>
    public Task<CommandResult> PlanAsync(CancellationToken cancellationToken) => PreviewUpAsync(null, dryRun: false, new CommandArguments(), cancellationToken);

    /// <summary>
    /// Reports what <c>up</c> would apply without changing anything: no lock, no state or history writes and no migration
    /// code runs. Exits with the code a real run would (barring the migrations themselves failing or a lock timeout).
    /// </summary>
    public Task<CommandResult> DryRunUpAsync(string? to, CancellationToken cancellationToken) => PreviewUpAsync(to, dryRun: true, new CommandArguments(), cancellationToken);

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
    private async Task<CommandResult> PreviewUpAsync(string? to, bool dryRun, CommandArguments arguments, CancellationToken cancellationToken)
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
            plan = arguments.Only == OnlyScopes.Collections ? [] : await PlanItemsAsync(PendingMigrations(states, to), "up", cancellationToken);
        }
        catch (MigrationEngineException e)
        {
            return new CommandResult { ExitCode = ExitCodes.Failure, Issues = issues, Message = e.Message };
        }

        var pendingCount = PendingMigrations(states, null).Count;
        List<CollectionInfo>? collections = null;
        if (arguments.Only != OnlyScopes.Migrations && CollectionsRunAfter(to, pendingCount, plan.Count, arguments, issues))
        {
            collections = (await PreviewCollectionsAsync(arguments, migrationsRunFirst: plan.Count > 0, issues, cancellationToken))?.Select(c => c.Info).ToList();
        }

        var holder = await ReportHeldLockAsync(issues, cancellationToken);
        var batch = NextBatch(states);
        var changing = collections?.Count(c => c.State is CollectionStates.Changes or CollectionStates.Blocked) ?? 0;
        var errors = issues.Any(i => i.Severity == "error");
        var work = DescribeWork(plan.Count, batch, changing);
        return new CommandResult
        {
            ExitCode = errors ? ExitCodes.ValidationFailed : plan.Count + changing > 0 && !dryRun ? ExitCodes.PendingMigrations : ExitCodes.Success,
            Plan = plan,
            Batch = plan.Count > 0 ? batch : null,
            Collections = collections,
            Issues = issues,
            Lock = holder,
            Message = (work, dryRun) switch
            {
                (null, true) => "Dry run: nothing to apply; database is up to date.",
                (null, false) => "Nothing to apply; database is up to date.",
                (_, true) => $"Dry run: would {work}; nothing was changed.",
                _ => $"Would {work}.",
            },
        };
    }

    /// <summary>e.g. <c>apply 2 migration(s) as batch 3 and change 1 collection(s)</c>, or null when there's nothing to do.</summary>
    private static string? DescribeWork(int migrations, int batch, int collections) => (migrations, collections) switch
    {
        (0, 0) => null,
        (_, 0) => $"apply {migrations} migration(s) as batch {batch}",
        (0, _) => $"change {collections} collection(s)",
        _ => $"apply {migrations} migration(s) as batch {batch} and change {collections} collection(s)",
    };

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
        if (holder is null || holder.ExpiresAt <= Now)
        {
            return null;
        }

        issues.Add(Issue("warning", "locked", null,
            $"Migrations are locked by run {holder.RunId} ({holder.Host}) until {holder.ExpiresAt:u}; a real run would wait up to {_options.LockWaitTimeout.TotalSeconds:0}s for it."));
        return holder;
    }

    public Task<CommandResult> UpAsync(string? to, CancellationToken cancellationToken) =>
        UpAsync(to, OutputOptionsFor(null), new CommandArguments(), cancellationToken);

    private async Task<CommandResult> UpAsync(string? to, OutputOptions outputOptions, CommandArguments arguments, CancellationToken cancellationToken)
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

            var pending = arguments.Only == OnlyScopes.Collections ? [] : PendingMigrations(states, to);
            var pendingCount = PendingMigrations(states, null).Count;
            var runCollections = arguments.Only != OnlyScopes.Migrations && CollectionsRunAfter(to, pendingCount, pending.Count, arguments, issues);

            // Stop before any migration runs if a collection definition is invalid, or (when no migration runs first that
            // could change the indexes, e.g. with --only collections) would make a change that isn't allowed.
            List<CollectionPreview>? collections = null;
            if (runCollections)
            {
                var found = new List<ValidationIssue>();
                collections = await PreviewCollectionsAsync(arguments, migrationsRunFirst: pending.Count > 0, found, ct);
                if (CollectionsBlocked(collections, found, issues, "Stopped before changing anything") is { } stopped)
                {
                    return stopped;
                }
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

            var reconciled = new List<string>();
            if (collections is not null)
            {
                // The migrations may have changed the indexes, so check every definition again before changing any.
                if (applied.Count > 0)
                {
                    var found = new List<ValidationIssue>();
                    collections = await PreviewCollectionsAsync(arguments, migrationsRunFirst: false, found, ct);
                    if (CollectionsBlocked(collections, found, issues, $"Applied {applied.Count} migration(s) in batch {batch}, then stopped before changing any collection") is { } stopped)
                    {
                        stopped.Batch = batch;
                        stopped.Applied = applied;
                        return stopped;
                    }
                }

                foreach (var collection in collections!)
                {
                    if (LockLost(ct, cancellationToken) is { } lost)
                    {
                        lost.Batch = applied.Count > 0 ? batch : null;
                        lost.Applied = applied;
                        lost.Reconciled = reconciled;
                        lost.Issues = issues;
                        return lost;
                    }

                    var (changed, error) = await ReconcileAsync(collection, arguments, outputOptions, batch, ct);
                    if (error is not null)
                    {
                        return new CommandResult
                        {
                            ExitCode = ExitCodes.Failure,
                            Batch = applied.Count > 0 ? batch : null,
                            Applied = applied,
                            Reconciled = reconciled,
                            Issues = issues,
                            Message = $"Collection {collection.Collection.Name} failed: {error}",
                        };
                    }

                    if (changed)
                    {
                        reconciled.Add(collection.Collection.Name);
                    }
                }
            }

            return new CommandResult
            {
                ExitCode = ExitCodes.Success,
                Batch = applied.Count > 0 ? batch : null,
                Applied = applied,
                Reconciled = runCollections ? reconciled : null,
                Issues = issues,
                Message = (applied.Count, reconciled.Count) switch
                {
                    (0, 0) => "Nothing to apply; database is up to date.",
                    (_, 0) => $"Applied {applied.Count} migration(s) in batch {batch}.",
                    (0, _) => $"Changed {reconciled.Count} collection(s).",
                    _ => $"Applied {applied.Count} migration(s) in batch {batch} and changed {reconciled.Count} collection(s).",
                },
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
        await _store.InitializeAsync(cancellationToken);
        var current = await _store.GetLockAsync(cancellationToken);
        if (current is null)
        {
            return new CommandResult { ExitCode = ExitCodes.Success, Message = "No lock is held." };
        }

        var now = Now;
        if (!force && current.ExpiresAt > now)
        {
            return new CommandResult
            {
                ExitCode = ExitCodes.Failure,
                Lock = current,
                Message = $"The lock is held by run {current.RunId} ({current.Host}) and expires at {current.ExpiresAt:u}. Use --force to remove it anyway.",
            };
        }

        var removed = await _store.DeleteLockAsync(force ? null : now, cancellationToken);
        return new CommandResult
        {
            ExitCode = removed ? ExitCodes.Success : ExitCodes.Failure,
            Lock = current,
            Message = removed ? $"Removed lock held by run {current.RunId} ({current.Host})." : "The lock changed while removing it; try again.",
        };
    }

    public async Task<CommandResult> HistoryAsync(string? id, int? limit, CancellationToken cancellationToken)
    {
        await _store.InitializeAsync(cancellationToken);
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
        await _store.InitializeAsync(cancellationToken);
        var states = await _store.GetStatesAsync(cancellationToken);
        if (!_byId.ContainsKey(id) && states.All(s => s.Id != id) &&
            (_collectionsByName.ContainsKey(id) || (await GetCollectionStatesAsync(cancellationToken)).ContainsKey(id)))
        {
            return await ShowCollectionAsync(id, historyLimit ?? DefaultShowHistoryLimit, noStatements, cancellationToken);
        }

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
        var issues = new List<ValidationIssue>();
        foreach (var state in states.Where(s => s.V > StoredMigrationState.CurrentSchemaVersion))
        {
            issues.Add(Issue("error", "unsupportedState", state.Id,
                $"State for {state.Id} was written by a newer version of Kevsoft.MigrateMongo (schema v{state.V}). Upgrade the library."));
        }

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
                await _store.SaveStateAsync(state, scope, cancellationToken);
                await _store.AppendHistoryAsync(History(migration.Id, migration.Name, HistoryEvents.Applied, "up", batch, migration.Checksum, state.DurationMs, null, output), scope, cancellationToken);
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
            Emit(EventTypes.MigrationFailed, migration, "up", transactional, stopwatch.ElapsedMilliseconds, error, output);
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
                await _store.DeleteStateAsync(migration.Id, scope, cancellationToken);
                await _store.AppendHistoryAsync(History(migration.Id, migration.Name, HistoryEvents.RolledBack, "down", state.Batch, state.Checksum, stopwatch.ElapsedMilliseconds, null, output), scope, cancellationToken);
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
            Emit(EventTypes.MigrationFailed, migration, "down", transactional, stopwatch.ElapsedMilliseconds, error, output);
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

        using var lockLost = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeat = HeartbeatAsync(lockLost, acquired.ExpiresAt);
        try
        {
            return await action(lockLost.Token);
        }
        catch (MigrationEngineException e)
        {
            return new CommandResult { ExitCode = ExitCodes.Failure, Message = e.Message };
        }
        finally
        {
            await StopHeartbeatAsync(lockLost, heartbeat);
            await _store.ReleaseLockAsync(RunId, CancellationToken.None);
        }
    }

    private async Task<LockInfo?> AcquireLockAsync(CancellationToken cancellationToken)
    {
        var deadline = Now + _options.LockWaitTimeout;
        string? reportedHolder = null;
        while (true)
        {
            var now = Now;
            var lockInfo = new LockInfo
            {
                RunId = RunId,
                Host = _options.Provenance.Host,
                AcquiredAt = now,
                ExpiresAt = now + _options.LockTtl,
            };

            if (await _store.TryAcquireLockAsync(lockInfo, now, cancellationToken))
            {
                _events.OnEvent(new RunnerEvent { Type = EventTypes.LockAcquired, Message = $"Acquired lock (run {RunId})." });
                return lockInfo;
            }

            if (Now >= deadline)
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

            var remaining = deadline - Now;
            var delay = remaining < _options.LockPollInterval ? remaining : _options.LockPollInterval;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _time, cancellationToken);
            }
        }
    }

    private async Task HeartbeatAsync(CancellationTokenSource lockLost, DateTime expiresAt)
    {
        var interval = _options.LockTtl / 3;
        while (!lockLost.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, _time, lockLost.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var renewedUntil = Now + _options.LockTtl;
            try
            {
                if (await _store.RenewLockAsync(RunId, renewedUntil, lockLost.Token))
                {
                    expiresAt = renewedUntil;
                    continue;
                }

                _events.OnEvent(new RunnerEvent { Type = EventTypes.Log, Level = "error", Message = "Lost the migration lock; cancelling." });
                await lockLost.CancelAsync();
                return;
            }
            catch (OperationCanceledException) when (lockLost.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // Transient errors (elections, network blips) are retried while the lock is still ours; stop the run
                // before it could expire and be taken by someone else.
                if (Now + interval >= expiresAt)
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

    private static async Task StopHeartbeatAsync(CancellationTokenSource lockLost, Task heartbeat)
    {
        if (!lockLost.IsCancellationRequested)
        {
            await lockLost.CancelAsync();
        }

        await heartbeat;
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

    private void Emit(string type, MigrationDescriptor migration, string direction, bool? transactional, long? durationMs, string? error, MigrationOutputInfo? output = null) =>
        _events.OnEvent(new RunnerEvent
        {
            Type = type,
            MigrationId = migration.Id,
            Name = migration.Name,
            Direction = direction,
            Transactional = transactional,
            DurationMs = durationMs,
            Error = error,
            Output = output,
        });

    private static ValidationIssue Issue(string severity, string kind, string? id, string message) =>
        new() { Severity = severity, Kind = kind, MigrationId = id, Message = message };

    private static string Describe(Exception e) => e switch
    {
        MigrationExecutionException { InnerException: { } inner } => Describe(inner),
        OperationCanceledException => "The operation was cancelled.",
        _ => $"{e.GetType().Name}: {e.Message}",
    };
}

public sealed class MigrationEngineException(string message) : Exception(message);
