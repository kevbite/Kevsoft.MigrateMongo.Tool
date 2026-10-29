using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.MongoDriver;
using MongoDB.Bson;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.MongoDriver;

public sealed class IndexPlannerTests
{
    private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
    private static readonly BsonDocument IdIndex = Server("_id_", new BsonDocument("_id", 1));

    [Fact]
    public void Missing_indexes_are_created_with_the_default_name()
    {
        var plan = Plan([Declared(new BsonDocument { { "companyId", 1 }, { "status", -1 } }), Declared(new BsonDocument("reference", 1), new BsonDocument("unique", true))], [IdIndex]);

        Summary(plan).ShouldBe(["create companyId_1_status_-1", "create reference_1 (unique)"]);
        plan.Changes[1].Create.ShouldBe(new BsonDocument { { "key", new BsonDocument("reference", 1) }, { "name", "reference_1" }, { "unique", true } });
        plan.Problems.ShouldBeEmpty();
    }

    [Fact]
    public void An_existing_index_with_the_same_keys_is_adopted_whatever_it_is_called()
    {
        var plan = Plan(
            [Declared(new BsonDocument("reference", 1), new BsonDocument { { "name", "reference_unique" }, { "unique", true } })],
            [IdIndex, Server("idx_ref", new BsonDocument("reference", 1.0), new BsonDocument("unique", true))]);

        plan.Changes.ShouldBeEmpty();
        plan.Indexes.Select(i => (i.Name, i.Status, i.DeclaredName)).ShouldBe([("_id_", "default", null), ("idx_ref", "declared", "reference_unique")]);
    }

    [Fact]
    public void Numbers_compare_by_value()
    {
        var plan = Plan(
            [Declared(new BsonDocument("at", 1), new BsonDocument("expireAfterSeconds", 3600L))],
            [IdIndex, Server("at_1", new BsonDocument("at", 1.0), new BsonDocument("expireAfterSeconds", 3600.0))]);

        plan.Changes.ShouldBeEmpty();
    }

    [Fact]
    public void Ttl_and_hidden_changes_are_made_in_place()
    {
        var plan = Plan(
            [Declared(new BsonDocument("at", 1), new BsonDocument { { "expireAfterSeconds", 60L }, { "hidden", true } })],
            [IdIndex, Server("at_1", new BsonDocument("at", 1), new BsonDocument("expireAfterSeconds", 3600))]);

        var change = plan.Changes.ShouldHaveSingleItem();
        change.Info.Action.ShouldBe("modify");
        change.Info.Detail.ShouldBe("hidden: false → true; expireAfterSeconds 3600 → 60");
        change.CollMods.ShouldBe([
            new BsonDocument { { "name", "at_1" }, { "hidden", true } },
            new BsonDocument { { "name", "at_1" }, { "expireAfterSeconds", 60L } },
        ]);
    }

    [Theory]
    [InlineData(true, "modify")]
    [InlineData(false, "rebuild")]
    public void Making_an_index_unique_uses_collMod_when_the_server_supports_it(bool canConvert, string action)
    {
        var plan = Plan(
            [Declared(new BsonDocument("email", 1), new BsonDocument("unique", true))],
            [IdIndex, Server("email_1", new BsonDocument("email", 1))],
            canConvertToUnique: canConvert);

        var change = plan.Changes.ShouldHaveSingleItem();
        change.Info.Action.ShouldBe(action);
        change.ConvertToUnique.ShouldBe(canConvert);
    }

    [Fact]
    public void Options_that_cant_change_in_place_rebuild_under_the_existing_name()
    {
        var plan = Plan(
            [Declared(new BsonDocument("email", 1), new BsonDocument("sparse", true))],
            [IdIndex, Server("by_email", new BsonDocument("email", 1))]);

        var change = plan.Changes.ShouldHaveSingleItem();
        change.Info.Action.ShouldBe("rebuild");
        change.Info.Index.ShouldBe("by_email");
        change.Info.Detail.ShouldBe("sparse: false → true (drop, then create)");
        change.DropName.ShouldBe("by_email");
        change.Create!["name"].AsString.ShouldBe("by_email");
        change.Info.Blocked.ShouldBeNull();
    }

