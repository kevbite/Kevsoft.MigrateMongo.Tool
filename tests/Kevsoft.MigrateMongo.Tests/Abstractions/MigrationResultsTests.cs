using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Abstractions;

public sealed class MigrationResultsTests
{
    private sealed record Tier(string Name, long Customers, decimal MinSpend);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void Values_are_stored_as_bson_in_the_order_they_were_first_set()
    {
        var results = new MigrationResults();
        var id = ObjectId.Parse("6523f0a1b2c3d4e5f6a7b8c9");

        results.Set("skipped", 3);
        results.Set("total", 5_000_000_000L);
        results.Set("ratio", 0.5);
        results.Set("price", 12.50m);
        results.Set("dryRun", false);
        results.Set("source", "billing-export");
        results.Set("cutoff", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        results.Set("runKey", Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"));
        results.Set("lastId", id);
        results.Set("skipped", 4);

        results.ToBsonDocument().ShouldBe(new BsonDocument
        {
            { "skipped", new BsonInt32(4) },
            { "total", new BsonInt64(5_000_000_000L) },
            { "ratio", new BsonDouble(0.5) },
            { "price", new BsonDecimal128(Decimal128.Parse("12.50")) },
            { "dryRun", BsonBoolean.False },
            { "source", "billing-export" },
            { "cutoff", new BsonDateTime(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)) },
            { "runKey", new BsonBinaryData(Convert.FromHexString("0f8fad5bd9cb469fa16570867728950e"), BsonBinarySubType.UuidStandard) },
            { "lastId", new BsonObjectId(id) },
        });
    }

    [Fact]
    public void Guids_and_dates_in_lists_are_stored_like_single_values()
    {
        var results = new MigrationResults();
        var id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var uuid = new BsonBinaryData(Convert.FromHexString("0f8fad5bd9cb469fa16570867728950e"), BsonBinarySubType.UuidStandard);

        results.Set("ids", new List<Guid> { id });
        results.Set("idArray", new[] { id });
        results.Set("nested", new List<Guid[]> { new[] { id } });
        results.Set("dates", new List<DateTime> { new(2026, 10, 1, 9, 0, 0, DateTimeKind.Unspecified) });
        results.Set("byTier", new Dictionary<string, int> { ["gold"] = 1 });
        results.Set("bytes", new byte[] { 1, 2 });

        results.ToBsonDocument().ShouldBe(new BsonDocument
        {
            { "ids", new BsonArray { uuid } },
            { "idArray", new BsonArray { uuid } },
            { "nested", new BsonArray { new BsonArray { uuid } } },
            { "dates", new BsonArray { new BsonDateTime(new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc)) } },
            { "byTier", new BsonDocument("gold", 1) },
            { "bytes", new BsonBinaryData([1, 2]) },
        });
    }

    [Fact]
    public void Objects_and_lists_are_serialised_like_documents_in_a_collection()
    {
        var results = new MigrationResults();

        results.Set("tiers", new List<Tier> { new("gold", 310, 1000m), new("silver", 1208, 250m) });
        results.Set("thresholds", new { gold = 1000, silver = 250 });
        results.Set("ids", new[] { 1, 2, 3 }.Where(i => i > 1));

        results.ToBsonDocument().ShouldBe(BsonDocument.Parse("""
            {
              "tiers": [
                { "Name": "gold", "Customers": { "$numberLong": "310" }, "MinSpend": { "$numberDecimal": "1000" } },
                { "Name": "silver", "Customers": { "$numberLong": "1208" }, "MinSpend": { "$numberDecimal": "250" } }
              ],
              "thresholds": { "gold": 1000, "silver": 250 },
              "ids": [2, 3]
            }
            """));
    }

