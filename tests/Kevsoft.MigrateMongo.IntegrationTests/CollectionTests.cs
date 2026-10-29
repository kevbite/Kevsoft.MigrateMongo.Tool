using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests;

/// <summary>Collection definitions ([ManagedCollection]) against a real server, with the CollectionMigrations fixture.</summary>
public sealed class CollectionTests(IntegrationEnvironment environment)
{
    private const string Fixture = "CollectionMigrations";

    [Fact]
    public async Task Up_creates_the_declared_indexes_and_a_second_run_finds_them_in_line()
    {
        await using var workspace = Workspace.FromFixture(environment, Fixture);

        var up = await workspace.RunAsync("up");

        up.ExitCode.ShouldBe(0, up.ToString());
        up.Output.ShouldContain("⟳ orders");
        up.Output.ShouldContain("+ create   companyId_1_createdAt_-1");
        up.Output.ShouldContain("Applied 1 migration(s) in batch 1 and changed 2 collection(s).");
        Indexes(workspace, "orders").Keys.ShouldBe([
            "_id_", "closedAt_1", "companyId_1_createdAt_-1", "customer_ci", "number_1", "open_by_company", "reference_1", "title_text_notes_text",
        ], ignoreOrder: true);
        // Typed keys use the class map's element names.
        Indexes(workspace, "customers").Keys.ShouldBe(["_id_", "email_1", "tier_1"], ignoreOrder: true);

        var states = workspace.Find("_migrations.collections");
        states.Select(s => s["_id"].AsString).ShouldBe(["customers", "orders"]);
        states[1]["declaration"].AsString.ShouldStartWith("sha256:");
        states[1]["origin"].AsString.ShouldBe("CollectionMigrations.Collections.Orders");
        states[1]["operations"].AsBsonArray.Count.ShouldBe(7);
        var history = workspace.Find("_migrations.history", sortBy: "at").Where(h => h.GetValue("kind", "").AsString == "collection").ToList();
        history.Select(h => (h["migrationId"].AsString, h["event"].AsString)).ShouldBe([("customers", "reconciled"), ("orders", "reconciled")]);

        // The server fills in collations, text index internals and number types; none of that reads as a change.
        var again = await workspace.RunAsync("up", "--no-build");
        again.ExitCode.ShouldBe(0, again.ToString());
        again.Output.ShouldContain("✔ orders   (in sync)");
        again.Output.ShouldContain("Nothing to apply; database is up to date.");
        workspace.Find("_migrations.history").Count.ShouldBe(3);

        var plan = await workspace.RunAsync("plan", "--no-build");
        plan.ExitCode.ShouldBe(0, plan.ToString());
        plan.Output.ShouldContain("⟳ orders   in sync");

        var status = await workspace.RunAsync("status", "--check", "--no-build");
        status.ExitCode.ShouldBe(0, status.ToString());
        status.Output.ShouldContain(" orders      in sync");
    }

    [Fact]
    public async Task Existing_indexes_are_adopted_and_undeclared_ones_follow_the_prune_setting()
    {
        await using var workspace = Workspace.FromFixture(environment, Fixture);
        var orders = workspace.Database.GetCollection<BsonDocument>("orders");
        await orders.Indexes.CreateManyAsync([
            new CreateIndexModel<BsonDocument>(new BsonDocument("reference", 1), new CreateIndexOptions { Name = "legacy_ref", Unique = true }),
            new CreateIndexModel<BsonDocument>(new BsonDocument("status", 1), new CreateIndexOptions { Name = "status_1" }),
            new CreateIndexModel<BsonDocument>(new BsonDocument("x", 1), new CreateIndexOptions { Name = "adhoc_x" }),
        ]);

        var up = await workspace.RunAsync("up");

        up.ExitCode.ShouldBe(0, up.ToString());
        var indexes = Indexes(workspace, "orders");
        indexes.Keys.ShouldContain("legacy_ref");
        indexes.Keys.ShouldNotContain("reference_1");
        indexes.Keys.ShouldContain("status_1");
        indexes.Keys.ShouldContain("adhoc_x");

        var show = await workspace.RunAsync("show", "orders", "--no-build");
        show.ExitCode.ShouldBe(0, show.ToString());
        show.Output.ShouldContain("orders (collection)");
        show.Output.ShouldContain("status_1                  { status: 1 }  undeclared");
        show.Output.ShouldContain("adhoc_x                   { x: 1 }  ignored");

        WriteConfig(workspace, "prune: hide");
        var hide = await workspace.RunAsync("up", "--no-build");
        hide.ExitCode.ShouldBe(0, hide.ToString());
        hide.Output.ShouldContain("- hide     status_1");
        Indexes(workspace, "orders")["status_1"].GetValue("hidden", false).ToBoolean().ShouldBeTrue();
        workspace.Find("_migrations.collections").Single(s => s["_id"] == "orders")["hidden"].AsBsonDocument.Names.ShouldBe(["status_1"]);

        WriteConfig(workspace, "prune: drop");
        var drop = await workspace.RunAsync("up", "--no-build");
        drop.ExitCode.ShouldBe(0, drop.ToString());
        Indexes(workspace, "orders").Keys.ShouldNotContain("status_1");
        Indexes(workspace, "orders").Keys.ShouldContain("adhoc_x");
        var dropped = workspace.Find("_migrations.collections").Single(s => s["_id"] == "orders")["operations"].AsBsonArray.Single().AsBsonDocument;
        dropped["kind"].AsString.ShouldBe("dropIndex");
        dropped["index"]["keys"].ShouldBe(new BsonDocument("status", 1));
    }