    [Fact]
    public void Changing_the_keys_but_keeping_the_name_rebuilds_instead_of_failing_with_a_conflict()
    {
        var plan = Plan(
            [Declared(new BsonDocument { { "companyId", 1 }, { "status", 1 }, { "createdAt", -1 } }, new BsonDocument("name", "by_company"))],
            [IdIndex, Server("by_company", new BsonDocument { { "companyId", 1 }, { "status", 1 } })]);

        var change = plan.Changes.ShouldHaveSingleItem();
        change.Info.Action.ShouldBe("rebuild");
        change.Info.Detail.ShouldBe("keys { companyId: 1, status: 1 } → { companyId: 1, status: 1, createdAt: -1 } (drop, then create)");
        change.Create!["key"].ShouldBe(new BsonDocument { { "companyId", 1 }, { "status", 1 }, { "createdAt", -1 } });
    }

    [Fact]
    public void Changing_the_keys_of_an_unnamed_index_builds_the_new_one_alongside()
    {
        var plan = Plan(
            [Declared(new BsonDocument { { "companyId", 1 }, { "status", 1 } })],
            [IdIndex, Server("companyId_1", new BsonDocument("companyId", 1))]);

        Summary(plan).ShouldBe(["create companyId_1_status_1", "keep companyId_1 (not declared (prune: report))"]);
    }

    [Fact]
    public void A_rebuild_is_blocked_when_onRebuild_is_fail_unless_it_is_allowed()
    {
        var declared = Declared(new BsonDocument("email", 1), new BsonDocument("sparse", true));
        BsonDocument[] existing = [IdIndex, Server("email_1", new BsonDocument("email", 1))];

        var blocked = Plan([declared], existing, Policy(onRebuild: "fail"));
        blocked.Changes.ShouldHaveSingleItem().Info.Blocked.ShouldBe(true);
        blocked.Problems.ShouldBe([
            "users: rebuilding email_1 drops it before the new one builds, and indexes.onRebuild is fail. " +
            "Give the declared index a new name so it builds alongside the old one, or run with --allow-rebuild users.email_1.",
        ]);

        var allowed = Plan([declared], existing, Policy(onRebuild: "fail", allowRebuild: ["email_1"]));
        allowed.Problems.ShouldBeEmpty();
        allowed.Changes.ShouldHaveSingleItem().Info.Blocked.ShouldBeNull();
    }

    [Fact]
    public void A_definition_can_make_rebuilds_stricter_but_not_looser()
    {
        BsonDocument[] existing = [IdIndex, Server("email_1", new BsonDocument("email", 1))];

        var strict = Plan([Declared(new BsonDocument("email", 1), new BsonDocument("sparse", true), IndexRebuild.Fail)], existing);
        strict.Problems.ShouldHaveSingleItem().ShouldContain("its definition sets IndexRebuild.Fail");

        var loose = Plan([Declared(new BsonDocument("email", 1), new BsonDocument("sparse", true), IndexRebuild.Allow)], existing, Policy(onRebuild: "fail"));
        loose.Problems.ShouldHaveSingleItem().ShouldContain("indexes.onRebuild is fail");
    }

    [Fact]
    public void Undeclared_indexes_are_reported_hidden_or_dropped()
    {
        BsonDocument[] existing = [IdIndex, Server("status_1", new BsonDocument("status", 1)), Server("old_1", new BsonDocument("old", 1))];
        IndexDeclaration[] declared = [Declared(new BsonDocument("status", 1))];

        Summary(Plan(declared, existing)).ShouldBe(["keep old_1 (not declared (prune: report))"]);
        Summary(Plan(declared, existing, Policy(prune: "drop"))).ShouldBe(["drop old_1 (not declared)"]);

        var hide = Plan(declared, existing, Policy(prune: "hide"));
        Summary(hide).ShouldBe(["hide old_1 (not declared)"]);
        hide.Changes[0].CollMods.ShouldBe([new BsonDocument { { "name", "old_1" }, { "hidden", true } }]);
        hide.Hidden.ShouldBe(new Dictionary<string, DateTime> { ["old_1"] = Now });
    }

