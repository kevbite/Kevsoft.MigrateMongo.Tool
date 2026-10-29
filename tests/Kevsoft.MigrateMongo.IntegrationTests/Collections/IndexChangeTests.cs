using Kevsoft.MigrateMongo.Protocol;
using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests.Collections;

/// <summary>Changing a definition on a real server: what's changed in place, what's rebuilt, and what's created alongside.</summary>
public sealed class IndexChangeTests(IntegrationEnvironment environment)
{
    [Fact]
    public async Task Adding_an_index_creates_only_that_one()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("a"))));

        var next = Define(i => i.Add(k => k.Ascending("a")).Add(k => k.Ascending("b")));
        CollectionHarness.Changes(await mongo.PlanAsync(next)).ShouldBe(["create b_1"]);
        await mongo.ApplyAsync(next);

        mongo.Operations().ShouldBe(["createIndex"]);
        mongo.Indexes().Keys.ShouldBe(["_id_", "a_1", "b_1"], ignoreOrder: true);
    }

    [Fact]
    public async Task Changing_a_ttl_is_made_in_place()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("expiresAt"), new() { ExpireAfter = TimeSpan.FromHours(1) })));

        var next = Define(i => i.Add(k => k.Ascending("expiresAt"), new() { ExpireAfter = TimeSpan.FromHours(2) }));
        var plan = await mongo.PlanAsync(next);
        plan.Collections!.Single().Changes.Single().Detail.ShouldBe("expireAfterSeconds 3600 → 7200");
        await mongo.ApplyAsync(next);

        mongo.Operations().ShouldBe(["runCommand"]);
        mongo.Indexes()["expiresAt_1"]["expireAfterSeconds"].ToInt64().ShouldBe(7200);
    }

    [Fact]
    public async Task Hiding_and_unhiding_is_made_in_place()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("status"))));

        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("status"), new() { Hidden = true })));
        mongo.Operations().ShouldBe(["runCommand"]);
        mongo.Indexes()["status_1"]["hidden"].ToBoolean().ShouldBeTrue();

        // Going straight back to the previous definition would count as an older build, so turn that check off.
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("status"))), o => o.IndexesSupersededWindow = TimeSpan.Zero);
        mongo.Operations().ShouldBe(["runCommand"]);
        mongo.Indexes()["status_1"].GetValue("hidden", false).ToBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Making_an_index_unique_is_made_in_place()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.Collection().InsertManyAsync([new BsonDocument("email", "a@example.com"), new BsonDocument("email", "b@example.com")]);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("email"))));

        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("email"), new() { Unique = true })));

        mongo.Operations().ShouldBe(["runCommand", "runCommand"]);
        mongo.Indexes()["email_1"]["unique"].ToBoolean().ShouldBeTrue();
        await Should.ThrowAsync<MongoWriteException>(() => mongo.Collection().InsertOneAsync(new BsonDocument("email", "a@example.com")));
    }

    [Fact]
    public async Task Making_an_index_unique_fails_on_duplicates_and_leaves_it_as_it_was()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.Collection().InsertManyAsync([new BsonDocument("email", "a@example.com"), new BsonDocument("email", "a@example.com")]);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("email"))));

        var up = await mongo.UpAsync(Define(i => i.Add(k => k.Ascending("email"), new() { Unique = true })));

        up.ExitCode.ShouldBe(ExitCodes.Failure);
        up.Message.ShouldStartWith("Collection things failed:");
        var index = mongo.Indexes()["email_1"];
        index.GetValue("unique", false).ToBoolean().ShouldBeFalse();
        index.GetValue("prepareUnique", false).ToBoolean().ShouldBeFalse();
        // New duplicates are still accepted, as before.
        await mongo.Collection().InsertOneAsync(new BsonDocument("email", "a@example.com"));
        // The failed collMod isn't recorded; prepareUnique going on, then off again, is.
        var failed = mongo.History().Last();
        failed["event"].AsString.ShouldBe("failed");
        failed["operations"].AsBsonArray.Select(o => o["command"]["index"]["prepareUnique"].AsBoolean).ShouldBe([true, false]);
    }

    [Fact]
    public async Task A_failure_part_way_through_records_what_ran_and_the_next_run_finishes_the_rest()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.Collection().InsertManyAsync([new BsonDocument("email", "a@example.com"), new BsonDocument("email", "a@example.com")]);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("email"))));
        var next = Define(i => i.Add(k => k.Ascending("email"), new() { Unique = true }).Add(k => k.Ascending("status")));

        // New indexes are created first, so status_1 is built before making email_1 unique fails on the duplicates.
        var failed = await mongo.UpAsync(next);

        failed.ExitCode.ShouldBe(ExitCodes.Failure);
        mongo.Indexes().Keys.ShouldBe(["_id_", "email_1", "status_1"], ignoreOrder: true);
        var history = mongo.History().Last();
        history["event"].AsString.ShouldBe("failed");
        history["operations"].AsBsonArray.Select(o => o["kind"].AsString).ShouldBe(["createIndex", "runCommand", "runCommand"]);

        // Nothing records the half-applied definition, so the next run works from the server and does only what's left.
        CollectionHarness.Changes(await mongo.PlanAsync(next)).ShouldBe(["modify email_1"]);
        await mongo.Collection().DeleteOneAsync(new BsonDocument("email", "a@example.com"));
        await mongo.ApplyAsync(next);
        mongo.Operations().ShouldBe(["runCommand", "runCommand"]);
        mongo.Indexes()["email_1"]["unique"].ToBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Several_in_place_changes_to_one_index_are_made_together()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.Collection().InsertManyAsync([new BsonDocument("email", "a@example.com"), new BsonDocument("email", "b@example.com")]);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("email")).Add(k => k.Ascending("expiresAt"), new() { ExpireAfter = TimeSpan.FromHours(1) })));

        await mongo.ApplyAsync(Define(i => i
            .Add(k => k.Ascending("email"), new() { Unique = true, Hidden = true })
            .Add(k => k.Ascending("expiresAt"), new() { ExpireAfter = TimeSpan.FromHours(2), Hidden = true })));

        mongo.Operations().ShouldBe(["runCommand", "runCommand", "runCommand", "runCommand", "runCommand"]);
        var indexes = mongo.Indexes();
        (indexes["email_1"]["unique"].ToBoolean(), indexes["email_1"]["hidden"].ToBoolean()).ShouldBe((true, true));
        (indexes["expiresAt_1"]["expireAfterSeconds"].ToInt64(), indexes["expiresAt_1"]["hidden"].ToBoolean()).ShouldBe((7200, true));
    }

    [Fact]
    public async Task Removing_unique_rebuilds_the_index()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("email"), new() { Unique = true })));

        var next = Define(i => i.Add(k => k.Ascending("email")));
        (await mongo.PlanAsync(next)).Collections!.Single().Changes.Single().Detail.ShouldBe("unique: true → false (drop, then create)");
        await mongo.ApplyAsync(next);

        mongo.Operations().ShouldBe(["dropIndex", "createIndex"]);
        mongo.Indexes()["email_1"].Contains("unique").ShouldBeFalse();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Adding_or_removing_a_ttl_rebuilds_the_index(bool before, bool after)
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("expiresAt"), new() { ExpireAfter = before ? TimeSpan.FromHours(1) : null })));

        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("expiresAt"), new() { ExpireAfter = after ? TimeSpan.FromHours(1) : null })));

        mongo.Operations().ShouldBe(["dropIndex", "createIndex"]);
        mongo.Indexes()["expiresAt_1"].Contains("expireAfterSeconds").ShouldBe(after);
    }

    [Fact]
    public async Task Changing_sparse_rebuilds_the_index_under_its_existing_name()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.Collection().Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument("email", 1), new CreateIndexOptions { Name = "by_email" }));

        var up = await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("email"), new() { Sparse = true })));

        up.Reconciled.ShouldBe([CollectionHarness.Things]);
        mongo.Operations().ShouldBe(["dropIndex", "createIndex"]);
        var indexes = mongo.Indexes();
        indexes.Keys.ShouldBe(["_id_", "by_email"], ignoreOrder: true);
        indexes["by_email"]["sparse"].ToBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_failed_rebuild_restores_the_previous_index()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.Collection().InsertManyAsync([new BsonDocument("email", "a@example.com"), new BsonDocument("email", "a@example.com")]);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("email"), new() { Sparse = true })));

        // Unique can't be built over the duplicates, and dropping sparse means a rebuild rather than collMod.
        var up = await mongo.UpAsync(Define(i => i.Add(k => k.Ascending("email"), new() { Unique = true })));

        up.ExitCode.ShouldBe(ExitCodes.Failure);
        up.Message!.ShouldContain("the previous index was restored");
        var index = mongo.Indexes()["email_1"];
        index["sparse"].ToBoolean().ShouldBeTrue();
        index.Contains("unique").ShouldBeFalse();
        mongo.History().Last()["operations"].AsBsonArray.Select(o => o["kind"].AsString).ShouldBe(["dropIndex", "createIndex"]);
    }

    [Fact]
    public async Task A_rebuild_cancelled_after_the_drop_restores_the_previous_index()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("email"), new() { Sparse = true })));
        using var cancel = new CancellationTokenSource();
        using var client = mongo.CancellingAfter("dropIndexes", cancel);

        var up = await mongo.RunAsync("up", [Define(i => i.Add(k => k.Ascending("email"), new() { Unique = true }))], client: client, cancellationToken: cancel.Token);

        up.ExitCode.ShouldBe(ExitCodes.Failure);
        up.Message.ShouldBe("Collection things failed: InvalidOperationException: Rebuilding things.email_1 failed (The operation was canceled.); the previous index was restored.");
        var index = mongo.Indexes()["email_1"];
        index["sparse"].ToBoolean().ShouldBeTrue();
        index.Contains("unique").ShouldBeFalse();
    }

    [Fact]
    public async Task Making_an_index_unique_cancelled_after_prepare_unique_turns_it_off_again()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("email"))));
        using var cancel = new CancellationTokenSource();
        using var client = mongo.CancellingAfter("collMod", cancel);

        var up = await mongo.RunAsync("up", [Define(i => i.Add(k => k.Ascending("email"), new() { Unique = true }))], client: client, cancellationToken: cancel.Token);

        up.ExitCode.ShouldBe(ExitCodes.Failure);
        up.Message.ShouldBe("Collection things failed: The operation was cancelled.");
        var index = mongo.Indexes()["email_1"];
        index.GetValue("unique", false).ToBoolean().ShouldBeFalse();
        index.GetValue("prepareUnique", false).ToBoolean().ShouldBeFalse();
        mongo.History().Last()["operations"].AsBsonArray.Select(o => o["command"]["index"]["prepareUnique"].AsBoolean).ShouldBe([true, false]);
    }

    [Fact]
    public async Task Changing_the_collation_rebuilds_the_index_because_its_name_stays_the_same()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("name"), new() { Collation = new Collation("en", strength: CollationStrength.Secondary) })));

        var next = Define(i => i.Add(k => k.Ascending("name"), new() { Collation = new Collation("en", strength: CollationStrength.Primary) }));
        var plan = await mongo.PlanAsync(next);
        CollectionHarness.Changes(plan).ShouldBe(["rebuild name_1"]);
        plan.Collections!.Single().Changes.Single().Detail.ShouldBe("collation strength 2 → 1 (drop, then create)");
        await mongo.ApplyAsync(next);

        mongo.Indexes()["name_1"]["collation"]["strength"].ToInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Leaving_a_field_out_of_the_collation_rebuilds_the_index_with_the_default()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("name"), new() { Collation = new Collation("en", strength: CollationStrength.Secondary) })));

        var next = Define(i => i.Add(k => k.Ascending("name"), new() { Collation = new Collation("en") }));
        CollectionHarness.Changes(await mongo.PlanAsync(next)).ShouldBe(["rebuild name_1"]);
        await mongo.ApplyAsync(next);

        mongo.Indexes()["name_1"]["collation"]["strength"].ToInt32().ShouldBe(3);
    }

    [Fact]
    public async Task Changing_the_partial_filter_of_a_named_index_rebuilds_it()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("companyId"), new() { Name = "active", PartialFilterExpression = Builders<BsonDocument>.Filter.Eq("status", "open") })));

        var next = Define(i => i.Add(k => k.Ascending("companyId"), new() { Name = "active", PartialFilterExpression = Builders<BsonDocument>.Filter.In("status", ["open", "pending"]) }));
        CollectionHarness.Changes(await mongo.PlanAsync(next)).ShouldBe(["rebuild active"]);
        await mongo.ApplyAsync(next);

        mongo.Indexes()["active"]["partialFilterExpression"]["status"].AsBsonDocument.Names.ShouldBe(["$in"]);
    }

    [Fact]
    public async Task Changing_the_keys_of_a_named_index_rebuilds_it_instead_of_failing_with_a_conflict()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("companyId").Ascending("status"), new() { Name = "by_company" })));

        var next = Define(i => i.Add(k => k.Ascending("companyId").Ascending("status").Descending("createdAt"), new() { Name = "by_company" }));
        var plan = await mongo.PlanAsync(next);
        plan.Collections!.Single().Changes.Single().Detail.ShouldBe("keys { companyId: 1, status: 1 } → { companyId: 1, status: 1, createdAt: -1 } (drop, then create)");
        await mongo.ApplyAsync(next);

        mongo.Indexes()["by_company"]["key"].ShouldBe(new BsonDocument { { "companyId", 1 }, { "status", 1 }, { "createdAt", -1 } });
    }

    [Fact]
    public async Task Changing_the_keys_of_an_unnamed_index_builds_the_new_one_alongside_the_old()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("companyId"))));

        var next = Define(i => i.Add(k => k.Ascending("companyId").Ascending("status")));
        CollectionHarness.Changes(await mongo.PlanAsync(next)).ShouldBe(["create companyId_1_status_1", "keep companyId_1"]);
        await mongo.ApplyAsync(next);

        mongo.Indexes().Keys.ShouldBe(["_id_", "companyId_1", "companyId_1_status_1"], ignoreOrder: true);
    }

    [Fact]
    public async Task Changing_key_order_or_direction_is_a_different_index()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("a").Ascending("b"))));

        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("b").Ascending("a")).Add(k => k.Ascending("a").Descending("b"))), o => o.IndexesPrune = "drop");

        mongo.Indexes().Keys.ShouldBe(["_id_", "b_1_a_1", "a_1_b_-1"], ignoreOrder: true);
    }

    [Fact]
    public async Task Changing_a_text_indexs_fields_rebuilds_the_collections_one_text_index()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Text("title"))));

        var next = Define(i => i.Add(k => k.Text("title").Text("body")));
        CollectionHarness.Changes(await mongo.PlanAsync(next)).ShouldBe(["rebuild title_text"]);
        await mongo.ApplyAsync(next);

        var text = mongo.Indexes().Values.Single(i => i["key"].AsBsonDocument.Contains("_fts"));
        text["weights"].ShouldBe(new BsonDocument { { "body", 1 }, { "title", 1 } });
    }

    [Fact]
    public async Task Changing_text_weights_rebuilds_the_text_index()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Text("title").Text("body"))));

        await mongo.ApplyAsync(Define(i => i.Add(k => k.Text("title").Text("body"), new() { Weights = new BsonDocument("title", 10) })));

        mongo.Operations().ShouldBe(["dropIndex", "createIndex"]);
        mongo.Indexes()["title_text_body_text"]["weights"]["title"].ToInt32().ShouldBe(10);
    }

    [Fact]
    public async Task Changing_the_text_language_rebuilds_the_text_index()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Text("title"))));

        await mongo.ApplyAsync(Define(i => i.Add(k => k.Text("title"), new() { DefaultLanguage = "spanish" })));

        mongo.Indexes()["title_text"]["default_language"].AsString.ShouldBe("spanish");
    }

    [Fact]
    public async Task Changing_a_wildcard_projection_rebuilds_the_index()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Wildcard(), new() { WildcardProjection = Builders<BsonDocument>.Projection.Include("a") })));

        await mongo.ApplyAsync(Define(i => i.Add(k => k.Wildcard(), new() { WildcardProjection = Builders<BsonDocument>.Projection.Include("a").Include("b") })));

        mongo.Operations().ShouldBe(["dropIndex", "createIndex"]);
        mongo.Indexes()["$**_1"]["wildcardProjection"].AsBsonDocument.Names.ShouldBe(["a", "b"], ignoreOrder: true);
    }

    [Fact]
    public async Task Renaming_a_declared_index_doesnt_rebuild_it()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(Define(i => i.Add(k => k.Ascending("status"), new() { Name = "by_status" })));

        var renamed = Define(i => i.Add(k => k.Ascending("status"), new() { Name = "status_lookup" }));
        var up = await mongo.UpAsync(renamed);

        // Nothing changes on the server, but the new declaration is recorded.
        up.Reconciled.ShouldBeEmpty();
        mongo.History().Count.ShouldBe(2);
        await mongo.ShouldBeInSyncAsync(renamed);
        mongo.Indexes().Keys.ShouldBe(["_id_", "by_status"], ignoreOrder: true);
        var show = await mongo.RunAsync("show", [renamed], arguments: new CommandArguments { Id = CollectionHarness.Things });
        show.ShowCollection!.Indexes!.Single(i => i.Name == "by_status").DeclaredName.ShouldBe("status_lookup");
    }

    [Fact]
    public async Task Indexes_made_by_hand_are_adopted_whatever_they_are_called_and_however_their_numbers_are_stored()
    {
        await using var mongo = new CollectionHarness(environment);
        // As a shell would create them: doubles for the key directions and the TTL.
        await mongo.Database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "createIndexes", CollectionHarness.Things },
            {
                "indexes", new BsonArray
                {
                    new BsonDocument { { "key", new BsonDocument { { "companyId", 1.0 }, { "createdAt", -1.0 } } }, { "name", "legacy_company" } },
                    new BsonDocument { { "key", new BsonDocument("expiresAt", 1.0) }, { "name", "legacy_ttl" }, { "expireAfterSeconds", 3600.0 } },
                    new BsonDocument { { "key", new BsonDocument("title", "text") }, { "name", "legacy_text" } },
                }
            },
        });

        var up = await mongo.UpAsync(Define(i => i
            .Add(k => k.Ascending("companyId").Descending("createdAt"))
            .Add(k => k.Ascending("expiresAt"), new() { ExpireAfter = TimeSpan.FromHours(1) })
            .Add(k => k.Text("title"))));

        up.ExitCode.ShouldBe(0, up.Message);
        up.Reconciled.ShouldBeEmpty();
        mongo.Indexes().Keys.ShouldBe(["_id_", "legacy_company", "legacy_ttl", "legacy_text"], ignoreOrder: true);
    }

    [Fact]
    public async Task Indexes_changed_or_dropped_by_hand_are_put_back()
    {
        await using var mongo = new CollectionHarness(environment);
        var collection = Define(i => i.Add(k => k.Ascending("status")).Add(k => k.Ascending("expiresAt"), new() { ExpireAfter = TimeSpan.FromHours(1) }));
        await mongo.ApplyAsync(collection);

        await mongo.Collection().Indexes.DropOneAsync("status_1");
        await mongo.Database.RunCommandAsync<BsonDocument>(new BsonDocument
        {
            { "collMod", CollectionHarness.Things },
            { "index", new BsonDocument { { "name", "expiresAt_1" }, { "expireAfterSeconds", 60 } } },
        });

        var status = await mongo.RunAsync("status", [collection], arguments: new CommandArguments { Check = true });
        status.ExitCode.ShouldBe(ExitCodes.PendingMigrations);
        status.Collections!.Single().State.ShouldBe(CollectionStates.Changes);

        await mongo.ApplyAsync(collection);
        var indexes = mongo.Indexes();
        indexes.Keys.ShouldContain("status_1");
        indexes["expiresAt_1"]["expireAfterSeconds"].ToInt64().ShouldBe(3600);
    }

    [Fact]
    public async Task Several_changes_run_creates_first_and_removals_last()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(Define(i => i
            .Add(k => k.Ascending("old"))
            .Add(k => k.Ascending("expiresAt"), new() { ExpireAfter = TimeSpan.FromHours(1) })
            .Add(k => k.Ascending("email"))));

        await mongo.ApplyAsync(
            Define(i => i
                .Add(k => k.Ascending("added"))
                .Add(k => k.Ascending("expiresAt"), new() { ExpireAfter = TimeSpan.FromHours(2) })
                .Add(k => k.Ascending("email"), new() { Sparse = true })),
            o => o.IndexesPrune = "drop");

        mongo.Operations().ShouldBe(["createIndex", "runCommand", "dropIndex", "createIndex", "dropIndex"]);
        mongo.Indexes().Keys.ShouldBe(["_id_", "added_1", "expiresAt_1", "email_1"], ignoreOrder: true);
    }

    private static CollectionDescriptor Define(Action<IndexesBuilder<BsonDocument>> indexes) => CollectionHarness.Define(indexes);
}
