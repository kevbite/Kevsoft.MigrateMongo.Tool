using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests.Collections;

/// <summary>
/// Every kind of index and every index option, declared, created on a real server, then compared with what the server
/// lists: a second run must find nothing to change.
/// </summary>
public sealed class IndexOptionTests(IntegrationEnvironment environment)
{
    [Fact]
    public Task Ascending_single_field() =>
        RoundTripAsync(i => i.Add(k => k.Ascending("status")), "status_1", index => index["key"].ShouldBe(new BsonDocument("status", 1)));

    [Fact]
    public Task Descending_single_field() =>
        RoundTripAsync(i => i.Add(k => k.Descending("createdAt")), "createdAt_-1", index => index["key"].ShouldBe(new BsonDocument("createdAt", -1)));

    [Fact]
    public Task Compound_with_mixed_directions() =>
        RoundTripAsync(
            i => i.Add(k => k.Ascending("companyId").Descending("createdAt").Ascending("status")),
            "companyId_1_createdAt_-1_status_1",
            index => index["key"].ShouldBe(new BsonDocument { { "companyId", 1 }, { "createdAt", -1 }, { "status", 1 } }));

    [Fact]
    public Task Nested_field() =>
        RoundTripAsync(i => i.Add(k => k.Ascending("address.city")), "address.city_1", index => index["key"].ShouldBe(new BsonDocument("address.city", 1)));

    [Fact]
    public Task Explicit_name() =>
        RoundTripAsync(i => i.Add(k => k.Ascending("status"), new() { Name = "by_status" }), "by_status", _ => { });

    [Fact]
    public Task Unique() =>
        RoundTripAsync(i => i.Add(k => k.Ascending("email"), new() { Unique = true }), "email_1", index => index["unique"].ToBoolean().ShouldBeTrue());

    [Fact]
    public Task Sparse() =>
        RoundTripAsync(i => i.Add(k => k.Ascending("email"), new() { Sparse = true }), "email_1", index => index["sparse"].ToBoolean().ShouldBeTrue());

    [Fact]
    public Task Unique_and_sparse() =>
        RoundTripAsync(i => i.Add(k => k.Ascending("email"), new() { Unique = true, Sparse = true }), "email_1", index =>
        {
            index["unique"].ToBoolean().ShouldBeTrue();
            index["sparse"].ToBoolean().ShouldBeTrue();
        });

    [Fact]
    public Task Ttl() =>
        RoundTripAsync(i => i.Add(k => k.Ascending("expiresAt"), new() { ExpireAfter = TimeSpan.FromHours(1) }), "expiresAt_1",
            index => index["expireAfterSeconds"].ToInt64().ShouldBe(3600));

    [Fact]
    public Task Ttl_of_zero_expires_at_the_dates_in_the_field() =>
        RoundTripAsync(i => i.Add(k => k.Ascending("expiresAt"), new() { ExpireAfter = TimeSpan.Zero }), "expiresAt_1",
            index => index["expireAfterSeconds"].ToInt64().ShouldBe(0));

    [Fact]
    public Task Hidden() =>
        RoundTripAsync(i => i.Add(k => k.Ascending("status"), new() { Hidden = true }), "status_1", index => index["hidden"].ToBoolean().ShouldBeTrue());

    [Fact]
    public Task Partial_filter_with_equality() =>
        RoundTripAsync(
            i => i.Add(k => k.Ascending("companyId"), new() { PartialFilterExpression = Builders<BsonDocument>.Filter.Eq("status", "open") }),
            "companyId_1",
            index => index["partialFilterExpression"].ShouldBe(new BsonDocument("status", "open")));