    [Fact]
    public void A_definitions_prune_setting_overrides_the_config()
    {
        var declaration = new CollectionDeclaration { Name = "users", Indexes = [Declared(new BsonDocument("status", 1))], Prune = IndexPrune.Drop };

        var plan = IndexPlanner.Plan(declaration, [IdIndex, Server("status_1", new BsonDocument("status", 1)), Server("old_1", new BsonDocument("old", 1))], Policy(), true);

        Summary(plan).ShouldBe(["drop old_1 (not declared)"]);
    }

    [Fact]
    public void Hidden_undeclared_indexes_are_dropped_once_they_have_been_hidden_long_enough()
    {
        BsonDocument[] existing = [IdIndex, Server("status_1", new BsonDocument("status", 1)), Server("old_1", new BsonDocument("old", 1), new BsonDocument("hidden", true))];
        IndexDeclaration[] declared = [Declared(new BsonDocument("status", 1))];

        var waiting = Plan(declared, existing, Policy(prune: "hide", hidden: new() { ["old_1"] = Now.AddDays(-3) }));
        Summary(waiting).ShouldBe(["keep old_1 (hidden; dropped after 2026-11-02 12:00)"]);
        waiting.Hidden.ShouldBe(new Dictionary<string, DateTime> { ["old_1"] = Now.AddDays(-3) });

        var due = Plan(declared, existing, Policy(prune: "hide", hidden: new() { ["old_1"] = Now.AddDays(-7) }));
        Summary(due).ShouldBe(["drop old_1 (not declared; hidden since 2026-10-22 12:00)"]);
        due.Hidden.ShouldBeEmpty();

        // Hidden by someone else: the clock starts now.
        var untracked = Plan(declared, existing, Policy(prune: "hide"));
        Summary(untracked).ShouldBe(["keep old_1 (hidden; dropped after 2026-11-05 12:00)"]);
        untracked.Hidden.ShouldBe(new Dictionary<string, DateTime> { ["old_1"] = Now });
    }

    [Fact]
    public void Declaring_a_hidden_index_again_unhides_it()
    {
        var plan = Plan(
            [Declared(new BsonDocument("old", 1))],
            [IdIndex, Server("old_1", new BsonDocument("old", 1), new BsonDocument("hidden", true))],
            Policy(prune: "hide", hidden: new() { ["old_1"] = Now.AddDays(-1) }));

        var change = plan.Changes.ShouldHaveSingleItem();
        change.Info.Detail.ShouldBe("hidden: true → false");
        change.CollMods.ShouldBe([new BsonDocument { { "name", "old_1" }, { "hidden", false } }]);
        plan.Hidden.ShouldBeEmpty();
    }

    [Fact]
    public void Under_report_hidden_indexes_are_shown_as_hidden_and_keep_the_time_they_were_hidden()
    {
        var hiddenAt = Now.AddDays(-3);
        BsonDocument[] existing =
        [
            IdIndex,
            Server("status_1", new BsonDocument("status", 1)),
            Server("old_1", new BsonDocument("old", 1), new BsonDocument("hidden", true)),
            Server("other_1", new BsonDocument("other", 1), new BsonDocument("hidden", true)),
        ];

        var plan = Plan([Declared(new BsonDocument("status", 1))], existing, Policy(prune: "report", hidden: new() { ["old_1"] = hiddenAt }));

        Summary(plan).ShouldBe([
            "keep old_1 (not declared (prune: report); hidden since 2026-10-26 12:00)",
            "keep other_1 (not declared (prune: report); hidden)",
        ]);
        plan.Hidden.ShouldBe(new Dictionary<string, DateTime> { ["old_1"] = hiddenAt });
    }

