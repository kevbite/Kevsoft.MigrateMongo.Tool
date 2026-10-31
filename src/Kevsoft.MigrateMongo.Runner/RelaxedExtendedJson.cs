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

    /// <summary>Relaxes every Extended JSON value in the event (results, statements, index keys, status, history and show).</summary>
    public static void Relax(RunnerEvent runnerEvent)
    {
        Relax(runnerEvent.Output);
        Relax(runnerEvent.Changes);
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

        foreach (var collection in (result.Collections ?? []).Append(result.ShowCollection).OfType<CollectionInfo>())
        {
            Relax(collection.Changes);
            Relax(collection.Output);
            foreach (var index in collection.Indexes ?? [])
            {
                index.Keys = index.Keys is { } keys ? ToRelaxed(keys) : null;
                index.Options = index.Options is { } options ? ToRelaxed(options) : null;
            }

            foreach (var record in collection.History ?? [])
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

    private static void Relax(List<IndexChangeInfo>? changes)
    {
        foreach (var change in changes ?? [])
        {
            change.Keys = change.Keys is { } keys ? ToRelaxed(keys) : null;
        }
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
