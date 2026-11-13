using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.MongoDriver;
using MongoDB.Bson;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.MongoDriver;

public sealed class CollectionOptionsPlannerTests
{
    private const int MongoDb80 = 25;

    [Fact]
    public void A_missing_collection_is_created_with_every_declared_option_and_the_validator()
    {
        var plan = Plan(
            """{ "capped": true, "size": 4096, "max": 10 }""",
            existing: null,
            validator: new ValidatorDeclaration { Validator = BsonDocument.Parse("""{ "$jsonSchema": { "required": ["name"] } }"""), Level = "moderate" });

        var change = plan.Changes.ShouldHaveSingleItem();
        change.Info.Action.ShouldBe("create-collection");
        change.Info.Index.ShouldBe("collection");
        change.Commands.ShouldBe([
            BsonDocument.Parse("""
                {
                  "create": "events", "capped": true, "size": 4096, "max": 10,
                  "validator": { "$jsonSchema": { "required": ["name"] } }, "validationLevel": "moderate"
                }
                """),
        ]);
        plan.Problems.ShouldBeEmpty();
    }

    [Fact]
    public void A_missing_collection_without_options_is_still_created()
    {
        var plan = Plan("{}", existing: null);

        var change = plan.Changes.ShouldHaveSingleItem();
        change.Info.Action.ShouldBe("create-collection");
        change.Info.Detail.ShouldBeNull();
        change.Commands.ShouldBe([new BsonDocument("create", "events")]);
        plan.Declaration.ShouldBe("", "a definition without options keeps the index-only declaration hash");
    }

    [Fact]
    public void A_capped_collection_is_resized_in_place_on_6_0_and_later()
    {
        var plan = Plan("""{ "capped": true, "size": 8192, "max": 20 }""", Existing("""{ "capped": true, "size": 4096, "max": 10 }"""));

        plan.Changes.Select(c => (c.Info.Action, c.Info.Index, c.Info.Detail)).ShouldBe([
            ("modify-collection", "cappedSize", "4,096 → 8,192"),
            ("modify-collection", "cappedMax", "10 → 20"),
        ]);
        plan.Changes.SelectMany(c => c.Commands).ShouldBe([
            BsonDocument.Parse("""{ "collMod": "events", "cappedSize": 8192 }"""),
            BsonDocument.Parse("""{ "collMod": "events", "cappedMax": 20 }"""),
        ]);
        plan.Problems.ShouldBeEmpty();
    }

    [Fact]
    public void A_capped_size_the_server_rounded_up_to_a_multiple_of_256_is_the_same()
    {
        var plan = Plan("""{ "capped": true, "size": 5000 }""", Existing("""{ "capped": true, "size": 5120 }"""));

        plan.Changes.ShouldBeEmpty();
    }

    [Fact]
    public void Resizing_a_capped_collection_before_6_0_is_unchangeable()
    {
        var plan = Plan("""{ "capped": true, "size": 8192 }""", Existing("""{ "capped": true, "size": 4096 }"""), wireVersion: 16);

        var change = plan.Changes.ShouldHaveSingleItem();
        (change.Info.Action, change.Info.Index, change.Info.Blocked).ShouldBe(("modify-collection", "cappedSize", true));
        change.Commands.ShouldBeEmpty();
        plan.Problems.ShouldHaveSingleItem().ShouldStartWith("events: a capped collection's size can only change on MongoDB 6.0+, so it can't change in place.");
    }

    [Fact]
    public void Converting_to_capped_is_blocked_unless_allowed()
    {
        var blocked = Plan("""{ "capped": true, "size": 1048576 }""", Existing("{}"));
        var change = blocked.Changes.ShouldHaveSingleItem();
        (change.Info.Action, change.Info.Blocked).ShouldBe(("convert-to-capped", true));
        blocked.Problems.ShouldHaveSingleItem().ShouldEndWith("Run with --allow-convert-to-capped events, or set collections.onConvertToCapped: allow.");

        var allowed = Plan("""{ "capped": true, "size": 1048576 }""", Existing("{}"), new CollectionPolicy { AllowConvertToCapped = true });
        allowed.Problems.ShouldBeEmpty();
        allowed.Changes.ShouldHaveSingleItem().Commands.ShouldBe([BsonDocument.Parse("""{ "convertToCapped": "events", "size": 1048576 }""")]);

        var byConfig = Plan("""{ "capped": true, "size": 1048576 }""", Existing("{}"), new CollectionPolicy { OnConvertToCapped = "allow" });
        byConfig.Problems.ShouldBeEmpty();
    }

