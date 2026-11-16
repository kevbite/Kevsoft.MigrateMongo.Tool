using Kevsoft.MigrateMongo.Protocol;
using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests.Collections;

/// <summary>
/// Collection options against a real server: explicit creation, capped, time series, clustered, default collation and
/// change stream pre- and post-images, what changes in place and what can't.
/// </summary>
public sealed class CollectionOptionsTests(IntegrationEnvironment environment)
{
    [Fact]
    public async Task A_managed_collection_without_options_is_created_explicitly()
    {
        await using var mongo = new CollectionHarness(environment);
        var collection = CollectionHarness.Define<BsonDocument>(_ => { });
        CollectionHarness.Changes(await mongo.PlanAsync(collection)).ShouldBe(["create-collection collection"]);

        await mongo.ApplyAsync(collection);

        var entry = ListCollection(mongo);
        entry["type"].AsString.ShouldBe("collection");
        entry["options"].AsBsonDocument.ShouldBe(new BsonDocument());
        mongo.Operations().ShouldBe(["runCommand"]);
    }

    [Fact]
    public async Task A_capped_collection_is_created_and_resized_in_place()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(CollectionHarness.Define<BsonDocument>(c => c.Options.Capped(5000, 100)));
        Options(mongo).ShouldBe(BsonDocument.Parse("{ capped: true, size: 5000, max: 100 }"));

        var resized = CollectionHarness.Define<BsonDocument>(c => c.Options.Capped(10_000, 200));
        CollectionHarness.Changes(await mongo.PlanAsync(resized)).ShouldBe(["modify-collection cappedSize", "modify-collection cappedMax"]);
        await mongo.ApplyAsync(resized);

