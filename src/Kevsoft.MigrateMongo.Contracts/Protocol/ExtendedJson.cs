using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Kevsoft.MigrateMongo.Protocol;

/// <summary>
/// Helpers for reading the MongoDB Extended JSON values in <see cref="MigrationOutputInfo"/>, canonical or relaxed.
/// </summary>
public static class ExtendedJson
{
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