    [Fact]
    public void A_rebuild_under_a_name_another_server_index_has_is_a_problem()
    {
        var plan = Plan(
            [Declared(new BsonDocument("email", 1), new BsonDocument { { "name", "email_lookup" }, { "sparse", true } })],
            [IdIndex, Server("by_email", new BsonDocument("email", 1)), Server("email_lookup", new BsonDocument { { "email", 1 }, { "x", 1 } })],
            Policy(prune: "drop"));

        var rebuild = plan.Changes.Single(c => c.Info.Action == "rebuild");
        (rebuild.Info.Blocked, rebuild.Info.Reason).ShouldBe((true, "email_lookup is taken"));
        plan.Problems.ShouldBe([
            "users: by_email is rebuilt as email_lookup, but the server already has an index called email_lookup (not declared). " +
            "Give the declared index a different name, or drop the server's email_lookup first.",
        ]);
    }

    [Fact]
    public void Ignored_indexes_are_left_alone()
    {
        var declaration = new CollectionDeclaration { Name = "users", Indexes = [Declared(new BsonDocument("status", 1))], Ignored = ["adhoc_*"] };

        var plan = IndexPlanner.Plan(
            declaration,
            [IdIndex, Server("status_1", new BsonDocument("status", 1)), Server("adhoc_created", new BsonDocument("createdAt", -1))],
            Policy(prune: "drop"),
            true);

        plan.Changes.ShouldBeEmpty();
        plan.Indexes.Single(i => i.Name == "adhoc_created").Status.ShouldBe("ignored");
    }

    [Fact]
    public void An_ignored_index_is_never_replaced_by_a_declared_one_with_its_name()
    {
        var declaration = new CollectionDeclaration
        {
            Name = "users",
            Indexes = [Declared(new BsonDocument("status", 1)), Declared(new BsonDocument("email", 1), new BsonDocument("name", "adhoc_email"))],
            Ignored = ["adhoc_*"],
        };

        var plan = IndexPlanner.Plan(
            declaration,
            [IdIndex, Server("status_1", new BsonDocument("status", 1)), Server("adhoc_email", new BsonDocument { { "email", 1 }, { "createdAt", -1 } })],
            Policy(),
            true);

        plan.Changes.ShouldNotContain(c => c.Info.Action == "rebuild");
        plan.Problems.ShouldBe([
            "users: the declared index { email: 1 } is named adhoc_email, but the server's adhoc_email is ignored, so it isn't replaced. " +
            "Give the declared index a different name, or stop ignoring adhoc_email.",
        ]);
    }

    [Fact]
    public void An_ignored_text_index_is_never_replaced_by_a_declared_one()
    {
        var declaration = new CollectionDeclaration
        {
            Name = "users",
            Indexes = [Declared(new BsonDocument("status", 1)), Declared(new BsonDocument("title", "text"))],
            Ignored = ["search_*"],
        };

        var plan = IndexPlanner.Plan(
            declaration,
            [
                IdIndex,
                Server("status_1", new BsonDocument("status", 1)),
                Server("search_text", new BsonDocument { { "_fts", "text" }, { "_ftsx", 1 } }, new BsonDocument("weights", new BsonDocument("body", 1))),
            ],
            Policy(),
            true);

        plan.Changes.ShouldNotContain(c => c.Info.Action == "rebuild");
        plan.Problems.ShouldBe([
            "users: the declared index { title: \"text\" } matches search_text, which is ignored, so it isn't changed " +
            "(weights { body: 1 } → { title: 1 }). Declare it as it is, or stop ignoring search_text.",
        ]);
    }

