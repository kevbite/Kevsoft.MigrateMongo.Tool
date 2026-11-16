using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.Protocol;
using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests.Collections;

/// <summary>
/// <c>$jsonSchema</c> validators against a real server: created with the collection, changed with <c>collMod</c>, existing
/// documents that fail a new one counted, and undeclared ones reported or removed.
/// </summary>
public sealed class ValidatorTests(IntegrationEnvironment environment)
{
    private const string NameRequired = """{ "bsonType": "object", "required": ["name"] }""";
    private const string TierRequired = """{ "bsonType": "object", "required": ["name", "tier"] }""";

    [Fact]
    public async Task A_validator_is_created_with_the_collection()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(CollectionHarness.Define<BsonDocument>(c => c.Validator.JsonSchema(NameRequired).Level(ValidationLevel.Moderate).Action(ValidationAction.Warn)));

        Options(mongo).ShouldBe(BsonDocument.Parse("""
            {
              "validator": { "$jsonSchema": { "bsonType": "object", "required": ["name"] } },
              "validationLevel": "moderate",
              "validationAction": "warn"
            }
            """));
        mongo.Operations().ShouldBe(["runCommand"]);
    }

    [Fact]
    public async Task A_changed_schema_level_and_action_are_applied_with_collMod()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(CollectionHarness.Define<BsonDocument>(c => c.Validator.JsonSchema(NameRequired)));
        Options(mongo).ShouldBe(BsonDocument.Parse("""{ "validator": { "$jsonSchema": { "bsonType": "object", "required": ["name"] } } }"""));

        var changed = CollectionHarness.Define<BsonDocument>(c => c.Validator.JsonSchema(TierRequired).Level(ValidationLevel.Moderate).Action(ValidationAction.Warn));
        var plan = await mongo.PlanAsync(changed);
        var change = plan.Collections.ShouldNotBeNull().Single().Changes.ShouldHaveSingleItem();
        (change.Action, change.Index, change.Detail).ShouldBe(("set-validator", "validator", "$jsonSchema changed; validationLevel strict → moderate; validationAction error → warn"));

        await mongo.ApplyAsync(changed);

        Options(mongo).ShouldBe(BsonDocument.Parse("""
            {
              "validator": { "$jsonSchema": { "bsonType": "object", "required": ["name", "tier"] } },
              "validationLevel": "moderate",
              "validationAction": "warn"
            }
            """));
        mongo.Operations().ShouldBe(["runCommand"]);
    }

    [Fact]
    public async Task Existing_documents_that_fail_a_new_validator_are_counted_and_warned_about_by_default()
    {
        await using var mongo = await SeededAsync(environment);

        var plan = await mongo.PlanAsync(Tiered);
        plan.Collections.ShouldNotBeNull().Single().Changes.ShouldHaveSingleItem().Warning
            .ShouldBe("2 existing documents don't match the validator (e.g. 1, 2)");

        var up = await mongo.UpAsync(Tiered);

        up.ExitCode.ShouldBe(ExitCodes.Success, up.Message);
        up.Issues!.ShouldContain(i => i.Message.Contains("2 existing documents don't match the validator"));
        Options(mongo)["validator"]["$jsonSchema"]["required"].AsBsonArray.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Failing_documents_block_the_validator_in_fail_mode_unless_allowed()
    {
        await using var mongo = await SeededAsync(environment);

        var up = await mongo.UpAsync(Tiered, FailOnInvalid);

        up.ExitCode.ShouldBe(ExitCodes.ValidationFailed);
        up.Issues!.ShouldContain(i => i.Message.StartsWith("things: 2 existing documents don't match the validator (e.g. 1, 2). Fix them first"));
        Options(mongo)["validator"]["$jsonSchema"]["required"].AsBsonArray.Count.ShouldBe(1);

        await mongo.ApplyAsync(Tiered, FailOnInvalid, new CommandArguments { AllowInvalidDocuments = ["things"] });
        Options(mongo)["validator"]["$jsonSchema"]["required"].AsBsonArray.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Documents_are_not_counted_when_counting_is_off()
    {
        await using var mongo = await SeededAsync(environment);

        var plan = await mongo.PlanAsync(Tiered, o =>
        {
            FailOnInvalid(o);
            o.ValidatorsCountInvalidDocuments = false;
        });

        plan.Issues?.ShouldNotContain(i => i.Message.Contains("existing document"));
        var change = plan.Collections.ShouldNotBeNull().Single().Changes.ShouldHaveSingleItem();
        change.Blocked.ShouldNotBe(true);
        change.Warning.ShouldBeNull();
    }

    [Fact]
    public async Task An_undeclared_validator_is_reported_by_default_and_removed_when_prune_is_remove()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(CollectionHarness.Define<BsonDocument>(c => c.Validator.JsonSchema(NameRequired)));

        var undeclared = CollectionHarness.Define<BsonDocument>(_ => { });
        CollectionHarness.Changes(await mongo.PlanAsync(undeclared)).ShouldBe(["keep validator"]);
        (await mongo.UpAsync(undeclared)).Reconciled.ShouldBeEmpty();
        await mongo.ShouldBeInSyncAsync(undeclared);
        Options(mongo).Contains("validator").ShouldBeTrue();

        CollectionHarness.Changes(await mongo.PlanAsync(undeclared, RemoveValidators)).ShouldBe(["remove-validator validator"]);
        await mongo.ApplyAsync(undeclared, RemoveValidators);
        Options(mongo).GetValue("validator", new BsonDocument()).AsBsonDocument.ShouldBe(new BsonDocument());
        mongo.Operations().ShouldBe(["runCommand"]);
    }

    private static CollectionDescriptor Tiered => CollectionHarness.Define<BsonDocument>(c => c.Validator.JsonSchema(TierRequired));

    private static void FailOnInvalid(EngineOptions options) => options.ValidatorsOnInvalidDocuments = "fail";

    private static void RemoveValidators(EngineOptions options) => options.ValidatorsPrune = "remove";

    /// <summary>A collection validated by <see cref="NameRequired"/> with two documents that have no tier and one that has.</summary>
    private static async Task<CollectionHarness> SeededAsync(IntegrationEnvironment environment)
    {
        var mongo = new CollectionHarness(environment);
        await mongo.ApplyAsync(CollectionHarness.Define<BsonDocument>(c => c.Validator.JsonSchema(NameRequired)));
        await mongo.Collection().InsertManyAsync([
            new BsonDocument { { "_id", 1 }, { "name", "a" } },
            new BsonDocument { { "_id", 2 }, { "name", "b" } },
            new BsonDocument { { "_id", 3 }, { "name", "c" }, { "tier", "gold" } },
        ]);
        return mongo;
    }

    private static BsonDocument Options(CollectionHarness mongo) =>
        mongo.Database.ListCollections(new ListCollectionsOptions { Filter = new BsonDocument("name", CollectionHarness.Things) }).Single()["options"].AsBsonDocument;
}