    [Fact]
    public void A_definition_can_refuse_converting_to_capped_although_the_config_allows_it()
    {
        var plan = Plan(
            """{ "capped": true, "size": 1048576 }""",
            Existing("{}"),
            new CollectionPolicy { OnConvertToCapped = "allow" },
            onConvertToCapped: ConvertToCapped.Fail);

        plan.Changes.ShouldHaveSingleItem().Info.Blocked.ShouldBe(true);
        plan.Problems.ShouldHaveSingleItem().ShouldEndWith("The definition sets OnConvertToCapped = Fail.");
    }

    [Fact]
    public void Another_meta_field_is_unchangeable()
    {
        var plan = Plan(
            """{ "timeseries": { "timeField": "at", "metaField": "device", "granularity": "seconds" } }""",
            TimeSeries("""{ "timeseries": { "timeField": "at", "metaField": "sensor", "granularity": "seconds", "bucketMaxSpanSeconds": 3600 } }"""));

        var change = plan.Changes.ShouldHaveSingleItem();
        (change.Info.Action, change.Info.Index, change.Info.Detail, change.Info.Blocked, change.Info.Reason)
            .ShouldBe(("modify-collection", "timeseries.metaField", "sensor → device", true, "can't be changed on an existing collection"));
        plan.Problems.ShouldBe([
            "events: the time series metaField can't change in place. Write a migration that copies the data into a new collection with " +
            "the new options and renames it, drop the collection if the data can go, or skip the change with --skip-unchangeable events.",
        ]);
    }

    [Theory]
    [InlineData("argument")]
    [InlineData("config")]
    [InlineData("definition")]
    public void An_unchangeable_option_is_skipped_with_a_warning_when_asked_to(string by)
    {
        var plan = Plan(
            """{ "timeseries": { "timeField": "at", "metaField": "device" }, "expireAfterSeconds": 60 }""",
            TimeSeries("""{ "timeseries": { "timeField": "at", "metaField": "sensor", "granularity": "seconds" } }"""),
            new CollectionPolicy { SkipUnchangeable = by == "argument", OnUnchangeable = by == "config" ? "skip" : "fail" },
            onUnchangeable: by == "definition" ? UnchangeableOption.Skip : null);

        plan.Problems.ShouldBeEmpty();
        plan.Changes.Select(c => (c.Info.Action, c.Info.Index)).ShouldBe([("skip", "timeseries.metaField"), ("modify-collection", "expireAfterSeconds")]);
        plan.Changes[0].Info.Warning.ShouldBe("timeseries.metaField sensor → device can't be changed on an existing collection, so it's skipped");
        plan.Changes[0].Commands.ShouldBeEmpty();
        plan.Changes[1].Commands.ShouldBe([BsonDocument.Parse("""{ "collMod": "events", "expireAfterSeconds": 60 }""")]);
    }

    [Fact]
    public void A_definition_that_fails_on_unchangeable_options_overrides_a_config_that_skips_them()
    {
        var plan = Plan(
            """{ "timeseries": { "timeField": "when" } }""",
            TimeSeries("""{ "timeseries": { "timeField": "at", "granularity": "seconds" } }"""),
            new CollectionPolicy { OnUnchangeable = "skip" },
            onUnchangeable: UnchangeableOption.Fail);

        plan.Changes.ShouldHaveSingleItem().Info.Blocked.ShouldBe(true);
        plan.Problems.ShouldHaveSingleItem();
    }