    [Fact]
    public async Task Changes_are_made_in_place_where_possible_and_rebuilds_can_be_refused()
    {
        await using var workspace = Workspace.FromFixture(environment, Fixture);
        (await workspace.RunAsync("up")).ExitCode.ShouldBe(0);

        WriteOrders(workspace, """
            .Add(k => k.Ascending("companyId").Descending("createdAt"), new CreateIndexOptions<BsonDocument> { Hidden = true })
            .Add(k => k.Ascending("reference"), new CreateIndexOptions<BsonDocument> { Unique = true, Sparse = true })
            .Add(k => k.Ascending("number"), new CreateIndexOptions<BsonDocument> { Unique = true })
            .Add(k => k.Ascending("closedAt"), new CreateIndexOptions<BsonDocument> { ExpireAfter = TimeSpan.FromDays(7) })
            .Add(k => k.Text("title").Text("notes"), new CreateIndexOptions<BsonDocument> { Weights = new BsonDocument("title", 10) })
            .Add(k => k.Ascending("customer"), new CreateIndexOptions<BsonDocument> { Name = "customer_ci", Collation = new Collation("en", strength: CollationStrength.Secondary) })
            .Add(k => k.Ascending("companyId"), new CreateIndexOptions<BsonDocument> { Name = "open_by_company", PartialFilterExpression = Builders<BsonDocument>.Filter.Eq("status", "open") })
            """);
        WriteConfig(workspace, "onRebuild: fail");

        var plan = await workspace.RunAsync("plan");
        plan.ExitCode.ShouldBe(4, plan.ToString());
        plan.Output.ShouldContain("~ modify   companyId_1_createdAt_-1   hidden: false → true");
        plan.Output.ShouldContain("~ modify   number_1                   unique: false → true");
        plan.Output.ShouldContain("~ modify   closedAt_1                 expireAfterSeconds 2592000 → 604800");
        plan.Output.ShouldContain("↻ rebuild  reference_1                sparse: false → true (drop, then create)   ✖ blocked: indexes.onRebuild is fail");
        plan.Output.ShouldContain("--allow-rebuild orders.reference_1");

        var refused = await workspace.RunAsync("up", "--no-build");
        refused.ExitCode.ShouldBe(4, refused.ToString());
        refused.Output.ShouldContain("Stopped before changing anything: 1 problem(s) with collection definitions.");
        Indexes(workspace, "orders")["closedAt_1"]["expireAfterSeconds"].ToInt64().ShouldBe(2592000);

        var allowed = await workspace.RunAsync("up", "--no-build", "--allow-rebuild", "orders.reference_1");
        allowed.ExitCode.ShouldBe(0, allowed.ToString());
        var indexes = Indexes(workspace, "orders");
        indexes["companyId_1_createdAt_-1"]["hidden"].ToBoolean().ShouldBeTrue();
        indexes["number_1"]["unique"].ToBoolean().ShouldBeTrue();
        indexes["closedAt_1"]["expireAfterSeconds"].ToInt64().ShouldBe(604800);
        indexes["reference_1"]["sparse"].ToBoolean().ShouldBeTrue();
        indexes["reference_1"]["unique"].ToBoolean().ShouldBeTrue();

        var show = await workspace.RunAsync("show", "orders", "--no-build");
        show.Output.ShouldContain("runCommand");
        show.Output.ShouldContain("collMod");
        show.Output.ShouldContain("dropIndex orders");

        (await workspace.RunAsync("plan", "--no-build")).ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task Keeping_a_name_while_changing_the_keys_rebuilds_the_index()
    {
        await using var workspace = Workspace.FromFixture(environment, Fixture);
        (await workspace.RunAsync("up")).ExitCode.ShouldBe(0);

        WriteOrders(workspace, """
            .Add(k => k.Ascending("companyId").Descending("createdAt"))
            .Add(k => k.Ascending("reference"), new CreateIndexOptions<BsonDocument> { Unique = true })
            .Add(k => k.Ascending("number"))
            .Add(k => k.Ascending("closedAt"), new CreateIndexOptions<BsonDocument> { ExpireAfter = TimeSpan.FromDays(30) })
            .Add(k => k.Text("title").Text("notes"), new CreateIndexOptions<BsonDocument> { Weights = new BsonDocument("title", 10) })
            .Add(k => k.Ascending("customer").Ascending("companyId"), new CreateIndexOptions<BsonDocument> { Name = "customer_ci", Collation = new Collation("en", strength: CollationStrength.Secondary) })
            .Add(k => k.Ascending("companyId"), new CreateIndexOptions<BsonDocument> { Name = "open_by_company", PartialFilterExpression = Builders<BsonDocument>.Filter.Eq("status", "open") })
            """);

        var up = await workspace.RunAsync("up");

        up.ExitCode.ShouldBe(0, up.ToString());
        up.Output.ShouldContain("↻ rebuild  customer_ci");
        Indexes(workspace, "orders")["customer_ci"]["key"].ShouldBe(new BsonDocument { { "customer", 1 }, { "companyId", 1 } });
    }

    [Fact]
    public async Task A_definition_a_newer_run_replaced_is_skipped_unless_allowed()
    {
        await using var workspace = Workspace.FromFixture(environment, Fixture);
        (await workspace.RunAsync("up")).ExitCode.ShouldBe(0);
        var original = File.ReadAllText(workspace.PathOf(Fixture, "Collections", "Orders.cs"));

        WriteOrders(workspace, """
            .Add(k => k.Ascending("companyId").Descending("createdAt"))
            .Add(k => k.Ascending("reference"), new CreateIndexOptions<BsonDocument> { Unique = true })
            .Add(k => k.Ascending("number"))
            .Add(k => k.Ascending("closedAt"), new CreateIndexOptions<BsonDocument> { ExpireAfter = TimeSpan.FromDays(7) })
            .Add(k => k.Text("title").Text("notes"), new CreateIndexOptions<BsonDocument> { Weights = new BsonDocument("title", 10) })
            .Add(k => k.Ascending("customer"), new CreateIndexOptions<BsonDocument> { Name = "customer_ci", Collation = new Collation("en", strength: CollationStrength.Secondary) })
            .Add(k => k.Ascending("companyId"), new CreateIndexOptions<BsonDocument> { Name = "open_by_company", PartialFilterExpression = Builders<BsonDocument>.Filter.Eq("status", "open") })
            """);
        (await workspace.RunAsync("up")).ExitCode.ShouldBe(0);

        // The previous build comes back, e.g. an old instance starting during a rolling deploy.
        File.WriteAllText(workspace.PathOf(Fixture, "Collections", "Orders.cs"), original);
        var stale = await workspace.RunAsync("up");

        stale.ExitCode.ShouldBe(0, stale.ToString());
        stale.Output.ShouldContain("⚠ orders: this build's definition was replaced at");
        Indexes(workspace, "orders")["closedAt_1"]["expireAfterSeconds"].ToInt64().ShouldBe(604800);

        var forced = await workspace.RunAsync("up", "--no-build", "--allow-superseded");
        forced.ExitCode.ShouldBe(0, forced.ToString());
        Indexes(workspace, "orders")["closedAt_1"]["expireAfterSeconds"].ToInt64().ShouldBe(2592000);
    }

    [Fact]
    public async Task Only_migrations_leaves_collections_for_a_later_step()
    {
        await using var workspace = Workspace.FromFixture(environment, Fixture);

        var migrations = await workspace.RunAsync("up", "--only", "migrations");
        migrations.ExitCode.ShouldBe(0, migrations.ToString());
        Indexes(workspace, "orders").Keys.ShouldBe(["_id_"]);

        var collections = await workspace.RunAsync("up", "--only", "collections", "--no-build");
        collections.ExitCode.ShouldBe(0, collections.ToString());
        collections.Output.ShouldContain("Changed 2 collection(s).");
        Indexes(workspace, "orders").Count.ShouldBe(8);
    }

    private static Dictionary<string, BsonDocument> Indexes(Workspace workspace, string collection) =>
        workspace.Database.GetCollection<BsonDocument>(collection).Indexes.List().ToList().ToDictionary(i => i["name"].AsString);

    private void WriteConfig(Workspace workspace, string indexes) =>
        workspace.WriteConfigFile($"""
            project: {Fixture}
            mongodb:
              url: {environment.MongoUrl}
              databaseName: {workspace.DatabaseName}
            lockWaitTimeout: 5
            indexes:
              {indexes}
            """);

    private static void WriteOrders(Workspace workspace, string indexes) =>
        File.WriteAllText(workspace.PathOf(Fixture, "Collections", "Orders.cs"), $$"""
            using Kevsoft.MigrateMongo;
            using MongoDB.Bson;
            using MongoDB.Driver;

            namespace CollectionMigrations.Collections;

            [ManagedCollection("orders")]
            public sealed class Orders : ICollectionDefinition<BsonDocument>
            {
                public void Define(CollectionBuilder<BsonDocument> collection) =>
                    collection.Indexes
                        {{indexes.ReplaceLineEndings("\n            ")}}
                        .Ignore("adhoc_*");
            }
            """);
}
