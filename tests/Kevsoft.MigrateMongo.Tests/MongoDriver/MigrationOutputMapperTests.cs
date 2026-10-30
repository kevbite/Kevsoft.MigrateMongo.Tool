using System.Text.Json;
using Kevsoft.MigrateMongo.MongoDriver;
using Kevsoft.MigrateMongo.Protocol;
using MongoDB.Bson;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.MongoDriver;

public sealed class MigrationOutputMapperTests
{
    private static readonly DateTime At = new(2026, 10, 22, 12, 31, 3, DateTimeKind.Utc);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void Nothing_recorded_gives_no_output()
    {
        MigrationOutputMapper.From(new MigrationResults(), 65536, out var tooLarge).ShouldBeNull();
        tooLarge.ShouldBeNull();
    }

    [Fact]
    public void Results_and_notes_are_converted_to_canonical_extended_json()
    {
        var results = new MigrationResults();
        results.Set("skipped", 2);
        results.Set("total", 3L);
        results.Set("cutoff", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        results.Note("careful", NoteLevel.Warning);

        var output = MigrationOutputMapper.From(results, 65536, out var tooLarge).ShouldNotBeNull();

        tooLarge.ShouldBeNull();
        output.Results!.Value.GetRawText().ShouldBe("""{ "skipped" : { "$numberInt" : "2" }, "total" : { "$numberLong" : "3" }, "cutoff" : { "$date" : { "$numberLong" : "1790812800000" } } }""");
        output.Notes!.Single().Level.ShouldBe("warning");
        output.Notes!.Single().Message.ShouldBe("careful");
        output.Operations.ShouldBeNull();
    }

    [Fact]
    public void Results_over_the_limit_are_left_out_and_explained()
    {
        var results = new MigrationResults();
        results.Set("big", new string('x', 70_000));
        results.Set("small", 1);
        results.Note("kept");

        var output = MigrationOutputMapper.From(results, 65536, out var tooLarge).ShouldNotBeNull();

        tooLarge.ShouldBe("MM-RESULTS-TOO-LARGE: results are 68.4 KB (limit 64 KB); largest keys: big (68.4 KB), small (16 B); store large data in your own collection.");
        output.Results.ShouldBeNull();
        output.Notes!.Single().Message.ShouldBe("kept");
    }

    [Fact]
    public void Output_is_stored_as_native_bson_with_statement_fields_alongside_the_operation()
    {
        var output = new MigrationOutputInfo
        {
            Results = Json("""{ "skipped": { "$numberInt": "2" }, "archive": { "matched": { "$numberLong": "1520" } } }"""),
            Notes = [new ResultNoteInfo { At = At, Level = "warning", Message = "2 customers were left untiered." }],
            Operations =
            [
                new OperationRecord
                {
                    Kind = "updateMany",
                    Collection = "orders",
                    Label = "archive",
                    Statement = Json("""{ "filter": { "status": "complete" }, "update": { "$set": { "archived": true } } }"""),
                    Counts = new Dictionary<string, long> { ["matched"] = 1520, ["modified"] = 1518 },
                    DurationMs = 611,
                    At = At,
                },
                new OperationRecord { Kind = "createIndex", Collection = "users", StatementTruncated = true, Counts = [], Error = "MongoCommandException: boom", DurationMs = 198, At = At },
            ],
            OperationsTruncated = 37,
            Committed = false,
        };
        var document = new BsonDocument("_id", "20261022120000");

        MigrationOutputMapper.Write(document, output);

        document.ShouldBe(BsonDocument.Parse("""
            {
              "_id": "20261022120000",
              "results": { "skipped": 2, "archive": { "matched": { "$numberLong": "1520" } } },
              "notes": [ { "at": { "$date": "2026-10-22T12:31:03Z" }, "level": "warning", "message": "2 customers were left untiered." } ],
              "resultsCommitted": false,
              "operations": [
                {
                  "kind": "updateMany", "collection": "orders", "label": "archive",
                  "filter": { "status": "complete" }, "update": { "$set": { "archived": true } },
                  "counts": { "matched": { "$numberLong": "1520" }, "modified": { "$numberLong": "1518" } },
                  "durationMs": { "$numberLong": "611" }, "at": { "$date": "2026-10-22T12:31:03Z" }
                },
                {
                  "kind": "createIndex", "collection": "users", "statementTruncated": true,
                  "counts": {}, "error": "MongoCommandException: boom", "durationMs": { "$numberLong": "198" }, "at": { "$date": "2026-10-22T12:31:03Z" }
                }
              ],
              "operationsTruncated": 37
            }
            """));
    }

    [Fact]
    public void Stored_output_is_read_back_with_the_statement_regathered()
    {
        var document = BsonDocument.Parse("""
            {
              "_id": "20261022120000",
              "results": { "skipped": 2 },
              "resultsCommitted": true,
              "operations": [
                {
                  "kind": "deleteMany", "collection": "orders",
                  "filter": { "status": "void" },
                  "counts": { "deleted": 4 }, "error": "MongoWriteException: boom", "durationMs": 9, "at": { "$date": "2026-10-22T12:31:03Z" }
                }
              ],
              "notesTruncated": 2
            }
            """);

        var output = MigrationOutputMapper.Read(document).ShouldNotBeNull();

        output.Committed.ShouldBe(true);
        output.Results!.Value.GetRawText().ShouldBe("""{ "skipped" : { "$numberInt" : "2" } }""");
        output.NotesTruncated.ShouldBe(2);
        output.Notes.ShouldBeNull();
        var operation = output.Operations!.Single();
        operation.Kind.ShouldBe("deleteMany");
        operation.Collection.ShouldBe("orders");
        operation.Label.ShouldBeNull();
        operation.Statement!.Value.GetRawText().ShouldBe("""{ "filter" : { "status" : "void" } }""");
        operation.Counts.ShouldBe(new Dictionary<string, long> { ["deleted"] = 4 });
        operation.Error.ShouldBe("MongoWriteException: boom");
        operation.DurationMs.ShouldBe(9);
        operation.At.ShouldBe(At);
    }

    [Fact]
    public void An_unknown_commit_outcome_is_stored_as_null()
    {
        var document = new BsonDocument("_id", "20261022120000");

        MigrationOutputMapper.Write(document, new MigrationOutputInfo { Results = Json("""{ "n": 1 }"""), Committed = null });

        document["resultsCommitted"].ShouldBe(BsonNull.Value);
        MigrationOutputMapper.Read(document).ShouldNotBeNull().Committed.ShouldBeNull();
    }

    [Fact]
    public void Documents_without_output_read_as_none()
    {
        MigrationOutputMapper.Read(new BsonDocument("_id", "x")).ShouldBeNull();
    }
}
