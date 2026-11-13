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
        if (!HasCollectionWork)
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
        if (!HasCollectionWork)
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
            var plan = await PlanAsync(collection, PolicyFor(collection, state, arguments), cancellationToken);
            var info = ToInfo(collection, plan, state, arguments);
            result.Add(new CollectionPreview(collection, state, plan, info));

            if (info.State == CollectionStates.Superseded)
            {
                issues.Add(Issue("warning", "superseded", collection.Name, Superseded(collection, state!)));
                continue;
            }

            issues.AddRange(Warnings(collection, plan));
            foreach (var problem in plan.Problems)
            {
                var severity = plan.Invalid || !migrationsRunFirst ? "error" : "warning";
                var message = plan.Invalid || !migrationsRunFirst ? problem : problem + " (The pending migrations may change this.)";
                issues.Add(Issue(severity, plan.Invalid ? "invalidCollection" : "collectionBlocked", collection.Name, message));
            }
        }

        if (PruneUndeclared)
        {
            var plan = await _executor.PlanUndeclaredAsync(UndeclaredPolicy(), cancellationToken);
            if (plan.Changes.Count > 0)
            {
                result.Add(new CollectionPreview(UndeclaredCollections, null, plan, UndeclaredInfo(plan)));
                issues.AddRange(UndeclaredWarnings(plan));
            }
        }

        return result;
    }

    /// <summary>Whether <c>up</c> has collection work: definitions, or undeclared collections to report or drop.</summary>
    private bool HasCollectionWork => _collections.Count > 0 || PruneUndeclared;

    private bool PruneUndeclared => _options.CollectionsPrune is "report" or "drop";

    /// <summary>The entry that lists the collections and views no definition declares.</summary>
    private static readonly CollectionDescriptor UndeclaredCollections = new(
        UndeclaredEntryName,
        "config",
        "collections.prune",
        (_, _) => throw new InvalidOperationException("Undeclared collections have no definition."));

    private const string UndeclaredEntryName = "(undeclared)";

    private UndeclaredPolicy UndeclaredPolicy() => new()
    {
        Prune = _options.CollectionsPrune,
        Ignore = _options.CollectionsIgnore,
        // A source a declared view reads from is kept even when nothing declares it.
        Declared = _collections.Select(c => c.Name).Concat(_collections.Select(c => c.ViewOn).OfType<string>()).ToHashSet(StringComparer.Ordinal),
    };

    private static CollectionInfo UndeclaredInfo(CollectionPlan plan) => new()
    {
        Name = UndeclaredEntryName,
        Kind = "undeclared",
        Source = UndeclaredCollections.Source,
        Origin = UndeclaredCollections.Origin,
        State = plan.HasChanges ? CollectionStates.Changes : CollectionStates.InSync,
        Changes = plan.Changes,
    };

    /// <summary>A warning for each undeclared collection or view that's left alone.</summary>
    private static IEnumerable<ValidationIssue> UndeclaredWarnings(CollectionPlan plan) =>
        plan.Changes
            .Where(c => c.Action == IndexActions.Keep)
            .Select(c => Issue("warning", "undeclaredCollection", c.Index, c.Warning ?? $"{c.Index} isn't declared: {c.Detail}."));

    /// <summary>
    /// Plans a definition, or for a view that's part of a cycle of views (which can't be created in any order), returns an
    /// invalid plan without asking the database.
    /// </summary>
    private Task<CollectionPlan> PlanAsync(CollectionDescriptor collection, CollectionPolicy policy, CancellationToken cancellationToken) =>
        _cyclicViews.TryGetValue(collection.Name, out var problem)
            ? Task.FromResult(new CollectionPlan { Invalid = true, Problems = [problem] })
            : _executor.PlanCollectionAsync(collection, policy, cancellationToken);

    /// <summary>
    /// Collections in name order, then views, each after the view it reads from (ties in name order). Views that read from
    /// each other in a cycle, or from a view in one, go last and are returned in <paramref name="cyclic"/> with the problem.
    /// </summary>
    private static List<CollectionDescriptor> OrderCollections(List<CollectionDescriptor> declared, out Dictionary<string, string> cyclic)
    {
        var ordered = declared.Where(c => !c.IsView).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        var views = declared.Where(c => c.IsView).ToDictionary(c => c.Name, StringComparer.Ordinal);
        var remaining = views.Values.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        while (remaining.FirstOrDefault(v => !views.ContainsKey(v.ViewOn!) || placed.Contains(v.ViewOn!)) is { } ready)
        {
            ordered.Add(ready);
            placed.Add(ready.Name);
            remaining.Remove(ready);
        }

        cyclic = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var view in remaining)
        {
            var chain = new List<string> { view.Name };
            var current = view.ViewOn!;
            while (!chain.Contains(current))
            {
                chain.Add(current);
                current = views[current].ViewOn!;
            }

            cyclic[view.Name] = current == view.Name
                ? $"{view.Name}: views read from each other in a cycle ({string.Join(" → ", chain.Append(view.Name))}), so none of them can be created."
                : $"{view.Name}: it reads from {view.ViewOn}, which depends on a cycle of views, so it can't be created.";
            ordered.Add(view);
        }

        return ordered;
    }

    /// <summary>Warnings on a plan's changes, e.g. a skipped option or existing documents that fail a new validator.</summary>
    private static IEnumerable<ValidationIssue> Warnings(CollectionDescriptor collection, CollectionPlan plan) =>
        plan.Changes
            .Where(c => c.Warning is not null && c.Blocked != true)
            .Select(c => Issue("warning", "collectionWarning", collection.Name, $"{collection.Name}: {c.Warning}"));

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
            var plan = await PlanAsync(collection, PolicyFor(collection, null, new CommandArguments()), cancellationToken);
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
        SearchPrune = _options.SearchIndexesPrune,
        SearchWaitForReady = _options.SearchIndexesWaitForReady,
        SearchWaitTimeout = _options.SearchIndexesWaitTimeout,
        OnRebuild = _options.IndexesOnRebuild,
        DropHiddenAfter = _options.IndexesDropHiddenAfter,
        AllowRebuild = (arguments.AllowRebuild ?? [])
            .Where(a => a.StartsWith(collection.Name + ".", StringComparison.Ordinal))
            .Select(a => a[(collection.Name.Length + 1)..])
            .ToHashSet(StringComparer.Ordinal),
        AllowUnmatched = arguments.AllowUnmatched?.Contains(collection.Name, StringComparer.Ordinal) == true,
        OnConvertToCapped = _options.CollectionsOnConvertToCapped,
        AllowConvertToCapped = arguments.AllowConvertToCapped?.Contains(collection.Name, StringComparer.Ordinal) == true,
        OnUnchangeable = _options.CollectionsOnUnchangeable,
        SkipUnchangeable = arguments.SkipUnchangeable?.Contains(collection.Name, StringComparer.Ordinal) == true,
        ValidatorPrune = _options.ValidatorsPrune,
        OnInvalidDocuments = _options.ValidatorsOnInvalidDocuments,
        CountInvalidDocuments = _options.ValidatorsCountInvalidDocuments,
        AllowInvalidDocuments = arguments.AllowInvalidDocuments?.Contains(collection.Name, StringComparer.Ordinal) == true,
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
        Kind = collection.IsView ? "view" : null,
        ViewOn = collection.ViewOn,
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
        List<ValidationIssue> issues,
        CancellationToken cancellationToken)
    {
        var (collection, state, plan, _) = preview;
        if (preview.Info.State == CollectionStates.Superseded)
        {
            _events.OnEvent(new RunnerEvent { Type = EventTypes.CollectionSkipped, Collection = collection.Name, Message = Superseded(collection, state!) });
            return (false, null);
        }

        if (!plan.HasChanges && plan.Pending.Count == 0 && state?.Declaration == plan.Declaration && SameHidden(state.Hidden, plan.Hidden))
        {
            issues.AddRange(Warnings(collection, plan));
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
        issues.AddRange(Warnings(collection, result));
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
        // The index changes are made, so record them even if the run is being cancelled.
        await _store.SaveCollectionStateAsync(saved, CancellationToken.None);
        if (changed || declarationChanged)
        {
            await _store.AppendHistoryAsync(
                CollectionHistory(collection, HistoryEvents.Reconciled, result.Declaration, stopwatch.ElapsedMilliseconds, null, changed ? saved.Output : null),
                null,
                CancellationToken.None);
        }

        EmitCollection(EventTypes.CollectionCompleted, collection, stopwatch.ElapsedMilliseconds, null,
            changes: result.Changes.Where(c => c.Action != IndexActions.Keep).ToList(), output: changed ? saved.Output : null);
        return (changed, null);
    }

    private static bool SameHidden(Dictionary<string, DateTime> stored, Dictionary<string, DateTime> planned) =>
        stored.Count == planned.Count && stored.All(p => planned.TryGetValue(p.Key, out var at) && at == p.Value);

    /// <summary>
    /// Drops the undeclared views, and collections that are still empty, per <c>collections.prune: drop</c>, recording each
    /// drop in the history under the name it had. What's left alone is reported as a warning.
    /// </summary>
    /// <returns>The names dropped, and the first error if a drop failed.</returns>
    private async Task<(List<string> Dropped, string? Error)> PruneUndeclaredAsync(
        CollectionPreview preview,
        OutputOptions outputOptions,
        int batch,
        List<ValidationIssue> issues,
        CancellationToken cancellationToken)
    {
        var run = new MigrationRunInfo(RunId, batch, MigrationDirection.Up, Transactional: false, _options.Environment);
        if (preview.Plan.HasChanges)
        {
            EmitCollection(EventTypes.CollectionStarted, UndeclaredCollections, null, null);
        }

        var stopwatch = Stopwatch.StartNew();
        var applied = await _executor.ApplyUndeclaredAsync(UndeclaredPolicy(), run, outputOptions, cancellationToken);
        var dropped = new List<string>();
        string? error = null;
        foreach (var drop in applied.Drops)
        {
            var record = History(drop.Name, UndeclaredEntryName, drop.Error is null ? HistoryEvents.Dropped : HistoryEvents.Failed, "up", null, null,
                drop.DurationMs, drop.Error, Committed(drop.Output, true));
            record.Kind = HistoryEvents.CollectionKind;
            await _store.AppendHistoryAsync(record, null, CancellationToken.None);
            if (drop.Error is null)
            {
                dropped.Add(drop.Name);
            }
            else
            {
                error ??= $"{drop.Name}: {drop.Error}";
            }
        }

        issues.AddRange(UndeclaredWarnings(applied.Plan));
        var changes = applied.Plan.Changes.Where(c => c.Action != IndexActions.Keep).ToList();
        EmitCollection(error is null ? EventTypes.CollectionCompleted : EventTypes.CollectionFailed, UndeclaredCollections, stopwatch.ElapsedMilliseconds, error,
            changes: changes);
        return (dropped, error);
    }

    private async Task<CommandResult> ShowCollectionAsync(string name, int historyLimit, bool noStatements, CancellationToken cancellationToken)
    {
        var states = await GetCollectionStatesAsync(cancellationToken);
        states.TryGetValue(name, out var state);
        CollectionInfo info;
        if (_collectionsByName.TryGetValue(name, out var collection))
        {
            var plan = await PlanAsync(collection, PolicyFor(collection, state, new CommandArguments()), cancellationToken);
            info = ToInfo(collection, plan, state, new CommandArguments());
            info.Indexes = plan.Indexes;
            info.Options = plan.Options;
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