    [Fact]
    public void Stored_values_are_copies()
    {
        var results = new MigrationResults();
        var document = new BsonDocument("a", 1);

        results.Set("doc", document);
        document["a"] = 2;
        results["doc"]!.AsBsonDocument["a"] = 3;

        results["doc"].ShouldBe(new BsonDocument("a", 1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has.dot")]
    [InlineData("$set")]
    [InlineData("with space")]
    [InlineData("a12345678901234567890123456789012345678901234567890123456789012345")]
    public void Invalid_keys_are_rejected(string key)
    {
        var results = new MigrationResults();

        Should.Throw<ArgumentException>(() => results.Set(key, 1)).Message.ShouldContain($"Result key '{key}' is invalid");
    }

    [Fact]
    public void Field_names_that_cant_be_queried_are_rejected()
    {
        var results = new MigrationResults();

        Should.Throw<ArgumentException>(() => results.Set("doc", new BsonDocument("$bad", 1))).Message.ShouldContain("'$bad'");
        Should.Throw<ArgumentException>(() => results.Set("doc", new BsonDocument("a", new BsonDocument("b.c", 1)))).Message.ShouldContain("Result 'doc.a'");
    }

    [Fact]
    public void Values_nested_more_than_ten_levels_are_rejected()
    {
        var results = new MigrationResults();
        BsonValue nineDeep = 1;
        for (var i = 0; i < 9; i++)
        {
            nineDeep = new BsonDocument("n", nineDeep);
        }

        results.Set("ok", new BsonDocument("n", nineDeep));
        Should.Throw<ArgumentException>(() => results.Set("deep", new BsonArray { new BsonDocument("n", nineDeep) }))
            .Message.ShouldContain("more than 10 levels");
    }

    [Fact]
    public void Increment_adds_to_whole_numbers_and_returns_the_total()
    {
        var results = new MigrationResults();
        results.Set("existing", 2);

        results.Increment("counter").ShouldBe(1);
        results.Increment("counter", 4).ShouldBe(5);
        results.Increment("existing", 3).ShouldBe(5);
        results.Set("text", "x");

        results.ToBsonDocument().ShouldBe(new BsonDocument { { "existing", 5L }, { "counter", 5L }, { "text", "x" } });
        Should.Throw<InvalidOperationException>(() => results.Increment("text")).Message.ShouldBe("Result 'text' is a String, so it can't be incremented.");
    }

    [Fact]
    public void Remove_deletes_a_value()
    {
        var results = new MigrationResults();
        results.Set("a", 1);

        results.Remove("a").ShouldBeTrue();
        results.Remove("a").ShouldBeFalse();
        results.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Notes_are_time_stamped_and_limited_to_one_hundred()
    {
        var results = new MigrationResults(time: new FixedTime(new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero)));

        results.Note("first");
        results.Note("careful", NoteLevel.Warning);
        for (var i = 0; i < 103; i++)
        {
            results.Note($"n{i}");
        }

        results.Notes.Count.ShouldBe(100);
        results.Notes[0].ShouldBe(new ResultNote(new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc), NoteLevel.Info, "first"));
        results.Notes[1].Level.ShouldBe(NoteLevel.Warning);
        results.NotesTruncated.ShouldBe(5);
    }

    [Fact]
    public void Long_notes_are_cut_to_1024_characters()
    {
        var results = new MigrationResults();

        results.Note(new string('x', 2000));

        results.Notes[0].Message.ShouldBe(new string('x', 1023) + "…");
    }

    [Fact]
    public void Tracked_write_results_add_their_counts_under_the_label()
    {
        var results = new MigrationResults();

        results.Track(new UpdateResult.Acknowledged(10, 8, null), "archive");
        results.Track(new UpdateResult.Acknowledged(5, 5, new BsonObjectId(ObjectId.GenerateNewId())), "archive");
        results.Track(new DeleteResult.Acknowledged(7), "purge");
        results.Track(DeleteResult.Unacknowledged.Instance, "ignored");

        results.ToBsonDocument().ShouldBe(BsonDocument.Parse("""
            {
              "archive": { "matched": { "$numberLong": "15" }, "modified": { "$numberLong": "13" }, "upserted": { "$numberLong": "1" } },
              "purge": { "deleted": { "$numberLong": "7" } }
            }
            """));
    }

    [Fact]
    public void Counts_cant_be_added_to_a_value_that_isnt_a_document()
    {
        var results = new MigrationResults();
        results.Set("archive", 1);

        Should.Throw<InvalidOperationException>(() => results.Track(new DeleteResult.Acknowledged(1), "archive"))
            .Message.ShouldBe("Result 'archive' is a Int32, so counts can't be added to it.");
    }
}
