using System.Diagnostics;
using Kevsoft.MigrateMongo.Protocol;

namespace Kevsoft.MigrateMongo.Engine;

/// <summary>Collection definitions: bringing each collection's indexes in line with its <c>[ManagedCollection]</c> class.</summary>
public sealed partial class MigrationEngine
{
    /// <summary>
    /// Whether <c>up</c> brings collections in line after the migrations. They're skipped when <c>--to</c> leaves later
    /// migrations pending, since a definition may rely on them.
    /// </summary>
    private bool CollectionsRunAfter(string? to, int pendingCount, int runningCount, CommandArguments arguments, List<ValidationIssue> issues)
    {
        if (_collections.Count == 0)
        {
            return false;
        }

        if (arguments.Only == OnlyScopes.Collections)
        {
            if (pendingCount > 0)
            {
                issues.Add(Issue("warning", "migrationsPending", null,
                    $"{pendingCount} pending migration(s) haven't run. Collection definitions assume they have; apply them first."));
            }

            return true;
        }

        if (to is not null && pendingCount > runningCount)
        {
            issues.Add(Issue("warning", "collectionsSkipped", null,
                $"Collection definitions are skipped: {pendingCount - runningCount} migration(s) after {to} are still pending."));
            return false;
        }

        return true;
    }

    /// <summary>
    /// Plans every collection definition without changing anything, adding an issue for each one that can't be applied. When
    /// migrations run before the definitions, a blocked change is only a warning: the migrations may change the indexes first.
    /// </summary>
    private async Task<List<CollectionPreview>?> PreviewCollectionsAsync(
        CommandArguments arguments,
        bool migrationsRunFirst,
        List<ValidationIssue> issues,
        CancellationToken cancellationToken)
    {
        if (_collections.Count == 0)
        {
            return null;
        }

        var states = await GetCollectionStatesAsync(cancellationToken);
        foreach (var state in states.Values.Where(s => s.V > StoredCollectionState.CurrentSchemaVersion))
        {
            issues.Add(Issue("error", "unsupportedState", state.Name,
                $"State for collection {state.Name} was written by a newer version of Kevsoft.MigrateMongo (schema v{state.V}). Upgrade the library."));
        }

        var result = new List<CollectionPreview>();
        foreach (var collection in _collections)
        {
            states.TryGetValue(collection.Name, out var state);
            var plan = await _executor.PlanCollectionAsync(collection, PolicyFor(collection, state, arguments), cancellationToken);
            var info = ToInfo(collection, plan, state, arguments);
            result.Add(new CollectionPreview(collection, state, plan, info));

            if (info.State == CollectionStates.Superseded)
            {
                issues.Add(Issue("warning", "superseded", collection.Name, Superseded(collection, state!)));
                continue;
            }

            foreach (var problem in plan.Problems)
            {
                var severity = plan.Invalid || !migrationsRunFirst ? "error" : "warning";
                var message = plan.Invalid || !migrationsRunFirst ? problem : problem + " (The pending migrations may change this.)";
                issues.Add(Issue(severity, plan.Invalid ? "invalidCollection" : "collectionBlocked", collection.Name, message));
            }
        }

        return result;
    }

    /// <summary>A collection definition planned against the database as it was then.</summary>
    private sealed record CollectionPreview(CollectionDescriptor Collection, StoredCollectionState? State, CollectionPlan Plan, CollectionInfo Info);

    /// <summary>
    /// The result that stops <c>up</c> when a preview found an error, or null. What the preview found is only reported then:
    /// otherwise the run goes on to apply it.
    /// </summary>
    private static CommandResult? CollectionsBlocked(
        List<CollectionPreview>? collections,
        List<ValidationIssue> found,
        List<ValidationIssue> issues,
        string stopped)
    {
        var errors = found.Count(i => i.Severity == "error");
        if (errors == 0)
        {
            return null;
        }

        issues.AddRange(found);
        return new CommandResult
        {
            ExitCode = ExitCodes.ValidationFailed,
            Applied = [],
            Collections = collections?.Select(c => c.Info).ToList(),
            Issues = issues,
            Message = $"{stopped}: {errors} problem(s) with collection definitions.",
        };
    }

    /// <summary>For <c>validate</c>: every definition renders and is consistent.</summary>
    private async Task ValidateCollectionsAsync(List<ValidationIssue> issues, CancellationToken cancellationToken)
    {
        foreach (var collection in _collections)
        {
            var plan = await _executor.PlanCollectionAsync(collection, PolicyFor(collection, null, new CommandArguments()), cancellationToken);
            if (plan.Invalid)
            {
                issues.AddRange(plan.Problems.Select(p => Issue("error", "invalidCollection", collection.Name, p)));
            }
        }
    }