    [Fact]
    public void A_second_text_index_is_not_created_beside_an_ignored_one()
    {
        var declaration = new CollectionDeclaration
        {
            Name = "users",
            Indexes = [Declared(new BsonDocument("status", 1)), Declared(new BsonDocument { { "companyId", 1 }, { "title", "text" } })],
            Ignored = ["search_*"],
        };

        var plan = IndexPlanner.Plan(
            declaration,
            [
                IdIndex,
                Server("status_1", new BsonDocument("status", 1)),
                Server("search_text", new BsonDocument { { "_fts", "text" }, { "_ftsx", 1 } }, new BsonDocument("weights", new BsonDocument("body", 1))),
            ],
            Policy(),
            true);

        plan.Problems.ShouldBe([
            "users: a collection can only have one text index, and the server's search_text is ignored, so the declared " +
            "{ companyId: 1, title: \"text\" } can't be created. Stop ignoring search_text to replace it.",
        ]);
    }

    [Fact]
    public void A_declared_index_the_same_as_an_ignored_one_is_adopted()
    {
        var declaration = new CollectionDeclaration { Name = "users", Indexes = [Declared(new BsonDocument("status", 1))], Ignored = ["adhoc_*"] };

        var plan = IndexPlanner.Plan(declaration, [IdIndex, Server("adhoc_status", new BsonDocument("status", 1))], Policy(), true);

        plan.Changes.ShouldBeEmpty();
        plan.Problems.ShouldBeEmpty();
        plan.Indexes.Single(i => i.Name == "adhoc_status").Status.ShouldBe("declared");
    }

    [Fact]
    public void Nothing_changes_when_the_declared_indexes_share_no_field_with_the_existing_ones_unless_that_is_allowed()
    {
        BsonDocument[] existing = [IdIndex, Server("CompanyId_1", new BsonDocument("CompanyId", 1)), Server("Title_text", new BsonDocument { { "_fts", "text" }, { "_ftsx", 1 } }, new BsonDocument("weights", new BsonDocument("Title", 1)))];
        IndexDeclaration[] declared = [Declared(new BsonDocument("companyId", 1)), Declared(new BsonDocument("title", "text"))];

        Plan(declared, existing).Problems.ShouldHaveSingleItem().ShouldStartWith("users: none of the declared indexes use a field that the 2 existing index(es) use");
        Plan(declared, existing, Policy(allowUnmatched: true)).Problems.ShouldBeEmpty();
    }

    [Fact]
    public void Changing_the_keys_of_a_collections_only_index_isnt_mistaken_for_unregistered_class_maps()
    {
        var plan = Plan([Declared(new BsonDocument { { "companyId", 1 }, { "status", 1 } })], [IdIndex, Server("companyId_1", new BsonDocument("companyId", 1))]);

        plan.Problems.ShouldBeEmpty();
    }

    [Fact]
    public void A_text_index_matches_the_servers_internal_form()
    {
        var plan = Plan(
            [Declared(new BsonDocument { { "title", "text" }, { "body", "text" } }, new BsonDocument("weights", new BsonDocument("title", 10)))],
            [IdIndex, Server("title_text_body_text", new BsonDocument { { "_fts", "text" }, { "_ftsx", 1 } }, new BsonDocument
            {
                { "weights", new BsonDocument { { "body", 1 }, { "title", 10 } } },
                { "default_language", "english" },
                { "language_override", "language" },
                { "textIndexVersion", 3 },
            })]);

        plan.Changes.ShouldBeEmpty();
    }

    [Fact]
    public void Changing_a_text_indexs_fields_rebuilds_the_collections_one_text_index()
    {
        var plan = Plan(
            [Declared(new BsonDocument("title", "text"))],
            [IdIndex, Server("body_text", new BsonDocument { { "_fts", "text" }, { "_ftsx", 1 } }, new BsonDocument("weights", new BsonDocument("body", 1)))]);

        var change = plan.Changes.ShouldHaveSingleItem();
        change.Info.Action.ShouldBe("rebuild");
        change.DropName.ShouldBe("body_text");
    }

    [Fact]
    public void A_declared_collation_matches_the_servers_filled_in_one()
    {
        var collation = new BsonDocument { { "locale", "en" }, { "strength", 2 } };
        var plan = Plan(
            [Declared(new BsonDocument("name", 1), new BsonDocument("collation", collation))],
            [IdIndex, Server("name_1", new BsonDocument("name", 1), new BsonDocument("collation", FilledCollation("en", ("strength", 2))))],
            filledCollations: Filled((collation, FilledCollation("en", ("strength", 2)))));

        plan.Changes.ShouldBeEmpty();
    }

