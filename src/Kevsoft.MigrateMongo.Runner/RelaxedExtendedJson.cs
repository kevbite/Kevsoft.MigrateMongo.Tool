using System.Text.Json;
using Kevsoft.MigrateMongo.Protocol;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace Kevsoft.MigrateMongo.Runner;

/// <summary>
/// Converts the canonical Extended JSON in runner events to relaxed Extended JSON (plain numbers, ISO dates from 1970
/// on) with the driver's own writer.
/// </summary>
internal static class RelaxedExtendedJson
{
    private static readonly JsonWriterSettings Relaxed = new() { OutputMode = JsonOutputMode.RelaxedExtendedJson };

    /// <summary>Relaxes every Extended JSON value in the event (results, statements, status, history and show).</summary>
    public static void Relax(RunnerEvent runnerEvent)
    {
        Relax(runnerEvent.Output);
        if (runnerEvent.Result is not { } result)
        {
            return;
        }

        foreach (var migration in result.Migrations ?? [])
        {
            Relax(migration.Output);
        }

        foreach (var record in result.History ?? [])
        {
            Relax(record.Output);
        }

        if (result.Show is { } show)
        {
            Relax(show.Migration.Output);
            foreach (var record in show.History)
            {
                Relax(record.Output);
            }
        }
    }

    /// <summary>A canonical Extended JSON document as relaxed Extended JSON.</summary>
    public static JsonElement ToRelaxed(JsonElement document)
    {
        using var relaxed = JsonDocument.Parse(BsonDocument.Parse(document.GetRawText()).ToJson(Relaxed));
        return relaxed.RootElement.Clone();
    }

    private static void Relax(MigrationOutputInfo? output)
    {
        if (output is null)
        {
            return;
        }

        if (output.Results is { } results)
        {
            output.Results = ToRelaxed(results);
        }

        foreach (var operation in output.Operations ?? [])
        {
            if (operation.Statement is { } statement)
            {
                operation.Statement = ToRelaxed(statement);
            }
        }
    }
}
