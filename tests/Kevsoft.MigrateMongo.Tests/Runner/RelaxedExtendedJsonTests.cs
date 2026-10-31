using System.Text.Json;
using Kevsoft.MigrateMongo.Protocol;
using Kevsoft.MigrateMongo.Runner;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Runner;

public sealed class RelaxedExtendedJsonTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Canonical_values_are_relaxed()
    {
        var relaxed = RelaxedExtendedJson.ToRelaxed(Json("""
            {"i":{"$numberInt":"2"},"l":{"$numberLong":"9007199254740993"},"d":{"$numberDouble":"1.5"},"s":"x","list":[{"$numberInt":"4"}],
             "at":{"$date":{"$numberLong":"1790812800000"}},"old":{"$date":{"$numberLong":"-1000"}},"nan":{"$numberDouble":"NaN"},
             "dec":{"$numberDecimal":"12.50"},"oid":{"$oid":"64b7f0c2a1b2c3d4e5f60718"}}
            """));

        JsonSerializer.Serialize(relaxed).ShouldBe(
            """{"i":2,"l":9007199254740993,"d":1.5,"s":"x","list":[4],"at":{"$date":"2026-10-01T00:00:00Z"},"old":{"$date":{"$numberLong":"-1000"}},"nan":{"$numberDouble":"NaN"},"dec":{"$numberDecimal":"12.50"},"oid":{"$oid":"64b7f0c2a1b2c3d4e5f60718"}}""");
    }

    [Fact]
    public void Values_inside_query_and_update_operators_are_relaxed()
    {
        var relaxed = RelaxedExtendedJson.ToRelaxed(Json("""
            {"filter":{"age":{"$gt":{"$numberInt":"5"}},"$or":[{"at":{"$lt":{"$date":{"$numberLong":"1790812800000"}}}}]},
             "update":{"$set":{"n":{"$numberLong":"3"},"d":{"$numberDouble":"1.0"}}}}
            """));

        JsonSerializer.Serialize(relaxed).ShouldBe(
            """{"filter":{"age":{"$gt":5},"$or":[{"at":{"$lt":{"$date":"2026-10-01T00:00:00Z"}}}]},"update":{"$set":{"n":3,"d":1.0}}}""");
    }

    [Fact]
    public void Json_lines_carry_relaxed_results_and_statements()
    {
        using var writer = new StringWriter();
        var sink = new JsonLineEventSink(writer);

        sink.OnEvent(new RunnerEvent
        {
            Type = EventTypes.MigrationCompleted,
            Output = new MigrationOutputInfo
            {
                Results = Json("""{"skipped":{"$numberLong":"3"}}"""),
                Operations =
                [
                    new OperationRecord
                    {
                        Kind = "updateMany",
                        Statement = Json("""{"update":{"$set":{"tier":"gold","n":{"$numberInt":"1"}}}}"""),
                        Counts = [],
                    },
                ],
                Committed = true,
            },
        });

        using var line = JsonDocument.Parse(writer.ToString());
        var output = line.RootElement.GetProperty("output");
        JsonSerializer.Serialize(output.GetProperty("results")).ShouldBe("""{"skipped":3}""");
        JsonSerializer.Serialize(output.GetProperty("operations")[0].GetProperty("statement")).ShouldBe("""{"update":{"$set":{"tier":"gold","n":1}}}""");
    }

    [Fact]
    public void Json_lines_carry_relaxed_collection_index_keys_and_options()
    {
        using var writer = new StringWriter();
        var sink = new JsonLineEventSink(writer);

        sink.OnEvent(new RunnerEvent
        {
            Type = EventTypes.CollectionCompleted,
            Changes =
            [
                new IndexChangeInfo { Action = IndexActions.Create, Index = "age_1", Keys = Json("""{"age":{"$numberInt":"1"}}""") },
            ],
            Result = new CommandResult
            {
                ShowCollection = new CollectionInfo
                {
                    Name = "orders",
                    Indexes =
                    [
                        new IndexInfo
                        {
                            Name = "expiresAt_1",
                            Keys = Json("""{"expiresAt":{"$numberInt":"1"}}"""),
                            Options = Json("""{"expireAfterSeconds":{"$numberLong":"3600"}}"""),
                        },
                    ],
                },
            },
        });

        using var line = JsonDocument.Parse(writer.ToString());
        JsonSerializer.Serialize(line.RootElement.GetProperty("changes")[0].GetProperty("keys")).ShouldBe("""{"age":1}""");
        var index = line.RootElement.GetProperty("result").GetProperty("showCollection").GetProperty("indexes")[0];
        JsonSerializer.Serialize(index.GetProperty("keys")).ShouldBe("""{"expiresAt":1}""");
        JsonSerializer.Serialize(index.GetProperty("options")).ShouldBe("""{"expireAfterSeconds":3600}""");
    }
}
