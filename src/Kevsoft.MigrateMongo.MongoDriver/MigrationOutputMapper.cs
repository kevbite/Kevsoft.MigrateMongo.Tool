using System.Globalization;
using System.Text.Json;
using Kevsoft.MigrateMongo.Protocol;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace Kevsoft.MigrateMongo.MongoDriver;

/// <summary>Converts what a migration recorded between <see cref="MigrationResults"/>, the protocol and stored BSON.</summary>
internal static class MigrationOutputMapper
{
    private static readonly JsonWriterSettings Canonical = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };

    private static readonly HashSet<string> OperationFields = new(StringComparer.Ordinal)
    {
        "kind", "collection", "label", "counts", "error", "durationMs", "at", "statementTruncated",
    };

    /// <summary>
    /// What <paramref name="results"/> holds, or null when nothing was recorded. When the named values are over
    /// <paramref name="maxBytes"/>, they're left out and <paramref name="tooLarge"/> explains why.
    /// </summary>
    public static MigrationOutputInfo? From(MigrationResults results, int maxBytes, out string? tooLarge)
    {
        tooLarge = null;
        if (results.IsEmpty)
        {
            return null;
        }

        var output = new MigrationOutputInfo();
        var values = results.ToBsonDocument();
        if (values.ElementCount > 0)
        {
            var size = values.ToBson().Length;
            if (size > maxBytes)
            {
                var largest = values
                    .Select(e => (e.Name, Size: new BsonDocument(e.Name, e.Value).ToBson().Length))
                    .OrderByDescending(e => e.Size)
                    .Take(3)
                    .Select(e => $"{e.Name} ({Kb(e.Size)})");
                tooLarge = $"MM-RESULTS-TOO-LARGE: results are {Kb(size)} (limit {Kb(maxBytes)}); largest keys: {string.Join(", ", largest)}; " +
                           "store large data in your own collection.";
            }
            else
            {
                output.Results = ToJson(values);
            }
        }

        var notes = results.Notes;
        if (notes.Count > 0)
        {
            output.Notes = notes.Select(n => new ResultNoteInfo { At = n.At, Level = Level(n.Level), Message = n.Message }).ToList();
        }

        output.NotesTruncated = results.NotesTruncated > 0 ? results.NotesTruncated : null;

        var operations = results.Operations;
        if (operations.Count > 0)
        {
            output.Operations = operations.Select(o => new OperationRecord
            {
                Kind = o.Kind,
                Collection = o.Collection,
                Label = o.Label,
                Statement = o.Statement is null ? null : ToJson(o.Statement),
                StatementTruncated = o.StatementTruncated ? true : null,
                Counts = new Dictionary<string, long>(o.Counts, StringComparer.Ordinal),
                Error = o.Error,
                DurationMs = o.DurationMs,
                At = o.At,
            }).ToList();
        }

        output.OperationsTruncated = results.OperationsTruncated > 0 ? results.OperationsTruncated : null;
        return output;
    }

    /// <summary>Adds <paramref name="output"/>'s fields to a state or history document.</summary>
    public static void Write(BsonDocument document, MigrationOutputInfo? output)
    {
        if (output is null)
        {
            return;
        }

        if (output.Results is { } results)
        {
            document.Add("results", ToBson(results));
        }

        if (output.Notes is { Count: > 0 } notes)
        {
            document.Add("notes", new BsonArray(notes.Select(n => new BsonDocument
            {
                { "at", n.At },
                { "level", n.Level },
                { "message", n.Message },
            })));
        }

        if (output.NotesTruncated is { } notesTruncated)
        {
            document.Add("notesTruncated", notesTruncated);
        }

        document.Add("resultsCommitted", output.Committed is { } committed ? committed : BsonNull.Value);

        if (output.Operations is { Count: > 0 } operations)
        {
            document.Add("operations", new BsonArray(operations.Select(WriteOperation)));
        }

        if (output.OperationsTruncated is { } operationsTruncated)
        {
            document.Add("operationsTruncated", operationsTruncated);
        }
    }

    /// <summary>Reads what <see cref="Write"/> added, or null when there's nothing.</summary>
    public static MigrationOutputInfo? Read(BsonDocument document)
    {
        // resultsCommitted is always written with the output: null means the commit's outcome is unknown.
        if (!document.TryGetValue("resultsCommitted", out var committed) || !(committed.IsBoolean || committed.IsBsonNull))
        {
            return null;
        }

        return new MigrationOutputInfo
        {
            Committed = committed.IsBoolean ? committed.AsBoolean : null,
            Results = document.TryGetValue("results", out var results) && results.IsBsonDocument ? ToJson(results.AsBsonDocument) : null,
            Notes = document.TryGetValue("notes", out var notes) && notes.IsBsonArray
                ? notes.AsBsonArray.OfType<BsonDocument>().Select(n => new ResultNoteInfo
                {
                    At = n.TryGetValue("at", out var at) && at.IsValidDateTime ? at.ToUniversalTime() : default,
                    Level = n.TryGetValue("level", out var level) && level.IsString ? level.AsString : "info",
                    Message = n.TryGetValue("message", out var message) && message.IsString ? message.AsString : "",
                }).ToList()
                : null,
            NotesTruncated = Int(document, "notesTruncated"),
            Operations = document.TryGetValue("operations", out var operations) && operations.IsBsonArray
                ? operations.AsBsonArray.OfType<BsonDocument>().Select(ReadOperation).ToList()
                : null,
            OperationsTruncated = Int(document, "operationsTruncated"),
        };
    }

    private static BsonDocument WriteOperation(OperationRecord operation)
    {
        var document = new BsonDocument("kind", operation.Kind);
        if (operation.Collection is not null)
        {
            document.Add("collection", operation.Collection);
        }

        if (operation.Label is not null)
        {
            document.Add("label", operation.Label);
        }

        // The statement's fields (filter, update, options, …) sit alongside the rest so they're easy to query.
        if (operation.Statement is { } statement)
        {
            foreach (var element in ToBson(statement).Where(e => !OperationFields.Contains(e.Name)))
            {
                document.Add(element);
            }
        }

        if (operation.StatementTruncated == true)
        {
            document.Add("statementTruncated", true);
        }

        var counts = new BsonDocument();
        foreach (var (name, value) in operation.Counts ?? [])
        {
            counts.Add(name, value);
        }

        document.Add("counts", counts);
        if (operation.Error is not null)
        {
            document.Add("error", operation.Error);
        }

        document.Add("durationMs", operation.DurationMs);
        document.Add("at", operation.At);
        return document;
    }

    private static OperationRecord ReadOperation(BsonDocument d)
    {
        var statement = new BsonDocument(d.Where(e => !OperationFields.Contains(e.Name)));
        return new OperationRecord
        {
            Kind = d.TryGetValue("kind", out var kind) && kind.IsString ? kind.AsString : "",
            Collection = d.TryGetValue("collection", out var collection) && collection.IsString ? collection.AsString : null,
            Label = d.TryGetValue("label", out var label) && label.IsString ? label.AsString : null,
            Statement = statement.ElementCount > 0 ? ToJson(statement) : null,
            StatementTruncated = d.TryGetValue("statementTruncated", out var truncated) && truncated.IsBoolean && truncated.AsBoolean ? true : null,
            Counts = d.TryGetValue("counts", out var counts) && counts.IsBsonDocument
                ? counts.AsBsonDocument.Where(e => e.Value.IsNumeric).ToDictionary(e => e.Name, e => e.Value.ToInt64(), StringComparer.Ordinal)
                : [],
            Error = d.TryGetValue("error", out var error) && error.IsString ? error.AsString : null,
            DurationMs = d.TryGetValue("durationMs", out var duration) && duration.IsNumeric ? duration.ToInt64() : 0,
            At = d.TryGetValue("at", out var at) && at.IsValidDateTime ? at.ToUniversalTime() : default,
        };
    }

    private static JsonElement ToJson(BsonDocument document)
    {
        using var json = JsonDocument.Parse(document.ToJson(Canonical));
        return json.RootElement.Clone();
    }

    private static BsonDocument ToBson(JsonElement element) => BsonDocument.Parse(element.GetRawText());

    private static int? Int(BsonDocument document, string name) =>
        document.TryGetValue(name, out var value) && value.IsNumeric ? value.ToInt32() : null;

    private static string Level(NoteLevel level) => level == NoteLevel.Warning ? "warning" : "info";

    private static string Kb(int bytes) => bytes < 1024
        ? bytes.ToString(CultureInfo.InvariantCulture) + " B"
        : (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
}
