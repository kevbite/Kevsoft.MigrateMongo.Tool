using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Kevsoft.MigrateMongo.Protocol;

/// <summary>Helpers for the MongoDB Extended JSON values in <see cref="MigrationOutputInfo"/>.</summary>
public static class ExtendedJson
{
    /// <summary>
    /// Converts canonical Extended JSON to relaxed Extended JSON: <c>$numberInt</c>, <c>$numberLong</c> and finite
    /// <c>$numberDouble</c> become plain numbers, and <c>$date</c> becomes an ISO-8601 string (from 1970 on).
    /// Everything else is kept as it is.
    /// </summary>
    public static JsonElement ToRelaxed(JsonElement element)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteRelaxed(writer, element);
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }

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

    private static void WriteRelaxed(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object when TryGetWrapper(element, out var name, out var value):
                switch (name)
                {
                    case "$numberInt" or "$numberLong" when value.ValueKind == JsonValueKind.String &&
                                                            long.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer):
                        writer.WriteNumberValue(integer);
                        return;
                    case "$numberDouble" when value.ValueKind == JsonValueKind.String &&
                                              double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                                              double.IsFinite(number):
                        writer.WriteRawValue(value.GetString()!.Contains('.', StringComparison.Ordinal) || value.GetString()!.Contains('E', StringComparison.OrdinalIgnoreCase)
                            ? value.GetString()!
                            : value.GetString() + ".0");
                        return;
                    case "$date" when TryGetDate(value, out var date) && date.Year >= 1970:
                        writer.WriteStartObject();
                        writer.WriteString("$date", FormatDate(date));
                        writer.WriteEndObject();
                        return;
                }

                element.WriteTo(writer);
                return;
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteRelaxed(writer, property.Value);
                }

                writer.WriteEndObject();
                return;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteRelaxed(writer, item);
                }

                writer.WriteEndArray();
                return;
            default:
                element.WriteTo(writer);
                return;
        }
    }

    /// <summary>
    /// Formats a value for people: <c>ObjectId("…")</c>, ISO dates, plain numbers and decimals, and
    /// <c>{ name: value }</c> documents. Strings are quoted when <paramref name="quoteStrings"/> is set.
    /// </summary>
    public static string Format(JsonElement element, bool quoteStrings = false)
    {
        var builder = new StringBuilder();
        Format(builder, element, quoteStrings);
        return builder.ToString();
    }

    private static void Format(StringBuilder builder, JsonElement element, bool quoteStrings)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                builder.Append(quoteStrings ? "\"" + JsonEncodedText.Encode(element.GetString()!, JavaScriptEncoder.UnsafeRelaxedJsonEscaping) + "\"" : element.GetString());
                return;
            case JsonValueKind.Object when TryGetWrapper(element, out var name, out var value) && FormatWrapper(name, value) is { } formatted:
                builder.Append(formatted);
                return;
            case JsonValueKind.Object:
                var properties = element.EnumerateObject().ToList();
                if (properties.Count == 0)
                {
                    builder.Append("{}");
                    return;
                }

                builder.Append("{ ");
                for (var i = 0; i < properties.Count; i++)
                {
                    builder.Append(i == 0 ? "" : ", ").Append(properties[i].Name).Append(": ");
                    Format(builder, properties[i].Value, quoteStrings: true);
                }

                builder.Append(" }");
                return;
            case JsonValueKind.Array:
                builder.Append('[');
                var first = true;
                foreach (var item in element.EnumerateArray())
                {
                    builder.Append(first ? "" : ", ");
                    Format(builder, item, quoteStrings: true);
                    first = false;
                }

                builder.Append(']');
                return;
            default:
                builder.Append(element.GetRawText());
                return;
        }
    }

    /// <summary>The value as a whole number when it's an integer (plain, <c>$numberInt</c> or <c>$numberLong</c>).</summary>
    public static bool TryGetInteger(JsonElement element, out long value)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetInt64(out value);
        }

        value = 0;
        return TryGetWrapper(element, out var name, out var wrapped) &&
               name is "$numberInt" or "$numberLong" &&
               wrapped.ValueKind == JsonValueKind.String &&
               long.TryParse(wrapped.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>True for objects and arrays that aren't Extended JSON wrappers for a single value.</summary>
    public static bool IsContainer(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ||
        (element.ValueKind == JsonValueKind.Object && !(TryGetWrapper(element, out var name, out var value) && FormatWrapper(name, value) is not null));

    private static string? FormatWrapper(string name, JsonElement value) => name switch
    {
        "$numberInt" or "$numberLong" or "$numberDouble" or "$numberDecimal" when value.ValueKind == JsonValueKind.String => value.GetString(),
        "$oid" when value.ValueKind == JsonValueKind.String => $"ObjectId(\"{value.GetString()}\")",
        "$date" when TryGetDate(value, out var date) => FormatDate(date),
        "$binary" when value.ValueKind == JsonValueKind.Object &&
                       value.TryGetProperty("subType", out var subType) && subType.GetString() == "04" &&
                       value.TryGetProperty("base64", out var base64) && TryGetGuid(base64.GetString(), out var guid) => $"UUID(\"{guid}\")",
        _ => null,
    };

    private static bool TryGetGuid(string? base64, out Guid guid)
    {
        guid = default;
        if (base64 is null)
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(base64);
            if (bytes.Length != 16)
            {
                return false;
            }

            guid = new Guid(bytes, bigEndian: true);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryGetWrapper(JsonElement element, out string name, out JsonElement value)
    {
        name = "";
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        using var properties = element.EnumerateObject();
        if (!properties.MoveNext() || !properties.Current.Name.StartsWith('$'))
        {
            return false;
        }

        name = properties.Current.Name;
        value = properties.Current.Value;
        return !properties.MoveNext();
    }

    private static bool TryGetDate(JsonElement value, out DateTime date)
    {
        date = default;
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out date);
            case JsonValueKind.Object when value.TryGetProperty("$numberLong", out var ms) &&
                                           long.TryParse(ms.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var milliseconds) &&
                                           milliseconds >= DateTimeOffset.MinValue.ToUnixTimeMilliseconds() &&
                                           milliseconds <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds():
                date = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime;
                return true;
            default:
                return false;
        }
    }

    private static string FormatDate(DateTime date) =>
        date.ToString(date.Millisecond == 0 ? "yyyy-MM-dd'T'HH:mm:ss'Z'" : "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