    [Fact]
    public Task Partial_filter_with_operators() =>
        RoundTripAsync(
            i => i.Add(k => k.Ascending("email"), new()
            {
                Unique = true,
                PartialFilterExpression = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Exists("email"),
                    Builders<BsonDocument>.Filter.Gt("age", 17),
                    Builders<BsonDocument>.Filter.Type("email", BsonType.String)),
            }),
            "email_1",
            index => index["partialFilterExpression"].AsBsonDocument.Names.ShouldBe(["email", "age"]));

    [Fact]
    public Task Partial_filter_with_or_and_in() =>
        RoundTripAsync(
            i => i.Add(k => k.Ascending("companyId"), new()
            {
                PartialFilterExpression = Builders<BsonDocument>.Filter.Or(
                    Builders<BsonDocument>.Filter.In("status", ["open", "pending"]),
                    Builders<BsonDocument>.Filter.Eq("flagged", true)),
            }),
            "companyId_1",
            index => index["partialFilterExpression"].AsBsonDocument.Names.ShouldBe(["$or"]));

    [Fact]
    public Task Collation_with_strength() =>
        RoundTripAsync(
            i => i.Add(k => k.Ascending("name"), new() { Collation = new Collation("en", strength: CollationStrength.Secondary) }),
            "name_1",
            index =>
            {
                // The server fills in every other collation field.
                var collation = index["collation"].AsBsonDocument;
                collation["strength"].ToInt32().ShouldBe(2);
                collation.Names.ShouldContain("caseFirst");
            });

    [Fact]
    public Task Collation_with_several_fields() =>
        RoundTripAsync(
            i => i.Add(k => k.Ascending("code"), new()
            {
                Collation = new Collation("en_US", caseLevel: true, caseFirst: CollationCaseFirst.Upper, numericOrdering: true, alternate: CollationAlternate.Shifted),
            }),
            "code_1",
            index =>
            {
                var collation = index["collation"].AsBsonDocument;
                collation["locale"].AsString.ShouldBe("en_US");
                collation["numericOrdering"].ToBoolean().ShouldBeTrue();
                collation["caseFirst"].AsString.ShouldBe("upper");
            });

    // The fields the server fills in depend on the locale, e.g. da puts upper case first and th ignores punctuation.
    [Theory]
    [InlineData("en")]
    [InlineData("da")]
    [InlineData("fr_CA")]
    [InlineData("fr_CA@collation=search")]
    [InlineData("th")]
    [InlineData("hi")]
    [InlineData("de@collation=phonebook")]
    public Task Collation_with_only_a_locale(string locale) =>
        RoundTripAsync(
            i => i.Add(k => k.Ascending("name"), new() { Collation = new Collation(locale) }),
            "name_1",
            index => index["collation"]["locale"].AsString.ShouldBe(locale));

    [Fact]
    public Task Unique_with_a_case_insensitive_collation() =>
        RoundTripAsync(
            i => i.Add(k => k.Ascending("email"), new() { Unique = true, Collation = new Collation("en", strength: CollationStrength.Secondary) }),
            "email_1",
            index => index["unique"].ToBoolean().ShouldBeTrue());

    [Fact]
    public Task Simple_collation_is_the_same_as_none() =>
        RoundTripAsync(i => i.Add(k => k.Ascending("code"), new() { Collation = Collation.Simple }), "code_1", index => index.Contains("collation").ShouldBeFalse());

    [Fact]
    public Task Text_single_field() =>
        RoundTripAsync(i => i.Add(k => k.Text("title")), "title_text", index =>
        {
            index["key"].ShouldBe(new BsonDocument { { "_fts", "text" }, { "_ftsx", 1 } });
            index["weights"].ShouldBe(new BsonDocument("title", 1));
        });

    [Fact]
    public Task Text_compound_with_weights_language_and_override() =>
        RoundTripAsync(
            i => i.Add(k => k.Text("title").Text("body"), new()
            {
                Weights = new BsonDocument("title", 10),
                DefaultLanguage = "french",
                LanguageOverride = "lang",
            }),
            "title_text_body_text",
            index =>
            {
                index["weights"].ShouldBe(new BsonDocument { { "body", 1 }, { "title", 10 } });
                index["default_language"].AsString.ShouldBe("french");
                index["language_override"].AsString.ShouldBe("lang");
            });

    [Fact]
    public Task Text_with_a_prefix_field() =>
        RoundTripAsync(i => i.Add(k => k.Ascending("companyId").Text("title")), "companyId_1_title_text",
            index => index["key"].ShouldBe(new BsonDocument { { "companyId", 1 }, { "_fts", "text" }, { "_ftsx", 1 } }));

    [Fact]
    public Task Text_on_every_string_field() =>
        RoundTripAsync(i => i.Add(k => k.Text("$**")), "$**_text", index => index["weights"].ShouldBe(new BsonDocument("$**", 1)));

    [Fact]
    public Task Text_with_explicit_versions() =>
        RoundTripAsync(i => i.Add(k => k.Text("title"), new() { TextIndexVersion = 3 }), "title_text", index => index["textIndexVersion"].ToInt32().ShouldBe(3));

    [Fact]
    public Task Wildcard_on_every_field() =>
        RoundTripAsync(i => i.Add(k => k.Wildcard()), "$**_1", index => index["key"].ShouldBe(new BsonDocument("$**", 1)));

    [Fact]
    public Task Wildcard_on_a_path() =>
        RoundTripAsync(i => i.Add(k => k.Wildcard("attributes")), "attributes.$**_1", index => index["key"].ShouldBe(new BsonDocument("attributes.$**", 1)));

    [Fact]
    public Task Wildcard_with_a_projection() =>
        RoundTripAsync(
            i => i.Add(k => k.Wildcard(), new() { WildcardProjection = Builders<BsonDocument>.Projection.Include("attributes").Include("tags") }),
            "$**_1",
            index => index["wildcardProjection"].AsBsonDocument.Names.ShouldBe(["attributes", "tags"], ignoreOrder: true));

    [Fact]
    public Task Wildcard_with_an_exclusion_projection() =>
        RoundTripAsync(
            i => i.Add(k => k.Wildcard(), new() { WildcardProjection = Builders<BsonDocument>.Projection.Exclude("secret") }),
            "$**_1",
            index => index["wildcardProjection"].AsBsonDocument.Names.ShouldBe(["secret"]));

    [Fact]
    public Task Geo_2dsphere() =>
        RoundTripAsync(i => i.Add(k => k.Geo2DSphere("location")), "location_2dsphere", index => index["key"].ShouldBe(new BsonDocument("location", "2dsphere")));

    [Fact]
    public Task Geo_2dsphere_compound_with_an_explicit_version() =>
        RoundTripAsync(
            i => i.Add(k => k.Geo2DSphere("location").Ascending("category"), new() { SphereIndexVersion = 3 }),
            "location_2dsphere_category_1",
            index => index["2dsphereIndexVersion"].ToInt32().ShouldBe(3));

    [Fact]
    public Task Geo_2d_with_bits_min_and_max() =>
        RoundTripAsync(
            i => i.Add(k => k.Geo2D("point"), new() { Bits = 20, Min = -500, Max = 500 }),
            "point_2d",
            index =>
            {
                index["bits"].ToInt32().ShouldBe(20);
                index["min"].ToDouble().ShouldBe(-500);
                index["max"].ToDouble().ShouldBe(500);
            });

    [Fact]
    public Task Hashed() =>
        RoundTripAsync(i => i.Add(k => k.Hashed("userId")), "userId_hashed", index => index["key"].ShouldBe(new BsonDocument("userId", "hashed")));

    [Fact]
    public Task Compound_hashed() =>
        RoundTripAsync(i => i.Add(k => k.Hashed("userId").Ascending("createdAt")), "userId_hashed_createdAt_1",
            index => index["key"].ShouldBe(new BsonDocument { { "userId", "hashed" }, { "createdAt", 1 } }));

    [Fact]
    public Task Storage_engine_options() =>
        RoundTripAsync(
            i => i.Add(k => k.Ascending("status"), new() { StorageEngine = new BsonDocument("wiredTiger", new BsonDocument("configString", "block_compressor=zlib")) }),
            "status_1",
            index => index["storageEngine"]["wiredTiger"]["configString"].AsString.ShouldBe("block_compressor=zlib"));

    [Fact]
    public async Task Typed_keys_and_partial_filter_use_the_class_map()
    {
        await using var mongo = new CollectionHarness(environment);
        var collection = CollectionHarness.Define<Account>(c => c.Indexes
            .Add(k => k.Ascending(a => a.Email), new CreateIndexOptions<Account> { Unique = true })
            .Add(k => k.Ascending(a => a.CompanyId).Descending(a => a.CreatedAt), new CreateIndexOptions<Account>
            {
                PartialFilterExpression = Builders<Account>.Filter.Eq(a => a.Status, AccountStatus.Active),
            }));

        await mongo.ApplyAsync(collection);

        var indexes = mongo.Indexes();
        indexes.Keys.ShouldBe(["_id_", "email_1", "companyId_1_createdAt_-1"], ignoreOrder: true);
        indexes["companyId_1_createdAt_-1"]["partialFilterExpression"].ShouldBe(new BsonDocument("status", "Active"));
    }

    [Fact]
    public async Task Several_kinds_on_one_collection()
    {
        await using var mongo = new CollectionHarness(environment);

        await mongo.ApplyAsync(CollectionHarness.Define(i => i
            .Add(k => k.Ascending("companyId").Descending("createdAt"))
            .Add(k => k.Ascending("email"), new() { Unique = true, Collation = new Collation("en", strength: CollationStrength.Secondary) })
            .Add(k => k.Ascending("expiresAt"), new() { ExpireAfter = TimeSpan.FromDays(1) })
            .Add(k => k.Text("title").Text("body"), new() { Weights = new BsonDocument("title", 5) })
            .Add(k => k.Geo2DSphere("location"))
            .Add(k => k.Hashed("userId"))
            .Add(k => k.Wildcard("attributes"))
            .Add(k => k.Ascending("status"), new() { Name = "open_status", PartialFilterExpression = Builders<BsonDocument>.Filter.Eq("open", true) })));

        mongo.Indexes().Count.ShouldBe(9);
    }

    [Fact]
    public async Task Declaring_the_id_index_changes_nothing()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.Collection().InsertOneAsync(new BsonDocument("_id", 1));

        var up = await mongo.UpAsync(CollectionHarness.Define(i => i.Add(k => k.Ascending("_id"))));

        up.ExitCode.ShouldBe(0, up.Message);
        mongo.Indexes().Keys.ShouldBe(["_id_"]);
    }

    [Fact]
    public async Task A_collection_that_doesnt_exist_is_created()
    {
        await using var mongo = new CollectionHarness(environment);

        await mongo.ApplyAsync(CollectionHarness.Define(i => i.Add(k => k.Ascending("status"))));

        (await mongo.Database.ListCollectionNames().ToListAsync()).ShouldContain(CollectionHarness.Things);
    }

    [Fact]
    public async Task Same_keys_with_different_collations_are_separate_indexes()
    {
        await using var mongo = new CollectionHarness(environment);

        await mongo.ApplyAsync(CollectionHarness.Define(i => i
            .Add(k => k.Ascending("name"))
            .Add(k => k.Ascending("name"), new() { Name = "name_ci", Collation = new Collation("en", strength: CollationStrength.Secondary) })));

        mongo.Indexes().Keys.ShouldBe(["_id_", "name_1", "name_ci"], ignoreOrder: true);
    }

    [Fact]
    public async Task Same_keys_with_different_partial_filters_are_separate_indexes()
    {
        await using var mongo = new CollectionHarness(environment);

        await mongo.ApplyAsync(CollectionHarness.Define(i => i
            .Add(k => k.Ascending("companyId"), new() { Name = "open_by_company", PartialFilterExpression = Builders<BsonDocument>.Filter.Eq("status", "open") })
            .Add(k => k.Ascending("companyId"), new() { Name = "closed_by_company", PartialFilterExpression = Builders<BsonDocument>.Filter.Eq("status", "closed") })));

        mongo.Indexes().Keys.ShouldBe(["_id_", "open_by_company", "closed_by_company"], ignoreOrder: true);
    }

    [Fact]
    public Task Compound_wildcard() =>
        RoundTripAsync(
            i => i.Add(
                k => k.Combine(k.Ascending("tenantId"), k.Wildcard()),
                new() { WildcardProjection = Builders<BsonDocument>.Projection.Exclude("tenantId") }),
            "tenantId_1_$**_1",
            index => index["key"].ShouldBe(new BsonDocument { { "tenantId", 1 }, { "$**", 1 } }));

    [Fact]
    public Task Partial_filter_with_dates_decimals_and_object_ids() =>
        RoundTripAsync(
            i => i.Add(k => k.Ascending("companyId"), new()
            {
                PartialFilterExpression = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Gte("createdAt", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                    Builders<BsonDocument>.Filter.Gt("amount", new BsonDecimal128(10.5m)),
                    Builders<BsonDocument>.Filter.Eq("ownerId", new ObjectId("64b7f0c2a1b2c3d4e5f60718"))),
            }),
            "companyId_1",
            index => index["partialFilterExpression"]["amount"]["$gt"].BsonType.ShouldBe(BsonType.Decimal128));

    [Fact]
    public async Task A_clustered_collections_id_index_is_left_alone()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.Database.CreateCollectionAsync(CollectionHarness.Things, new CreateCollectionOptions<BsonDocument>
        {
            ClusteredIndex = new ClusteredIndexOptions<BsonDocument>(),
        });

        await mongo.ApplyAsync(CollectionHarness.Define(i => i.Add(k => k.Ascending("status"))), o => o.IndexesPrune = "drop");

        var indexes = mongo.Indexes();
        indexes.Keys.ShouldBe(["_id_", "status_1"], ignoreOrder: true);
        indexes["_id_"]["clustered"].ToBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Secondary_indexes_on_a_time_series_collection()
    {
        await using var mongo = new CollectionHarness(environment);
        await mongo.Database.CreateCollectionAsync(CollectionHarness.Things, new CreateCollectionOptions
        {
            TimeSeriesOptions = new TimeSeriesOptions("timestamp", "metadata"),
        });

        await mongo.ApplyAsync(CollectionHarness.Define(i => i
            .Add(k => k.Ascending("metadata.sensor").Descending("timestamp"))
            .Add(k => k.Ascending("metadata.region"), new() { PartialFilterExpression = Builders<BsonDocument>.Filter.Gt("timestamp", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)) })));

        mongo.Indexes().Keys.ShouldContain("metadata.sensor_1_timestamp_-1");
        mongo.Indexes().Keys.ShouldContain("metadata.region_1");
    }

    private async Task RoundTripAsync(Action<IndexesBuilder<BsonDocument>> indexes, string expectedName, Action<BsonDocument> assert)
    {
        await using var mongo = new CollectionHarness(environment);
        var collection = CollectionHarness.Define(indexes);

        var up = await mongo.ApplyAsync(collection);

        up.Reconciled.ShouldBe([CollectionHarness.Things]);
        var created = mongo.Indexes();
        created.Keys.ShouldBe(["_id_", expectedName], ignoreOrder: true);
        assert(created[expectedName]);
        mongo.Operations().ShouldBe(["createIndex"]);
    }

    private enum AccountStatus
    {
        Active,
        Closed,
    }

    private sealed class Account
    {
        public ObjectId Id { get; set; }

        [BsonElement("email")]
        public string Email { get; set; } = "";

        [BsonElement("companyId")]
        public int CompanyId { get; set; }

        [BsonElement("createdAt")]
        public DateTime CreatedAt { get; set; }

        [BsonElement("status")]
        [BsonRepresentation(BsonType.String)]
        public AccountStatus Status { get; set; }
    }
}
