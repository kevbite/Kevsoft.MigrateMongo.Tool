using Kevsoft.MigrateMongo.Protocol;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests.Collections;

/// <summary>Undeclared indexes, and the checks that stop a change before anything runs, on a real server.</summary>
public sealed class IndexPruneAndGuardTests(IntegrationEnvironment environment)
{
    [Fact]
    public async Task Report_leaves_undeclared_indexes_alone()
    {
        await using var mongo = await WithLegacyIndexAsync();
        var collection = Define(i => i.Add(k => k.Ascending("status")));

        var plan = await mongo.PlanAsync(collection);
        CollectionHarness.Changes(plan).ShouldBe(["keep legacy_1"]);
        plan.Collections!.Single().Changes.Single().Detail.ShouldBe("not declared (prune: report)");
        var up = await mongo.UpAsync(collection);

        up.ExitCode.ShouldBe(0, up.Message);
        up.Reconciled.ShouldBeEmpty();
        mongo.Indexes().Keys.ShouldBe(["_id_", "legacy_1", "status_1"], ignoreOrder: true);
        await mongo.ShouldBeInSyncAsync(collection);
    }

    [Fact]
    public async Task Drop_drops_undeclared_indexes_and_records_their_definition()
    {
        await using var mongo = await WithLegacyIndexAsync(unique: true);

        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("status"))), Prune("drop"));

        mongo.Indexes().Keys.ShouldBe(["_id_", "status_1"], ignoreOrder: true);
        var drop = mongo.State()["operations"].AsBsonArray.Select(o => o.AsBsonDocument).Single(o => o["kind"] == "dropIndex");
        drop["index"].ShouldBe(new BsonDocument { { "name", "legacy_1" }, { "keys", new BsonDocument("legacy", 1) }, { "options", new BsonDocument("unique", true) } });
    }

    [Fact]
    public async Task Hide_hides_undeclared_indexes_then_drops_them_once_hidden_long_enough()
    {
        await using var mongo = await WithLegacyIndexAsync();
        var collection = Define(i => i.Add(k => k.Ascending("status")));

        await mongo.ApplyAsync(collection, Prune("hide", TimeSpan.FromDays(1)));
        mongo.Indexes()["legacy_1"]["hidden"].ToBoolean().ShouldBeTrue();
        var hiddenAt = mongo.State()["hidden"]["legacy_1"].ToUniversalTime();
        hiddenAt.ShouldBe(DateTime.UtcNow, TimeSpan.FromMinutes(1));

        // Still inside the window: left hidden, with the time it was hidden kept.
        var waiting = await mongo.PlanAsync(collection, Prune("hide", TimeSpan.FromDays(1)));
        waiting.Collections!.Single().Changes.Single().Detail.ShouldStartWith("hidden; dropped after ");

        await mongo.ApplyAsync(collection, Prune("hide", TimeSpan.Zero));
        mongo.Indexes().Keys.ShouldBe(["_id_", "status_1"], ignoreOrder: true);
        mongo.State()["hidden"].AsBsonDocument.ElementCount.ShouldBe(0);
    }

    [Fact]
    public async Task Hidden_indexes_named_with_dots_or_a_dollar_are_recorded()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.Collection().Indexes.CreateManyAsync([
            new CreateIndexModel<BsonDocument>(new BsonDocument("status", 1)),
            new CreateIndexModel<BsonDocument>(new BsonDocument("address.city", 1)),
            new CreateIndexModel<BsonDocument>(new BsonDocument("$**", 1)),
        ]);

        // The state is keyed by index name, which MongoDB 5.0+ stores as a field name as it is.
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("status"))), Prune("hide", TimeSpan.FromDays(1)));

        mongo.State()["hidden"].AsBsonDocument.Names.ShouldBe(["address.city_1", "$**_1"], ignoreOrder: true);
        mongo.Indexes()["$**_1"]["hidden"].ToBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task An_index_someone_else_hid_starts_its_clock_when_found()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.Collection().Indexes.CreateManyAsync([
            new CreateIndexModel<BsonDocument>(new BsonDocument("status", 1)),
            new CreateIndexModel<BsonDocument>(new BsonDocument("legacy", 1), new CreateIndexOptions { Hidden = true }),
        ]);

        var up = await mongo.UpAsync(Define(i => i.Add(k => k.Ascending("status"))), Prune("hide", TimeSpan.FromDays(1)));

        up.ExitCode.ShouldBe(0, up.Message);
        up.Reconciled.ShouldBeEmpty();
        mongo.State()["hidden"]["legacy_1"].ToUniversalTime().ShouldBe(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Declaring_a_hidden_index_again_unhides_it()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("status")).Add(k => k.Ascending("legacy"))));
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("status"))), Prune("hide", TimeSpan.FromDays(1)));
        mongo.Indexes()["legacy_1"]["hidden"].ToBoolean().ShouldBeTrue();

        // Going straight back to the previous definition would count as an older build, so turn that check off.
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("status")).Add(k => k.Ascending("legacy"))), o =>
        {
            Prune("hide", TimeSpan.FromDays(1))(o);
            o.IndexesSupersededWindow = TimeSpan.Zero;
        });

        mongo.Operations().ShouldBe(["runCommand"]);
        mongo.Indexes()["legacy_1"].GetValue("hidden", false).ToBoolean().ShouldBeFalse();
        mongo.State()["hidden"].AsBsonDocument.ElementCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_definitions_prune_setting_overrides_the_config()
    {
        await using var mongo = await WithLegacyIndexAsync();

        await mongo.ApplyAsync(CollectionHarness.Define<BsonDocument>(c =>
        {
            c.Indexes.Prune = IndexPrune.Drop;
            c.Indexes.Add(k => k.Ascending("status"));
        }));

        mongo.Indexes().Keys.ShouldNotContain("legacy_1");
    }

    [Fact]
    public async Task Ignored_indexes_and_the_id_index_are_never_pruned()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.Collection().Indexes.CreateManyAsync([
            new CreateIndexModel<BsonDocument>(new BsonDocument("x", 1), new CreateIndexOptions { Name = "adhoc_x" }),
            new CreateIndexModel<BsonDocument>(new BsonDocument("legacy", 1)),
        ]);

        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("legacy")).Ignore("adhoc_*")), Prune("drop"));
        await mongo.ApplyAsync(Define(i => i.Ignore("adhoc_*")), Prune("drop"));

        mongo.Indexes().Keys.ShouldBe(["_id_", "adhoc_x"], ignoreOrder: true);
    }

    [Fact]
    public async Task An_ignored_text_index_is_not_replaced_by_a_declared_one()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.Collection().Indexes.CreateManyAsync([
            new CreateIndexModel<BsonDocument>(new BsonDocument("status", 1)),
            new CreateIndexModel<BsonDocument>(new BsonDocument("body", "text"), new CreateIndexOptions { Name = "search_text" }),
        ]);

        var up = await mongo.UpAsync(Define(i => i.Add(k => k.Ascending("status")).Add(k => k.Text("title")).Ignore("search_*")));

        up.ExitCode.ShouldBe(ExitCodes.ValidationFailed);
        up.Issues!.ShouldHaveSingleItem().Message.ShouldStartWith("things: the declared index { title: \"text\" } matches search_text, which is ignored");
        mongo.Indexes()["search_text"]["weights"].ShouldBe(new BsonDocument("body", 1));
    }

    [Fact]
    public async Task A_rebuild_is_refused_before_anything_changes_when_onRebuild_is_fail()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("email"))));
        var next = Define(i => i.Add(k => k.Ascending("email"), new() { Sparse = true }).Add(k => k.Ascending("added")));

        var plan = await mongo.PlanAsync(next, Rebuild("fail"));
        plan.ExitCode.ShouldBe(ExitCodes.ValidationFailed);
        var rebuild = plan.Collections!.Single().Changes.Single(c => c.Action == "rebuild");
        (rebuild.Blocked, rebuild.Reason).ShouldBe((true, "indexes.onRebuild is fail"));

        var up = await mongo.UpAsync(next, Rebuild("fail"));
        up.ExitCode.ShouldBe(ExitCodes.ValidationFailed);
        up.Issues!.ShouldHaveSingleItem().Message.ShouldContain("--allow-rebuild things.email_1");
        mongo.Indexes().Keys.ShouldBe(["_id_", "email_1"], ignoreOrder: true);
        mongo.Indexes()["email_1"].Contains("sparse").ShouldBeFalse();

        await mongo.ApplyAsync(next, Rebuild("fail"), new CommandArguments { AllowRebuild = ["things.email_1"] });
        mongo.Indexes()["email_1"]["sparse"].ToBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_definition_can_refuse_rebuilds_even_when_the_config_allows_them()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("email")).Add(k => k.Ascending("status"))));

        var perIndex = await mongo.UpAsync(Define(i => i.Add(k => k.Ascending("email"), new() { Sparse = true }, IndexRebuild.Fail).Add(k => k.Ascending("status"))));
        perIndex.ExitCode.ShouldBe(ExitCodes.ValidationFailed);
        perIndex.Issues!.Single().Message.ShouldContain("its definition sets IndexRebuild.Fail");

        var perCollection = await mongo.UpAsync(CollectionHarness.Define<BsonDocument>(c =>
        {
            c.Indexes.OnRebuild = IndexRebuild.Fail;
            c.Indexes.Add(k => k.Ascending("email")).Add(k => k.Ascending("status"), new() { Sparse = true });
        }));
        perCollection.ExitCode.ShouldBe(ExitCodes.ValidationFailed);

        // Allowing it in a definition doesn't override a config that refuses it.
        var loose = await mongo.UpAsync(Define(i => i.Add(k => k.Ascending("email"), new() { Sparse = true }, IndexRebuild.Allow).Add(k => k.Ascending("status"))), Rebuild("fail"));
        loose.ExitCode.ShouldBe(ExitCodes.ValidationFailed);
        mongo.Indexes().Values.ShouldAllBe(i => !i.Contains("sparse"));
    }

    [Fact]
    public async Task Keys_on_fields_no_existing_index_uses_are_blocked_until_allowed()
    {
        await using var mongo = new CollectionHarness(environment);
        // As if the app's class maps weren't registered: the declared keys use the C# property names.
        await mongo.Collection().Indexes.CreateManyAsync([
            new CreateIndexModel<BsonDocument>(new BsonDocument("companyId", 1)),
            new CreateIndexModel<BsonDocument>(new BsonDocument("email", 1), new CreateIndexOptions { Unique = true }),
        ]);
        var unmapped = CollectionHarness.Define<Unmapped>(c => c.Indexes
            .Add(k => k.Ascending(u => u.CompanyId))
            .Add(k => k.Ascending(u => u.Email), new CreateIndexOptions<Unmapped> { Unique = true }));

        var blocked = await mongo.UpAsync(unmapped, Prune("drop"));
        blocked.ExitCode.ShouldBe(ExitCodes.ValidationFailed);
        blocked.Issues!.Single().Message.ShouldContain("--allow-unmatched things");
        mongo.Indexes().Keys.ShouldBe(["_id_", "companyId_1", "email_1"], ignoreOrder: true);

        await mongo.ApplyAsync(unmapped, Prune("drop"), new CommandArguments { AllowUnmatched = ["things"] });
        mongo.Indexes().Keys.ShouldBe(["_id_", "CompanyId_1", "Email_1"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_declared_name_taken_by_another_declared_index_on_the_server_is_a_problem()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.Collection().Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("b", 1), new CreateIndexOptions { Name = "lookup" }));

        var up = await mongo.UpAsync(Define(i => i.Add(k => k.Ascending("a"), new() { Name = "lookup" }).Add(k => k.Ascending("b"))));

        up.ExitCode.ShouldBe(ExitCodes.ValidationFailed);
        up.Issues!.Single().Message.ShouldStartWith("things: the declared index { a: 1 } is named lookup, but the server's lookup is another declared index.");
        mongo.Indexes().Keys.ShouldBe(["_id_", "lookup"], ignoreOrder: true);
    }

    [Fact]
    public async Task Invalid_definitions_change_nothing()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.Collection().InsertOneAsync(new BsonDocument("_id", 1));

        var duplicate = await mongo.UpAsync(Define(i => i.Add(k => k.Ascending("a")).Add(k => k.Ascending("a"), new() { Unique = true })));
        duplicate.ExitCode.ShouldBe(ExitCodes.ValidationFailed);
        duplicate.Issues!.Select(i => i.Kind).ShouldAllBe(k => k == "invalidCollection");

        var twoText = await mongo.UpAsync(Define(i => i.Add(k => k.Text("a")).Add(k => k.Text("b"), new() { Name = "b_text" })));
        twoText.ExitCode.ShouldBe(ExitCodes.ValidationFailed);
        twoText.Issues!.Select(i => i.Message).ShouldContain("things: a collection can only have one text index.");

        var unrenderable = await mongo.UpAsync(CollectionHarness.Define<Unmapped>(c => c.Indexes.Add(k => k.Ascending(u => u.Email.Length))));
        unrenderable.ExitCode.ShouldBe(ExitCodes.ValidationFailed);
        unrenderable.Issues!.Single().Message.ShouldStartWith("things: Index 1 on 'things' couldn't be rendered:");

        mongo.Indexes().Keys.ShouldBe(["_id_"]);
        var validate = await mongo.RunAsync("validate", [Define(i => i.Add(k => k.Text("a")).Add(k => k.Text("b"), new() { Name = "b_text" }))]);
        validate.ExitCode.ShouldBe(ExitCodes.ValidationFailed);
    }

    [Fact]
    public async Task A_definition_a_newer_run_replaced_is_skipped_unless_allowed()
    {
        await using var mongo = new CollectionHarness(environment);
        var previous = Define(i => i.Add(k => k.Ascending("expiresAt"), new() { ExpireAfter = TimeSpan.FromHours(1) }));
        var current = Define(i => i.Add(k => k.Ascending("expiresAt"), new() { ExpireAfter = TimeSpan.FromHours(2) }));
        await mongo.ApplyAsync(previous);
        await mongo.ApplyAsync(current);

        var stale = await mongo.UpAsync(previous);

        stale.ExitCode.ShouldBe(0, stale.Message);
        mongo.Events.Last(e => e.Collection == "things").Type.ShouldBe("collectionSkipped");
        mongo.Indexes()["expiresAt_1"]["expireAfterSeconds"].ToInt64().ShouldBe(7200);
        (await mongo.PlanAsync(previous)).Collections!.Single().State.ShouldBe(CollectionStates.Superseded);

        var forced = await mongo.UpAsync(previous, arguments: new CommandArguments { AllowSuperseded = true });
        forced.Reconciled.ShouldBe(["things"]);
        mongo.Indexes()["expiresAt_1"]["expireAfterSeconds"].ToInt64().ShouldBe(3600);
    }

    [Fact]
    public async Task Show_lists_each_index_and_how_it_relates_to_the_definition()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.Collection().Indexes.CreateManyAsync([
            new CreateIndexModel<BsonDocument>(new BsonDocument("status", 1), new CreateIndexOptions { Name = "by_status" }),
            new CreateIndexModel<BsonDocument>(new BsonDocument("x", 1), new CreateIndexOptions { Name = "adhoc_x" }),
            new CreateIndexModel<BsonDocument>(new BsonDocument("legacy", 1)),
        ]);
        var collection = Define(i => i.Add(k => k.Ascending("status"), new() { Name = "status_lookup" }).Add(k => k.Ascending("added")).Ignore("adhoc_*"));
        await mongo.ApplyAsync(collection, Prune("hide", TimeSpan.FromDays(1)));

        var show = await mongo.RunAsync("show", [collection], o => o.IndexesPrune = "hide", new CommandArguments { Id = "things" });

        var info = show.ShowCollection.ShouldNotBeNull();
        info.State.ShouldBe(CollectionStates.InSync);
        info.Indexes!.Select(i => (i.Name, i.Status, i.DeclaredName, i.HiddenAt.HasValue)).ShouldBe([
            ("_id_", "default", null, false),
            ("by_status", "declared", "status_lookup", false),
            ("adhoc_x", "ignored", null, false),
            ("legacy_1", "undeclared", null, true),
            ("added_1", "declared", null, false),
        ], ignoreOrder: true);
        info.History!.ShouldHaveSingleItem().Event.ShouldBe("reconciled");
        info.Output!.Operations!.Select(o => o.Kind).ShouldBe(["createIndex", "runCommand"]);
    }

    private async Task<CollectionHarness> WithLegacyIndexAsync(bool unique = false)
    {
        var mongo = new CollectionHarness(environment);
        await mongo.Collection().Indexes.CreateManyAsync([
            new CreateIndexModel<BsonDocument>(new BsonDocument("status", 1)),
            new CreateIndexModel<BsonDocument>(new BsonDocument("legacy", 1), new CreateIndexOptions { Unique = unique ? true : null }),
        ]);
        return mongo;
    }

    private static Action<Engine.EngineOptions> Prune(string prune, TimeSpan? dropHiddenAfter = null) => o =>
    {
        o.IndexesPrune = prune;
        o.IndexesDropHiddenAfter = dropHiddenAfter ?? TimeSpan.FromDays(7);
    };

    private static Action<Engine.EngineOptions> Rebuild(string onRebuild) => o => o.IndexesOnRebuild = onRebuild;

    private static CollectionDescriptor Define(Action<IndexesBuilder<BsonDocument>> indexes) => CollectionHarness.Define(indexes);

    private sealed class Unmapped
    {
        [BsonId]
        public ObjectId Id { get; set; }

        public int CompanyId { get; set; }

        public string Email { get; set; } = "";
    }
}
