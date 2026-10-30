using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.Protocol;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Engine;

/// <summary>Collection definitions in the engine: when they run, what they record and what blocks them.</summary>
public sealed class CollectionEngineTests
{
    private readonly FakeStore _store = new();
    private readonly RecordingSink _sink = new();
    private readonly FakeExecutor _executor = new();

    private static readonly IndexChangeInfo CreateStatus = Collections.Change("create", "status_1");

    private MigrationEngine Engine(IEnumerable<MigrationDescriptor> migrations, params string[] collections) =>
        new(_store, _executor, migrations, new EngineOptions
        {
            LockWaitTimeout = TimeSpan.Zero,
            Provenance = new Provenance { Host = "test-host", User = "tester", Library = "1.0.0", Driver = "3.5.0", Runtime = "10.0.0" },
        }, _sink, collections: collections.Select(Collections.Create));

    [Fact]
    public async Task Up_applies_migrations_then_brings_collections_in_line()
    {
        _executor.CollectionPlans = (c, _) => c.Name == "orders" ? Collections.Plan("sha256:orders", CreateStatus) : Collections.Plan("sha256:users");

        var result = await Engine([Migrations.Create("20260101000001", "seed")], "orders", "users").ExecuteAsync("up", new CommandArguments(), CancellationToken.None);

        result.ExitCode.ShouldBe(0, result.Message);
        result.Message.ShouldBe("Applied 1 migration(s) in batch 1 and changed 1 collection(s).");
        result.Reconciled.ShouldBe(["orders"]);
        _executor.Calls.ShouldBe(["Up:20260101000001:tx", "Apply:orders", "Apply:users"]);

        var orders = _store.CollectionStates["orders"];
        orders.Declaration.ShouldBe("sha256:orders");
        orders.PreviousDeclaration.ShouldBeNull();
        orders.Origin.ShouldBe("Test.Orders");
        orders.Output!.Operations!.ShouldHaveSingleItem().Kind.ShouldBe("createIndex");

        // users had nothing to change, but its declaration is recorded the first time.
        _store.CollectionStates["users"].Output.ShouldBeNull();
        _store.History.Where(h => h.Kind == "collection").Select(h => (h.MigrationId, h.Event, h.Name, h.Checksum)).ShouldBe([
            ("orders", "reconciled", "Orders", "sha256:orders"),
            ("users", "reconciled", "Users", "sha256:users"),
        ]);

        _sink.Events.Where(e => e.Collection is not null).Select(e => (e.Type, e.Collection, e.Changes?.Count)).ShouldBe([
            ("collectionStarted", "orders", null),
            ("collectionCompleted", "orders", 1),
            ("collectionStarted", "users", null),
            ("collectionCompleted", "users", 0),
        ]);
    }

    [Fact]
    public async Task A_collection_that_is_already_in_line_writes_nothing()
    {
        await Engine([], "users").UpAsync(null, CancellationToken.None);
        var history = _store.History.Count;
        var saved = _store.CollectionStates["users"];

        var result = await Engine([], "users").UpAsync(null, CancellationToken.None);

        result.Message.ShouldBe("Nothing to apply; database is up to date.");
        _store.History.Count.ShouldBe(history);
        _store.CollectionStates["users"].ShouldBeSameAs(saved);
        _sink.Events.Last(e => e.Collection == "users").Type.ShouldBe("collectionCompleted");
    }

    [Fact]
    public async Task Only_migrations_skips_collection_definitions()
    {
        _executor.CollectionPlans = (_, _) => Collections.Plan("sha256:a", CreateStatus);

        var result = await Engine([Migrations.Create("20260101000001", "seed")], "users")
            .ExecuteAsync("up", new CommandArguments { Only = "migrations" }, CancellationToken.None);

        result.Reconciled.ShouldBeNull();
        _executor.Calls.ShouldBe(["Up:20260101000001:tx"]);
    }