    [Fact]
    public void A_coarser_granularity_changes_in_place_and_a_finer_one_is_unchangeable()
    {
        var coarser = Plan(
            """{ "timeseries": { "timeField": "at", "granularity": "hours" } }""",
            TimeSeries("""{ "timeseries": { "timeField": "at", "granularity": "minutes", "bucketMaxSpanSeconds": 86400 } }"""));
        var change = coarser.Changes.ShouldHaveSingleItem();
        (change.Info.Index, change.Info.Detail).ShouldBe(("timeseries.granularity", "minutes → hours"));
        change.Commands.ShouldBe([BsonDocument.Parse("""{ "collMod": "events", "timeseries": { "granularity": "hours" } }""")]);

        var finer = Plan(
            """{ "timeseries": { "timeField": "at", "granularity": "seconds" } }""",
            TimeSeries("""{ "timeseries": { "timeField": "at", "granularity": "minutes", "bucketMaxSpanSeconds": 86400 } }"""));
        finer.Changes.ShouldHaveSingleItem().Info.Blocked.ShouldBe(true);
        finer.Problems.ShouldHaveSingleItem().ShouldStartWith("events: the time series granularity can only be made coarser, so it can't change in place.");
    }

    [Fact]
    public void Removing_a_time_series_expiry_turns_it_off()
    {
        var plan = Plan(
            """{ "timeseries": { "timeField": "at" } }""",
            TimeSeries("""{ "timeseries": { "timeField": "at", "granularity": "seconds" }, "expireAfterSeconds": 3600 }"""));

        var change = plan.Changes.ShouldHaveSingleItem();
        (change.Info.Index, change.Info.Detail).ShouldBe(("expireAfterSeconds", "3,600 → none"));
        change.Commands.ShouldBe([BsonDocument.Parse("""{ "collMod": "events", "expireAfterSeconds": "off" }""")]);
    }

    [Fact]
    public void A_clustered_collection_expiry_changes_in_place_and_becoming_clustered_is_unchangeable()
    {
        var plan = Plan(
            """{ "clusteredIndex": { "key": { "_id": 1 }, "unique": true }, "expireAfterSeconds": 7200 }""",
            Existing("""{ "clusteredIndex": { "v": 2, "key": { "_id": 1 }, "name": "_id_", "unique": true }, "expireAfterSeconds": 3600 }"""));
        plan.Changes.ShouldHaveSingleItem().Commands.ShouldBe([BsonDocument.Parse("""{ "collMod": "events", "expireAfterSeconds": 7200 }""")]);

        var notClustered = Plan("""{ "clusteredIndex": { "key": { "_id": 1 }, "unique": true } }""", Existing("{}"));
        notClustered.Changes.ShouldHaveSingleItem().Info.Index.ShouldBe("clusteredIndex");
        notClustered.Problems.ShouldHaveSingleItem().ShouldStartWith("events: a collection can't become clustered, so it can't change in place.");
    }

    [Fact]
    public void A_default_collation_is_compared_as_the_server_fills_it_in()
    {
        const string server = """
            {
              "collation": {
                "locale": "fr", "caseLevel": false, "caseFirst": "off", "strength": 2, "numericOrdering": false,
                "alternate": "non-ignorable", "maxVariable": "punct", "normalization": false, "backwards": false, "version": "57.1"
              }
            }
            """;
        var filled = BsonDocument.Parse("""
            {
              "locale": "fr", "caseLevel": false, "caseFirst": "off", "strength": 2, "numericOrdering": false,
              "alternate": "non-ignorable", "maxVariable": "punct", "normalization": false, "backwards": false, "version": "57.1"
            }
            """);

        Plan("""{ "collation": { "locale": "fr", "strength": 2 } }""", Existing(server), filledCollation: filled).Changes.ShouldBeEmpty();
        Plan("""{ "collation": { "locale": "fr", "strength": 2 } }""", Existing(server)).Changes.ShouldBeEmpty();

        var changed = Plan("""{ "collation": { "locale": "de" } }""", Existing(server));
        changed.Changes.ShouldHaveSingleItem().Info.Index.ShouldBe("collation");
        changed.Problems.ShouldHaveSingleItem().ShouldStartWith("events: the default collation can't change in place.");
    }