        Options(mongo).ShouldBe(BsonDocument.Parse("{ capped: true, size: 10000, max: 200 }"));
        mongo.Operations().ShouldBe(["runCommand", "runCommand"]);
    }

    [Fact]
    public async Task Converting_to_capped_is_blocked_by_default()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(CollectionHarness.Define<BsonDocument>(c => c.Indexes.Add(k => k.Ascending("status"))));
        await mongo.Collection().InsertOneAsync(new BsonDocument("status", "new"));

        var capped = CollectionHarness.Define<BsonDocument>(c =>
        {
            c.Options.Capped(1_000_000);
            c.Indexes.Add(k => k.Ascending("status"));
        });
        var up = await mongo.UpAsync(capped);

        up.ExitCode.ShouldBe(ExitCodes.ValidationFailed);
        up.Issues!.ShouldContain(i => i.Message.Contains("--allow-convert-to-capped things"));
        Options(mongo).ShouldBe(new BsonDocument());
        mongo.Indexes().Keys.ShouldBe(["_id_", "status_1"], ignoreOrder: true);
    }

    [Fact]
    public async Task Converting_to_capped_when_allowed_recreates_the_indexes()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(CollectionHarness.Define<BsonDocument>(c => c.Indexes.Add(k => k.Ascending("status"))));
        await mongo.Collection().InsertOneAsync(new BsonDocument("status", "new"));

        var capped = CollectionHarness.Define<BsonDocument>(c =>
        {
            c.Options.Capped(1_000_000);
            c.Indexes.Add(k => k.Ascending("status"));
        });
        CollectionHarness.Changes(await mongo.PlanAsync(capped, arguments: new CommandArguments { AllowConvertToCapped = ["things"] }))
            .ShouldBe(["convert-to-capped capped"]);
        await mongo.ApplyAsync(capped, arguments: new CommandArguments { AllowConvertToCapped = ["things"] });

        Options(mongo).ShouldBe(BsonDocument.Parse("{ capped: true, size: 1000000 }"));
        mongo.Indexes().Keys.ShouldBe(["_id_", "status_1"], ignoreOrder: true);
        mongo.Collection().CountDocuments(FilterDefinition<BsonDocument>.Empty).ShouldBe(1);
        mongo.Operations().ShouldBe(["runCommand", "createIndex"]);
    }

    [Fact]
    public async Task Converting_to_capped_can_be_allowed_in_the_config()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(CollectionHarness.Define<BsonDocument>(_ => { }));

        await mongo.ApplyAsync(CollectionHarness.Define<BsonDocument>(c => c.Options.Capped(4096)), o => o.CollectionsOnConvertToCapped = "allow");

        Options(mongo).ShouldBe(BsonDocument.Parse("{ capped: true, size: 4096 }"));
    }

    [Fact]
    public async Task A_time_series_collection_is_created_and_changed_in_place()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(TimeSeries("sensor", TimeSeriesGranularity.Seconds, TimeSpan.FromDays(1)));
        var entry = ListCollection(mongo);
        entry["type"].AsString.ShouldBe("timeseries");
        entry["options"]["timeseries"].AsBsonDocument
            .ShouldBe(BsonDocument.Parse("{ timeField: 'at', metaField: 'sensor', granularity: 'seconds', bucketMaxSpanSeconds: 3600 }"));
        entry["options"]["expireAfterSeconds"].ToInt64().ShouldBe(86400);

        var changed = TimeSeries("sensor", TimeSeriesGranularity.Minutes, TimeSpan.FromDays(2));
        CollectionHarness.Changes(await mongo.PlanAsync(changed))
            .ShouldBe(["modify-collection timeseries.granularity", "modify-collection expireAfterSeconds"], ignoreOrder: true);
        await mongo.ApplyAsync(changed);
        entry = ListCollection(mongo);
        entry["options"]["timeseries"]["granularity"].AsString.ShouldBe("minutes");
        entry["options"]["expireAfterSeconds"].ToInt64().ShouldBe(172800);
        mongo.Operations().ShouldBe(["runCommand", "runCommand"]);

        var noExpiry = TimeSeries("sensor", TimeSeriesGranularity.Minutes, null);
        CollectionHarness.Changes(await mongo.PlanAsync(noExpiry)).ShouldBe(["modify-collection expireAfterSeconds"]);
        await mongo.ApplyAsync(noExpiry);
        Options(mongo).Contains("expireAfterSeconds").ShouldBeFalse();
    }

    [Fact]
    public async Task A_finer_granularity_or_another_meta_field_is_unchangeable()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(TimeSeries("sensor", TimeSeriesGranularity.Minutes, TimeSpan.FromDays(1)));

        var up = await mongo.UpAsync(TimeSeries("device", TimeSeriesGranularity.Seconds, TimeSpan.FromDays(2)));

        up.ExitCode.ShouldBe(ExitCodes.ValidationFailed);
        var changes = up.Collections.ShouldNotBeNull().Single().Changes;
        changes.Where(c => c.Blocked == true).Select(c => $"{c.Index}: {c.Detail}")
            .ShouldBe(["timeseries.metaField: sensor → device", "timeseries.granularity: minutes → seconds"], ignoreOrder: true);
        var options = Options(mongo);
        options["timeseries"]["metaField"].AsString.ShouldBe("sensor");
        options["expireAfterSeconds"].ToInt64().ShouldBe(86400, "nothing changes while a change is blocked");
    }

    [Fact]
    public async Task An_unchangeable_option_can_be_skipped_and_the_rest_applied()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(TimeSeries("sensor", TimeSeriesGranularity.Seconds, TimeSpan.FromDays(1)));

        var byDefinition = TimeSeries("device", TimeSeriesGranularity.Seconds, TimeSpan.FromDays(2), UnchangeableOption.Skip);
        var plan = await mongo.PlanAsync(byDefinition);
        CollectionHarness.Changes(plan).ShouldBe(["skip timeseries.metaField", "modify-collection expireAfterSeconds"], ignoreOrder: true);
        var up = await mongo.UpAsync(byDefinition);

        up.ExitCode.ShouldBe(ExitCodes.Success, up.Message);
        var options = Options(mongo);
        options["timeseries"]["metaField"].AsString.ShouldBe("sensor");
        options["expireAfterSeconds"].ToInt64().ShouldBe(172800);
        up.Issues!.ShouldContain(i => i.Message.Contains("timeseries.metaField sensor → device can't be changed on an existing collection, so it's skipped"));

        var byArgument = await mongo.UpAsync(
            TimeSeries("device", TimeSeriesGranularity.Seconds, TimeSpan.FromDays(3)),
            arguments: new CommandArguments { SkipUnchangeable = ["things"] });
        byArgument.ExitCode.ShouldBe(ExitCodes.Success, byArgument.Message);
        Options(mongo)["expireAfterSeconds"].ToInt64().ShouldBe(259200);

        var byConfig = await mongo.UpAsync(TimeSeries("device", TimeSeriesGranularity.Seconds, TimeSpan.FromDays(4)), o => o.CollectionsOnUnchangeable = "skip");
        byConfig.ExitCode.ShouldBe(ExitCodes.Success, byConfig.Message);
        Options(mongo)["expireAfterSeconds"].ToInt64().ShouldBe(345600);
    }

    [Fact]
    public async Task A_clustered_collection_with_a_ttl_has_its_expiry_changed_in_place()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(CollectionHarness.Define<BsonDocument>(c =>
        {
            c.Options.Clustered("by_id", TimeSpan.FromHours(1));
            c.Indexes.Add(k => k.Ascending("status"));
        }));
        var options = Options(mongo);
        options["clusteredIndex"]["name"].AsString.ShouldBe("by_id");
        options["clusteredIndex"]["key"].AsBsonDocument.ShouldBe(new BsonDocument("_id", 1));
        options["expireAfterSeconds"].ToInt64().ShouldBe(3600);

        var changed = CollectionHarness.Define<BsonDocument>(c =>
        {
            c.Options.Clustered("by_id", TimeSpan.FromHours(2));
            c.Indexes.Add(k => k.Ascending("status"));
        });
        CollectionHarness.Changes(await mongo.PlanAsync(changed)).ShouldBe(["modify-collection expireAfterSeconds"]);
        await mongo.ApplyAsync(changed);
        Options(mongo)["expireAfterSeconds"].ToInt64().ShouldBe(7200);
    }

    [Fact]
    public async Task A_default_collation_is_created_and_another_one_is_unchangeable()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(CollectionHarness.Define<BsonDocument>(c =>
        {
            c.Options.Collation(new Collation("fr", strength: CollationStrength.Secondary));
            c.Indexes.Add(k => k.Ascending("name"));
        }));
        var collation = Options(mongo)["collation"].AsBsonDocument;
        collation["locale"].AsString.ShouldBe("fr");
        collation["strength"].ToInt32().ShouldBe(2);
        mongo.Indexes()["name_1"]["collation"]["locale"].AsString.ShouldBe("fr");

        var up = await mongo.UpAsync(CollectionHarness.Define<BsonDocument>(c =>
        {
            c.Options.Collation(new Collation("de"));
            c.Indexes.Add(k => k.Ascending("name"));
        }));

        up.ExitCode.ShouldBe(ExitCodes.ValidationFailed);
        up.Issues!.ShouldContain(i => i.Message.StartsWith("things: the default collation can't change in place."));
        Options(mongo)["collation"]["locale"].AsString.ShouldBe("fr");
    }

    [Fact]
    public async Task Change_stream_pre_and_post_images_are_turned_on_and_off_and_left_alone_when_undeclared()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.Database.CreateCollectionAsync(CollectionHarness.Things);

        var on = CollectionHarness.Define<BsonDocument>(c => c.Options.ChangeStreamPreAndPostImages());
        CollectionHarness.Changes(await mongo.PlanAsync(on)).ShouldBe(["modify-collection changeStreamPreAndPostImages"]);
        await mongo.ApplyAsync(on);
        Options(mongo)["changeStreamPreAndPostImages"].AsBsonDocument.ShouldBe(new BsonDocument("enabled", true));
        mongo.Operations().ShouldBe(["runCommand"]);

        var undeclared = CollectionHarness.Define<BsonDocument>(_ => { });
        (await mongo.UpAsync(undeclared)).Reconciled.ShouldBeEmpty();
        await mongo.ShouldBeInSyncAsync(undeclared);
        Options(mongo)["changeStreamPreAndPostImages"]["enabled"].AsBoolean.ShouldBeTrue();

        await mongo.ApplyAsync(CollectionHarness.Define<BsonDocument>(c => c.Options.ChangeStreamPreAndPostImages(false)));
        Options(mongo).GetValue("changeStreamPreAndPostImages", new BsonDocument("enabled", false))["enabled"].AsBoolean.ShouldBeFalse();
    }

    private static CollectionDescriptor TimeSeries(string meta, TimeSeriesGranularity granularity, TimeSpan? expireAfter, UnchangeableOption? onUnchangeable = null) =>
        CollectionHarness.Define<BsonDocument>(c =>
        {
            c.Options.TimeSeries("at", meta, granularity);
            if (expireAfter is { } expiry)
            {
                c.Options.ExpireAfter(expiry);
            }

            c.Options.OnUnchangeable = onUnchangeable;
        });

    private static BsonDocument ListCollection(CollectionHarness mongo) =>
        mongo.Database.ListCollections(new ListCollectionsOptions { Filter = new BsonDocument("name", CollectionHarness.Things) }).Single();

    private static BsonDocument Options(CollectionHarness mongo) => ListCollection(mongo)["options"].AsBsonDocument;
}