    [Fact]
    public void Leaving_a_field_out_of_a_collation_rebuilds_the_index()
    {
        var collation = new BsonDocument("locale", "en");
        var plan = Plan(
            [Declared(new BsonDocument("name", 1), new BsonDocument("collation", collation))],
            [IdIndex, Server("name_1", new BsonDocument("name", 1), new BsonDocument("collation", FilledCollation("en", ("strength", 2))))],
            filledCollations: Filled((collation, FilledCollation("en"))));

        var change = plan.Changes.ShouldHaveSingleItem();
        change.Info.Action.ShouldBe("rebuild");
        change.Info.Detail.ShouldBe("collation strength 2 → 3 (drop, then create)");
    }

    [Fact]
    public void A_locales_own_defaults_are_not_a_difference()
    {
        var collation = new BsonDocument("locale", "da");
        var plan = Plan(
            [Declared(new BsonDocument("name", 1), new BsonDocument("collation", collation))],
            [IdIndex, Server("name_1", new BsonDocument("name", 1), new BsonDocument("collation", FilledCollation("da", ("caseFirst", "upper"))))],
            filledCollations: Filled((collation, FilledCollation("da", ("caseFirst", "upper")))));

        plan.Changes.ShouldBeEmpty();
    }

    [Fact]
    public void A_collation_the_server_did_not_fill_in_is_compared_by_the_fields_it_sets()
    {
        var plan = Plan(
            [Declared(new BsonDocument("name", 1), new BsonDocument("collation", new BsonDocument("locale", "en")))],
            [IdIndex, Server("name_1", new BsonDocument("name", 1), new BsonDocument("collation", FilledCollation("en", ("strength", 2))))]);

        plan.Changes.ShouldBeEmpty();
    }

    [Fact]
    public void The_filled_in_collation_is_found_in_an_explain_but_not_in_its_command()
    {
        var declared = new BsonDocument("locale", "th");
        var filled = FilledCollation("th", ("alternate", "shifted"));
        var explain = new BsonDocument
        {
            { "stages", new BsonArray { new BsonDocument("$cursor", new BsonDocument("queryPlanner", new BsonDocument("collation", filled))) } },
            { "command", new BsonDocument { { "find", "things" }, { "collation", declared } } },
        };

        IndexPlanner.ExplainedCollation(explain, declared).ShouldBe(filled);
        IndexPlanner.ExplainedCollation(new BsonDocument("command", new BsonDocument("collation", declared)), declared).ShouldBeNull();
    }

    [Fact]
    public void Indexes_on_the_same_keys_are_told_apart_by_their_partial_filter()
    {
        var plan = Plan(
            [Declared(new BsonDocument("companyId", 1), new BsonDocument { { "name", "open_by_company" }, { "partialFilterExpression", new BsonDocument("status", "open") } })],
            [IdIndex, Server("companyId_1", new BsonDocument("companyId", 1))],
            Policy(allowUnmatched: true));

        Summary(plan).ShouldBe(["create open_by_company (partialFilterExpression { status: \"open\" })", "keep companyId_1 (not declared (prune: report))"]);
    }

    [Fact]
    public void Declaring_the_id_index_changes_nothing()
    {
        Plan([Declared(new BsonDocument("_id", 1))], [IdIndex]).Changes.ShouldBeEmpty();
    }

    [Fact]
    public void Inconsistent_declarations_are_invalid()
    {
        var plan = Plan([Declared(new BsonDocument("a", 1)), Declared(new BsonDocument("a", 1), new BsonDocument("unique", true))], [IdIndex]);

        plan.Invalid.ShouldBeTrue();
        plan.Problems.ShouldBe(["users: { a: 1 } is declared more than once with the same collation and partial filter.", "users: more than one declared index is named a_1."]);
    }