    [Fact]
    public void Change_stream_pre_and_post_images_are_changed_in_place_and_left_alone_when_undeclared()
    {
        var on = Plan("""{ "changeStreamPreAndPostImages": { "enabled": true } }""", Existing("{}"));
        on.Changes.ShouldHaveSingleItem().Commands.ShouldBe([
            BsonDocument.Parse("""{ "collMod": "events", "changeStreamPreAndPostImages": { "enabled": true } }"""),
        ]);

        Plan("""{ "changeStreamPreAndPostImages": { "enabled": false } }""", Existing("{}")).Changes.ShouldBeEmpty();
        Plan("{}", Existing("""{ "changeStreamPreAndPostImages": { "enabled": true } }""")).Changes.ShouldBeEmpty();
    }

    [Fact]
    public void A_changed_validator_level_and_action_are_set_with_one_collMod()
    {
        var plan = Plan(
            "{}",
            Existing("""{ "validator": { "$jsonSchema": { "required": ["name"] } } }"""),
            validator: new ValidatorDeclaration
            {
                Validator = BsonDocument.Parse("""{ "$jsonSchema": { "required": ["name", "tier"] } }"""),
                Level = "moderate",
                Action = "warn",
            });

        var change = plan.Changes.ShouldHaveSingleItem();
        (change.Info.Action, change.Info.Index, change.Info.Detail)
            .ShouldBe(("set-validator", "validator", "$jsonSchema changed; validationLevel strict → moderate; validationAction error → warn"));
        change.Commands.ShouldBe([
            BsonDocument.Parse("""
                {
                  "collMod": "events", "validator": { "$jsonSchema": { "required": ["name", "tier"] } },
                  "validationLevel": "moderate", "validationAction": "warn"
                }
                """),
        ]);
    }

    [Fact]
    public void The_same_validator_with_its_default_level_and_action_is_unchanged()
    {
        var plan = Plan(
            "{}",
            Existing("""{ "validator": { "$jsonSchema": { "required": ["name"] } }, "validationLevel": "strict", "validationAction": "error" }"""),
            validator: new ValidatorDeclaration { Validator = BsonDocument.Parse("""{ "$jsonSchema": { "required": ["name"] } }"""), Level = "strict" });

        plan.Changes.ShouldBeEmpty();
    }

    [Fact]
    public void Existing_documents_that_fail_the_validator_are_a_warning_by_default()
    {
        var plan = Plan("{}", Existing("{}"), validator: NameRequired, invalid: new InvalidDocumentCount(2, [1, 2]));

        plan.Problems.ShouldBeEmpty();
        plan.Changes.ShouldHaveSingleItem().Info.Warning.ShouldBe("2 existing documents don't match the validator (e.g. 1, 2)");
    }

    [Fact]
    public void Existing_documents_that_fail_the_validator_block_it_in_fail_mode_unless_allowed()
    {
        var blocked = Plan("{}", Existing("{}"), new CollectionPolicy { OnInvalidDocuments = "fail" }, validator: NameRequired, invalid: new InvalidDocumentCount(1, [7]));
        var change = blocked.Changes.ShouldHaveSingleItem();
        (change.Info.Blocked, change.Info.Reason).ShouldBe((true, "1 existing document doesn't match the validator (e.g. 7)"));
        blocked.Problems.ShouldHaveSingleItem().ShouldStartWith(
            "events: 1 existing document doesn't match the validator (e.g. 7). Fix them first (e.g. in a migration), or run with --allow-invalid-documents events");

        var allowed = Plan("{}", Existing("{}"), new CollectionPolicy { OnInvalidDocuments = "fail", AllowInvalidDocuments = true }, validator: NameRequired, invalid: new InvalidDocumentCount(1, [7]));
        allowed.Problems.ShouldBeEmpty();
        allowed.Changes.ShouldHaveSingleItem().Info.Warning.ShouldBe("1 existing document doesn't match the validator (e.g. 7)");
    }

    [Fact]
    public void An_undeclared_validator_is_reported_or_removed()
    {
        const string server = """{ "validator": { "$jsonSchema": { "required": ["name"] } } }""";

        var report = Plan("{}", Existing(server));
        var kept = report.Changes.ShouldHaveSingleItem();
        (kept.Info.Action, kept.Info.Index, kept.Info.Detail).ShouldBe(("keep", "validator", "not declared"));
        kept.Commands.ShouldBeEmpty();

        var remove = Plan("{}", Existing(server), new CollectionPolicy { ValidatorPrune = "remove" });
        var removed = remove.Changes.ShouldHaveSingleItem();
        removed.Info.Action.ShouldBe("remove-validator");
        removed.Commands.ShouldBe([BsonDocument.Parse("""{ "collMod": "events", "validator": {} }""")]);
    }