    [Fact]
    public async Task Only_collections_skips_migrations_and_warns_that_they_are_pending()
    {
        _executor.CollectionPlans = (_, _) => Collections.Plan("sha256:a", CreateStatus);

        var result = await Engine([Migrations.Create("20260101000001", "seed")], "users")
            .ExecuteAsync("up", new CommandArguments { Only = "collections" }, CancellationToken.None);

        result.ExitCode.ShouldBe(0, result.Message);
        _executor.Calls.ShouldBe(["Apply:users"]);
        result.Issues!.ShouldHaveSingleItem().Message.ShouldBe("1 pending migration(s) haven't run. Collection definitions assume they have; apply them first.");
    }

    [Fact]
    public async Task Up_to_a_migration_that_leaves_others_pending_skips_collections()
    {
        var result = await Engine([Migrations.Create("20260101000001", "a"), Migrations.Create("20260101000002", "b")], "users")
            .ExecuteAsync("up", new CommandArguments { To = "20260101000001" }, CancellationToken.None);

        _executor.Calls.ShouldBe(["Up:20260101000001:tx"]);
        result.Issues!.ShouldHaveSingleItem().Message.ShouldBe("Collection definitions are skipped: 1 migration(s) after 20260101000001 are still pending.");
    }

    [Fact]
    public async Task A_blocked_change_stops_up_before_anything_runs_when_no_migration_is_pending()
    {
        _executor.CollectionPlans = (_, _) => new CollectionPlan { Declaration = "sha256:a", Changes = [CreateStatus], Problems = ["users: rebuild blocked."] };

        var result = await Engine([], "users").UpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(ExitCodes.ValidationFailed);
        result.Message.ShouldBe("Stopped before changing anything: 1 problem(s) with collection definitions.");
        result.Issues!.ShouldHaveSingleItem().Message.ShouldBe("users: rebuild blocked.");
        result.Collections!.ShouldHaveSingleItem().State.ShouldBe("blocked");
        _executor.Calls.ShouldBeEmpty();
        _store.CollectionStates.ShouldBeEmpty();
    }

    [Fact]
    public async Task While_migrations_are_pending_a_blocked_change_is_checked_again_after_they_run()
    {
        var migrated = false;
        _executor.CollectionPlans = (_, _) => migrated
            ? Collections.Plan("sha256:a", CreateStatus)
            : new CollectionPlan { Declaration = "sha256:a", Problems = ["users: rebuild blocked."] };

        var result = await Engine([Migrations.Create("20260101000001", "drop-old-index", up: () => { migrated = true; return Task.CompletedTask; })], "users")
            .UpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(0, result.Message);
        _executor.Calls.ShouldBe(["Up:20260101000001:tx", "Apply:users"]);
    }