    private async Task<Dictionary<string, StoredCollectionState>> GetCollectionStatesAsync(CancellationToken cancellationToken) =>
        (await _store.GetCollectionStatesAsync(cancellationToken)).ToDictionary(s => s.Name, StringComparer.Ordinal);

    private CollectionPolicy PolicyFor(CollectionDescriptor collection, StoredCollectionState? state, CommandArguments arguments) => new()
    {
        Prune = _options.IndexesPrune,
        OnRebuild = _options.IndexesOnRebuild,
        DropHiddenAfter = _options.IndexesDropHiddenAfter,
        AllowRebuild = (arguments.AllowRebuild ?? [])
            .Where(a => a.StartsWith(collection.Name + ".", StringComparison.Ordinal))
            .Select(a => a[(collection.Name.Length + 1)..])
            .ToHashSet(StringComparer.Ordinal),
        AllowUnmatched = arguments.AllowUnmatched?.Contains(collection.Name, StringComparer.Ordinal) == true,
        Hidden = state?.Hidden ?? new Dictionary<string, DateTime>(StringComparer.Ordinal),
        Now = Now,
    };

    /// <summary>
    /// True when the code's declaration is the one a newer run replaced less than <see cref="EngineOptions.IndexesSupersededWindow"/>
    /// ago, e.g. an instance of the previous build starting during a rolling deploy. Applying it would undo the newer one.
    /// After the window, going back to it counts as a deliberate change.
    /// </summary>
    private bool IsSuperseded(CollectionPlan plan, StoredCollectionState? state, CommandArguments arguments) =>
        !arguments.AllowSuperseded && !plan.Invalid && state is { ReplacedAt: { } replacedAt } &&
        plan.Declaration != state.Declaration && plan.Declaration == state.PreviousDeclaration &&
        Now < replacedAt + _options.IndexesSupersededWindow;

    private string Superseded(CollectionDescriptor collection, StoredCollectionState state) =>
        $"{collection.Name}: this build's definition was replaced at {state.ReplacedAt:u}; skipping it until " +
        $"{state.ReplacedAt + _options.IndexesSupersededWindow:u}. Run with --allow-superseded to apply it now.";

    private CollectionInfo ToInfo(CollectionDescriptor collection, CollectionPlan plan, StoredCollectionState? state, CommandArguments arguments) => new()
    {
        Name = collection.Name,
        Source = collection.Source,
        Origin = collection.Origin,
        State = IsSuperseded(plan, state, arguments) ? CollectionStates.Superseded
            : plan.Problems.Count > 0 ? CollectionStates.Blocked
            : plan.HasChanges ? CollectionStates.Changes
            : CollectionStates.InSync,
        Changes = plan.Changes,
        Problems = plan.Problems.Count > 0 ? plan.Problems : null,
        Declaration = plan.Invalid ? null : plan.Declaration,
        ReconciledAt = state?.ReconciledAt,
        RunId = state?.RunId,
        Host = state?.AppliedBy.Host,
        User = state?.AppliedBy.User,
        DurationMs = state?.DurationMs,
    };

    /// <summary>
    /// Brings one collection in line with its definition, from a preview that was checked for problems before any collection
    /// changed. The executor plans it again before applying, so it works from the indexes as they are then.
    /// </summary>
    /// <returns>Whether any index changed, and the error if it failed.</returns>
    private async Task<(bool Changed, string? Error)> ReconcileAsync(
        CollectionPreview preview,
        CommandArguments arguments,
        OutputOptions outputOptions,
        int batch,
        CancellationToken cancellationToken)
    {
        var (collection, state, plan, _) = preview;
        if (preview.Info.State == CollectionStates.Superseded)
        {
            _events.OnEvent(new RunnerEvent { Type = EventTypes.CollectionSkipped, Collection = collection.Name, Message = Superseded(collection, state!) });
            return (false, null);
        }

        if (!plan.HasChanges && state?.Declaration == plan.Declaration && SameHidden(state.Hidden, plan.Hidden))
        {
            EmitCollection(EventTypes.CollectionCompleted, collection, null, null, changes: []);
            return (false, null);
        }

        EmitCollection(EventTypes.CollectionStarted, collection, null, null);
        var run = new MigrationRunInfo(RunId, batch, MigrationDirection.Up, Transactional: false, _options.Environment);
        var stopwatch = Stopwatch.StartNew();
        CollectionApplyResult applied;
        try
        {
            applied = await _executor.ApplyCollectionAsync(collection, PolicyFor(collection, state, arguments), run, outputOptions, cancellationToken);
        }
        catch (Exception e)
        {
            var error = Describe(e);
            var output = Committed((e as MigrationExecutionException)?.Output, true);
            await _store.AppendHistoryAsync(CollectionHistory(collection, HistoryEvents.Failed, plan.Declaration, stopwatch.ElapsedMilliseconds, error, output), null, CancellationToken.None);
            EmitCollection(EventTypes.CollectionFailed, collection, stopwatch.ElapsedMilliseconds, error, output: output);
            return (false, error);
        }

        if (!applied.Applied)
        {
            var problems = string.Join(" ", applied.Plan.Problems);
            EmitCollection(EventTypes.CollectionFailed, collection, stopwatch.ElapsedMilliseconds, problems);
            return (false, problems);
        }

        var result = applied.Plan;
        var changed = result.HasChanges;
        var declarationChanged = state?.Declaration != result.Declaration;
        var saved = new StoredCollectionState
        {
            Name = collection.Name,
            Source = collection.Source,
            Origin = collection.Origin,
            Declaration = result.Declaration,
            PreviousDeclaration = declarationChanged ? state?.Declaration : state?.PreviousDeclaration,
            ReplacedAt = declarationChanged ? Now : state?.ReplacedAt,
            ReconciledAt = changed || declarationChanged || state is null ? Now : state.ReconciledAt,
            DurationMs = changed ? stopwatch.ElapsedMilliseconds : state?.DurationMs,
            RunId = changed || declarationChanged || state is null ? RunId : state.RunId,
            AppliedBy = changed || declarationChanged || state is null ? _options.Provenance : state.AppliedBy,
            Hidden = result.Hidden,
            Output = changed ? Committed(applied.Output, true) : state?.Output,
        };
        await _store.SaveCollectionStateAsync(saved, cancellationToken);
        if (changed || declarationChanged)
        {
            await _store.AppendHistoryAsync(
                CollectionHistory(collection, HistoryEvents.Reconciled, result.Declaration, stopwatch.ElapsedMilliseconds, null, changed ? saved.Output : null),
                null,
                cancellationToken);
        }

        EmitCollection(EventTypes.CollectionCompleted, collection, stopwatch.ElapsedMilliseconds, null,
            changes: result.Changes.Where(c => c.Action != IndexActions.Keep).ToList(), output: changed ? saved.Output : null);
        return (changed, null);
    }