    [Fact]
    public void Switching_to_the_constraint_level_prepares_it_first()
    {
        var plan = Plan("{}", Existing("{}"), validator: new ValidatorDeclaration { Level = "constraint" }, wireVersion: 29);

        plan.Changes.ShouldHaveSingleItem().Commands.ShouldBe([
            BsonDocument.Parse("""{ "collMod": "events", "prepareConstraintValidationLevel": true }"""),
            BsonDocument.Parse("""{ "collMod": "events", "validationLevel": "constraint" }"""),
        ]);
    }

    [Theory]
    [InlineData("constraint", null, 28, "events: the constraint validation level needs MongoDB 9.0 or later, and this server is older.")]
    [InlineData(null, "errorAndLog", 25, "events: the errorAndLog validation action needs MongoDB 8.1 or later, and this server is older.")]
    public void A_validation_level_or_action_the_server_is_too_old_for_is_a_problem(string? level, string? action, int wireVersion, string problem)
    {
        var plan = Plan("{}", Existing("{}"), validator: new ValidatorDeclaration { Level = level, Action = action }, wireVersion: wireVersion);

        plan.Problems.ShouldBe([problem]);
        plan.Changes.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{ "clusteredIndex": { "key": { "_id": 1 }, "unique": true } }""", 15, "events: a clustered collection needs MongoDB 5.3 or later, and this server is older.")]
    [InlineData("""{ "changeStreamPreAndPostImages": { "enabled": true } }""", 16, "events: changeStreamPreAndPostImages needs MongoDB 6.0 or later, and this server is older.")]
    [InlineData("""{ "timeseries": { "timeField": "at", "bucketMaxSpanSeconds": 60, "bucketRoundingSeconds": 60 } }""", 19, "events: custom time series bucketing needs MongoDB 6.3 or later, and this server is older.")]
    public void An_option_the_server_is_too_old_for_is_a_problem_before_anything_is_created(string options, int wireVersion, string problem)
    {
        var plan = Plan(options, existing: null, wireVersion: wireVersion);

        plan.Problems.ShouldBe([problem]);
        plan.Changes.ShouldBeEmpty();
    }

    [Fact]
    public void A_view_with_the_name_is_a_problem()
    {
        var plan = Plan("{}", new BsonDocument { { "name", "events" }, { "type", "view" }, { "options", new BsonDocument("viewOn", "raw") } });

        plan.IsView.ShouldBeTrue();
        plan.Problems.ShouldBe(["events: a view with this name exists, so it can't be managed as a collection. Drop the view or rename the definition."]);
    }

    private static ValidatorDeclaration NameRequired => new() { Validator = BsonDocument.Parse("""{ "$jsonSchema": { "required": ["name"] } }""") };

    private static CollectionOptionsPlan Plan(
        string options,
        BsonDocument? existing,
        CollectionPolicy? policy = null,
        int wireVersion = MongoDb80,
        ValidatorDeclaration? validator = null,
        UnchangeableOption? onUnchangeable = null,
        ConvertToCapped? onConvertToCapped = null,
        BsonDocument? filledCollation = null,
        InvalidDocumentCount? invalid = null) =>
        CollectionOptionsPlanner.Plan(
            new CollectionDeclaration
            {
                Name = "events",
                Indexes = [],
                Options = BsonDocument.Parse(options),
                Validator = validator,
                OnUnchangeable = onUnchangeable,
                OnConvertToCapped = onConvertToCapped,
            },
            existing,
            policy ?? new CollectionPolicy(),
            wireVersion,
            filledCollation,
            invalid);

    private static BsonDocument Existing(string options) =>
        new() { { "name", "events" }, { "type", "collection" }, { "options", BsonDocument.Parse(options) } };

    private static BsonDocument TimeSeries(string options) =>
        new() { { "name", "events" }, { "type", "timeseries" }, { "options", BsonDocument.Parse(options) } };
}