    [Fact]
    public async Task Only_collections_stops_on_a_blocked_change_before_changing_anything_though_migrations_are_pending()
    {
        _executor.CollectionPlans = (c, _) => c.Name == "orders"
            ? Collections.Plan("sha256:orders", CreateStatus)
            : new CollectionPlan { Declaration = "sha256:users", Problems = ["users: rebuild blocked."] };
        var engine = Engine([Migrations.Create("20260101000001", "seed")], "orders", "users");
        var only = new CommandArguments { Only = "collections" };

        // The pending migration doesn't run first, so it can't change what's blocked.
        var plan = await engine.ExecuteAsync("plan", only, CancellationToken.None);
        plan.ExitCode.ShouldBe(ExitCodes.ValidationFailed);
        plan.Issues!.Single(i => i.Kind == "collectionBlocked").Message.ShouldBe("users: rebuild blocked.");

        var up = await engine.ExecuteAsync("up", only, CancellationToken.None);
        up.ExitCode.ShouldBe(ExitCodes.ValidationFailed);
        up.Message.ShouldBe("Stopped before changing anything: 1 problem(s) with collection definitions.");
        _executor.Calls.ShouldBeEmpty();
        _store.CollectionStates.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_blocked_change_found_after_the_migrations_stops_before_changing_any_collection()
    {
        _executor.CollectionPlans = (c, _) => c.Name == "orders"
            ? Collections.Plan("sha256:orders", CreateStatus)
            : new CollectionPlan { Declaration = "sha256:users", Problems = ["users: rebuild blocked."] };

        var result = await Engine([Migrations.Create("20260101000001", "seed")], "orders", "users").UpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(ExitCodes.ValidationFailed);
        result.Applied.ShouldBe(["20260101000001"]);
        result.Message.ShouldBe("Applied 1 migration(s) in batch 1, then stopped before changing any collection: 1 problem(s) with collection definitions.");
        result.Issues!.ShouldHaveSingleItem().Message.ShouldBe("users: rebuild blocked.");
        result.Collections!.Select(c => (c.Name, c.State)).ShouldBe([("orders", "changes"), ("users", "blocked")]);
        _executor.Calls.ShouldBe(["Up:20260101000001:tx"]);
        _store.CollectionStates.ShouldBeEmpty();
    }

    [Fact]
    public async Task Up_plans_each_collection_once_and_again_only_after_migrations()
    {
        await Engine([], "orders", "users").UpAsync(null, CancellationToken.None);
        _executor.Policies.Count.ShouldBe(2);

        _executor.Policies.Clear();
        await Engine([Migrations.Create("20260101000001", "seed")], "orders", "users").UpAsync(null, CancellationToken.None);
        _executor.Policies.Count.ShouldBe(4);
    }

    [Fact]
    public async Task A_failed_change_is_recorded_and_fails_the_run()
    {
        _executor.CollectionPlans = (_, _) => Collections.Plan("sha256:a", CreateStatus);
        _executor.ApplyFailure = _ => new InvalidOperationException("Index build failed.");

        var result = await Engine([], "users").UpAsync(null, CancellationToken.None);

        result.ExitCode.ShouldBe(ExitCodes.Failure);
        result.Message.ShouldBe("Collection users failed: InvalidOperationException: Index build failed.");
        var failed = _store.History.ShouldHaveSingleItem();
        (failed.Kind, failed.Event, failed.Error).ShouldBe(("collection", "failed", "InvalidOperationException: Index build failed."));
        failed.Output!.Operations!.ShouldHaveSingleItem();
        _store.CollectionStates.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_definition_that_a_newer_run_replaced_moments_ago_is_skipped()
    {
        var replacedAt = DateTime.UtcNow.AddMinutes(-5);
        _store.CollectionStates["users"] = Replaced(replacedAt);
        _executor.CollectionPlans = (_, _) => Collections.Plan("sha256:old", CreateStatus);

        var skipped = await Engine([], "users").UpAsync(null, CancellationToken.None);

        skipped.ExitCode.ShouldBe(0);
        _executor.Calls.ShouldBeEmpty();
        _sink.Events.Single(e => e.Type == "collectionSkipped").Message.ShouldBe(
            $"users: this build's definition was replaced at {replacedAt:u}; skipping it until {replacedAt.AddHours(1):u}. " +
            "Run with --allow-superseded to apply it now.");

        var forced = await Engine([], "users").ExecuteAsync("up", new CommandArguments { AllowSuperseded = true }, CancellationToken.None);

        forced.Reconciled.ShouldBe(["users"]);
        var state = _store.CollectionStates["users"];
        (state.Declaration, state.PreviousDeclaration).ShouldBe(("sha256:old", "sha256:new"));
        state.ReplacedAt!.Value.ShouldBe(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Fixing_drift_does_not_restart_the_window_for_an_older_build()
    {
        // The definition was replaced two days ago, and a run five minutes ago put back an index someone dropped.
        _store.CollectionStates["users"] = Replaced(DateTime.UtcNow.AddDays(-2));
        _store.CollectionStates["users"].ReconciledAt = DateTime.UtcNow.AddMinutes(-5);
        _executor.CollectionPlans = (_, _) => Collections.Plan("sha256:old", CreateStatus);

        var result = await Engine([], "users").UpAsync(null, CancellationToken.None);

        result.Reconciled.ShouldBe(["users"]);
    }

    [Fact]
    public async Task Fixing_drift_keeps_when_the_definition_was_replaced()
    {
        var replacedAt = DateTime.UtcNow.AddDays(-2);
        _store.CollectionStates["users"] = Replaced(replacedAt);
        _executor.CollectionPlans = (_, _) => Collections.Plan("sha256:new", CreateStatus);

        await Engine([], "users").UpAsync(null, CancellationToken.None);

        var state = _store.CollectionStates["users"];
        state.ReplacedAt.ShouldBe(replacedAt);
        state.ReconciledAt.ShouldBe(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Going_back_to_a_previous_definition_after_the_window_is_a_deliberate_change()
    {
        _store.CollectionStates["users"] = Replaced(DateTime.UtcNow.AddHours(-2));
        _executor.CollectionPlans = (_, _) => Collections.Plan("sha256:old", CreateStatus);

        var result = await Engine([], "users").UpAsync(null, CancellationToken.None);

        result.Reconciled.ShouldBe(["users"]);
    }

    [Fact]
    public async Task The_superseded_check_can_be_turned_off()
    {
        _store.CollectionStates["users"] = Replaced(DateTime.UtcNow.AddMinutes(-5));
        _executor.CollectionPlans = (_, _) => Collections.Plan("sha256:old", CreateStatus);
        var engine = new MigrationEngine(_store, _executor, [], new EngineOptions { LockWaitTimeout = TimeSpan.Zero, IndexesSupersededWindow = TimeSpan.Zero }, _sink,
            collections: [Collections.Create("users")]);

        var result = await engine.UpAsync(null, CancellationToken.None);

        result.Reconciled.ShouldBe(["users"]);
    }

    private static StoredCollectionState Replaced(DateTime at) => new()
    {
        Name = "users",
        Declaration = "sha256:new",
        PreviousDeclaration = "sha256:old",
        RunId = "abcdef0123456789",
        AppliedBy = new Provenance { Host = "deploy-1" },
        ReplacedAt = at,
        ReconciledAt = at,
    };

    [Fact]
    public async Task Allowances_and_hidden_indexes_are_passed_to_the_plan()
    {
        _store.CollectionStates["users"] = new StoredCollectionState { Name = "users", Declaration = "sha256:in-sync", Hidden = new() { ["old_1"] = DateTime.UnixEpoch } };

        await Engine([], "users", "orders").ExecuteAsync("plan", new CommandArguments
        {
            AllowRebuild = ["users.email_1", "orders.status_1", "users"],
            AllowUnmatched = ["orders"],
        }, CancellationToken.None);

        var orders = _executor.Policies[0];
        orders.AllowRebuild.ShouldBe(["status_1"]);
        orders.AllowUnmatched.ShouldBeTrue();
        orders.Hidden.ShouldBeEmpty();

        var users = _executor.Policies[1];
        users.AllowRebuild.ShouldBe(["email_1"]);
        users.AllowUnmatched.ShouldBeFalse();
        users.Hidden.Keys.ShouldBe(["old_1"]);
    }

    [Fact]
    public async Task Plan_shows_collection_changes_and_exits_with_pending_work()
    {
        _executor.CollectionPlans = (c, _) => c.Name == "orders" ? Collections.Plan("sha256:orders", CreateStatus) : Collections.Plan("sha256:users");

        var result = await Engine([], "orders", "users").ExecuteAsync("plan", new CommandArguments(), CancellationToken.None);

        result.ExitCode.ShouldBe(ExitCodes.PendingMigrations);
        result.Message.ShouldBe("Would change 1 collection(s).");
        result.Collections!.Select(c => (c.Name, c.State)).ShouldBe([("orders", "changes"), ("users", "inSync")]);
    }

    [Fact]
    public async Task Plan_reports_blocked_changes_as_errors_or_while_migrations_are_pending_warnings()
    {
        _executor.CollectionPlans = (_, _) => new CollectionPlan { Declaration = "sha256:a", Problems = ["users: rebuild blocked."] };

        var blocked = await Engine([], "users").ExecuteAsync("plan", new CommandArguments(), CancellationToken.None);
        blocked.ExitCode.ShouldBe(ExitCodes.ValidationFailed);
        blocked.Issues!.ShouldHaveSingleItem().Severity.ShouldBe("error");

        var pending = await Engine([Migrations.Create("20260101000001", "seed")], "users").ExecuteAsync("plan", new CommandArguments(), CancellationToken.None);
        pending.ExitCode.ShouldBe(ExitCodes.PendingMigrations);
        var warning = pending.Issues!.ShouldHaveSingleItem();
        warning.Severity.ShouldBe("warning");
        warning.Message.ShouldBe("users: rebuild blocked. (The pending migrations may change this.)");
    }

    [Fact]
    public async Task Dry_run_up_previews_collections_without_applying_them()
    {
        _executor.CollectionPlans = (_, _) => Collections.Plan("sha256:a", CreateStatus);

        var result = await Engine([], "users").ExecuteAsync("up", new CommandArguments { DryRun = true }, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.Message.ShouldBe("Dry run: would change 1 collection(s); nothing was changed.");
        _executor.Calls.ShouldBeEmpty();
        _store.CollectionStates.ShouldBeEmpty();
    }

    [Fact]
    public async Task Status_lists_collections_and_check_fails_while_one_is_out_of_line()
    {
        _executor.CollectionPlans = (_, _) => Collections.Plan("sha256:a", CreateStatus);

        var result = await Engine([], "users").ExecuteAsync("status", new CommandArguments { Check = true }, CancellationToken.None);

        result.ExitCode.ShouldBe(ExitCodes.PendingMigrations);
        result.Message.ShouldBe("1 collection(s) out of line with their definitions.");
        result.Collections!.ShouldHaveSingleItem().State.ShouldBe("changes");
    }

    [Fact]
    public async Task Undeclared_indexes_kept_by_report_are_not_pending_work()
    {
        _executor.CollectionPlans = (_, _) => Collections.Plan("sha256:a", Collections.Change("keep", "old_1", "not declared (prune: report)"));

        var result = await Engine([], "users").ExecuteAsync("status", new CommandArguments { Check = true }, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
        result.Collections!.ShouldHaveSingleItem().State.ShouldBe("inSync");
    }

    [Fact]
    public async Task Show_resolves_a_collection_name()
    {
        _executor.CollectionPlans = (_, _) => new CollectionPlan
        {
            Declaration = "sha256:a",
            Indexes = [new IndexInfo { Name = "_id_", Status = "default" }],
        };
        await Engine([], "users").UpAsync(null, CancellationToken.None);

        var result = await Engine([], "users").ExecuteAsync("show", new CommandArguments { Id = "users" }, CancellationToken.None);

        result.ExitCode.ShouldBe(0, result.Message);
        var show = result.ShowCollection!;
        (show.Name, show.State, show.Origin, show.Declaration).ShouldBe(("users", "inSync", "Test.Users", "sha256:a"));
        show.Indexes!.ShouldHaveSingleItem().Name.ShouldBe("_id_");
        show.History!.ShouldHaveSingleItem().Event.ShouldBe("reconciled");
    }

    [Fact]
    public async Task Validate_reports_invalid_definitions()
    {
        _executor.CollectionPlans = (_, _) => new CollectionPlan { Invalid = true, Problems = ["users: Index 1 on 'users' couldn't be rendered."] };

        var result = await Engine([], "users").ValidateAsync(CancellationToken.None);

        result.ExitCode.ShouldBe(ExitCodes.ValidationFailed);
        result.Issues!.ShouldHaveSingleItem().Kind.ShouldBe("invalidCollection");
    }

    [Fact]
    public void Collections_defined_twice_are_rejected()
    {
        Should.Throw<InvalidOperationException>(() => Engine([], "users", "users"))
            .Message.ShouldBe("Collection 'users' is defined more than once (Test.Users, Test.Users).");
    }

    [Fact]
    public async Task Only_must_be_migrations_or_collections()
    {
        var result = await Engine([], "users").ExecuteAsync("up", new CommandArguments { Only = "indexes" }, CancellationToken.None);

        result.ExitCode.ShouldBe(ExitCodes.Failure);
        result.Message.ShouldBe("--only must be 'migrations' or 'collections', not 'indexes'.");
    }
}