    private static bool SameHidden(Dictionary<string, DateTime> stored, Dictionary<string, DateTime> planned) =>
        stored.Count == planned.Count && stored.All(p => planned.TryGetValue(p.Key, out var at) && at == p.Value);

    private async Task<CommandResult> ShowCollectionAsync(string name, int historyLimit, bool noStatements, CancellationToken cancellationToken)
    {
        var states = await GetCollectionStatesAsync(cancellationToken);
        states.TryGetValue(name, out var state);
        CollectionInfo info;
        if (_collectionsByName.TryGetValue(name, out var collection))
        {
            var plan = await _executor.PlanCollectionAsync(collection, PolicyFor(collection, state, new CommandArguments()), cancellationToken);
            info = ToInfo(collection, plan, state, new CommandArguments());
            info.Indexes = plan.Indexes;
        }
        else
        {
            info = new CollectionInfo
            {
                Name = name,
                Source = state!.Source,
                Origin = state.Origin,
                State = "removed",
                Declaration = state.Declaration,
                ReconciledAt = state.ReconciledAt,
                RunId = state.RunId,
                Host = state.AppliedBy.Host,
                User = state.AppliedBy.User,
                DurationMs = state.DurationMs,
            };
        }

        info.Output = noStatements ? WithoutStatements(state?.Output) : state?.Output;
        var history = await _store.GetHistoryAsync(name, historyLimit, cancellationToken);
        info.History = history
            .Where(h => h.Kind == HistoryEvents.CollectionKind)
            .Select(h =>
            {
                if (noStatements)
                {
                    h.Output = WithoutStatements(h.Output);
                }

                return h;
            })
            .ToList();

        return new CommandResult { ExitCode = ExitCodes.Success, ShowCollection = info };
    }

    private HistoryRecord CollectionHistory(CollectionDescriptor collection, string historyEvent, string declaration, long durationMs, string? error, MigrationOutputInfo? output)
    {
        var record = History(collection.Name, ShortName(collection.Origin), historyEvent, "up", null, declaration.Length > 0 ? declaration : null, durationMs, error, output);
        record.Kind = HistoryEvents.CollectionKind;
        return record;
    }

    /// <summary>The class name without its namespace, e.g. <c>Cases</c> for <c>MyApp.Collections.Cases</c>.</summary>
    private static string ShortName(string origin)
    {
        var dot = origin.LastIndexOf('.');
        return dot >= 0 ? origin[(dot + 1)..] : origin;
    }

    private void EmitCollection(
        string type,
        CollectionDescriptor collection,
        long? durationMs,
        string? error,
        List<IndexChangeInfo>? changes = null,
        MigrationOutputInfo? output = null) =>
        _events.OnEvent(new RunnerEvent
        {
            Type = type,
            Collection = collection.Name,
            Name = ShortName(collection.Origin),
            Direction = "up",
            DurationMs = durationMs,
            Error = error,
            Changes = changes,
            Output = output,
        });
}