    [Fact]
    public void The_declaration_hash_ignores_order_but_not_options()
    {
        var a = Declared(new BsonDocument("a", 1));
        var b = Declared(new BsonDocument("b", 1));

        Plan([a, b], [IdIndex]).Declaration.ShouldBe(Plan([b, a], [IdIndex]).Declaration);
        Plan([a, b], [IdIndex]).Declaration.ShouldNotBe(Plan([a, Declared(new BsonDocument("b", 1), new BsonDocument("unique", true))], [IdIndex]).Declaration);
        Plan([a], [IdIndex]).Declaration.ShouldStartWith("sha256:");
    }

    [Fact]
    public void Changes_are_ordered_creates_first_and_removals_last()
    {
        var plan = Plan(
            [
                Declared(new BsonDocument("email", 1), new BsonDocument("sparse", true)),
                Declared(new BsonDocument("at", 1), new BsonDocument("expireAfterSeconds", 60L)),
                Declared(new BsonDocument("status", 1)),
            ],
            [
                IdIndex,
                Server("old_1", new BsonDocument("old", 1)),
                Server("email_1", new BsonDocument("email", 1)),
                Server("at_1", new BsonDocument("at", 1), new BsonDocument("expireAfterSeconds", 3600)),
            ],
            Policy(prune: "drop"));

        plan.Changes.Select(c => c.Info.Action).ShouldBe(["create", "modify", "rebuild", "drop"]);
    }

    private static IndexPlan Plan(
        IndexDeclaration[] declared,
        BsonDocument[] existing,
        CollectionPolicy? policy = null,
        bool canConvertToUnique = true,
        Dictionary<string, BsonDocument>? filledCollations = null) =>
        IndexPlanner.Plan(new CollectionDeclaration { Name = "users", Indexes = declared }, existing, policy ?? Policy(), canConvertToUnique, filledCollations);

    private static Dictionary<string, BsonDocument> Filled(params (BsonDocument Declared, BsonDocument Filled)[] collations) =>
        collations.ToDictionary(c => IndexPlanner.CollationKey(c.Declared), c => c.Filled);

    private static CollectionPolicy Policy(
        string prune = "report",
        string onRebuild = "allow",
        string[]? allowRebuild = null,
        bool allowUnmatched = false,
        Dictionary<string, DateTime>? hidden = null) => new()
        {
            Prune = prune,
            OnRebuild = onRebuild,
            DropHiddenAfter = TimeSpan.FromDays(7),
            AllowRebuild = (allowRebuild ?? []).ToHashSet(StringComparer.Ordinal),
            AllowUnmatched = allowUnmatched,
            Hidden = hidden ?? [],
            Now = Now,
        };

    private static IndexDeclaration Declared(BsonDocument keys, BsonDocument? options = null, IndexRebuild? onRebuild = null) =>
        new() { Keys = keys, Options = options ?? [], OnRebuild = onRebuild };

    private static BsonDocument Server(string name, BsonDocument keys, BsonDocument? options = null)
    {
        var index = new BsonDocument { { "v", 2 }, { "key", keys }, { "name", name } };
        index.AddRange(options ?? []);
        return index;
    }

    /// <summary>A collation as the server lists it, with every field filled in.</summary>
    private static BsonDocument FilledCollation(string locale, params (string Name, BsonValue Value)[] fields)
    {
        var collation = new BsonDocument
        {
            { "locale", locale }, { "caseLevel", false }, { "caseFirst", "off" }, { "strength", 3 }, { "numericOrdering", false },
            { "alternate", "non-ignorable" }, { "maxVariable", "punct" }, { "normalization", false }, { "backwards", false }, { "version", "57.1" },
        };
        foreach (var (name, value) in fields)
        {
            collation[name] = value;
        }

        return collation;
    }

    private static List<string> Summary(IndexPlan plan) =>
        plan.Changes.Select(c => $"{c.Info.Action} {c.Info.Index}" + (c.Info.Action is "create" or "keep" or "drop" or "hide" && c.Info.Detail is { } detail ? $" ({detail})" : "")).ToList();
}
