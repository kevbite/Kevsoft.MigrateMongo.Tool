using System.Text.RegularExpressions;
using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.Protocol;
using MongoDB.Bson;

namespace Kevsoft.MigrateMongo.MongoDriver;

/// <summary>
/// Works out what happens to the collections and views no definition declares (<c>collections.prune</c>). With <c>report</c>
/// each is kept and listed; with <c>drop</c> views are dropped, and collections only when they're empty. This tool's own state
/// collections, <c>system.*</c> and names matching <c>collections.ignore</c> are never listed.
/// </summary>
internal static class UndeclaredPlanner
{
    /// <summary>The <c>listCollections</c> entries that aren't declared, ignored or this tool's own, in name order.</summary>
    public static List<BsonDocument> Undeclared(IEnumerable<BsonDocument> entries, UndeclaredPolicy policy, string? stateCollection)
    {
        var ignore = policy.Ignore.Select(Glob).ToList();
        return entries
            .Where(e => e.GetValue("name", "").ToString() is { Length: > 0 } name &&
                        !policy.Declared.Contains(name) &&
                        !name.StartsWith("system.", StringComparison.Ordinal) &&
                        !IsState(name, stateCollection) &&
                        !ignore.Any(r => r.IsMatch(name)))
            .OrderBy(e => e["name"].AsString, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// What happens to each undeclared entry. <paramref name="counts"/> has the (estimated) number of documents in each
    /// collection; a collection that isn't in it counts as not empty.
    /// </summary>
    public static List<IndexChangeInfo> Plan(IReadOnlyList<BsonDocument> undeclared, string prune, IReadOnlyDictionary<string, long> counts)
    {
        var changes = new List<IndexChangeInfo>();
        foreach (var entry in undeclared)
        {
            var name = entry["name"].AsString;
            if (IsView(entry))
            {
                var on = (entry.GetValue("options", null) as BsonDocument)?.GetValue("viewOn", null)?.ToString();
                changes.Add(prune == "drop"
                    ? new IndexChangeInfo { Action = IndexActions.DropView, Index = name, Detail = $"view on {on}, not declared" }
                    : new IndexChangeInfo { Action = IndexActions.Keep, Index = name, Detail = $"view on {on}, not declared" });
                continue;
            }

            var count = counts.TryGetValue(name, out var c) ? c : (long?)null;
            var documents = count is null ? "" : $" ({Documents(count.Value)})";
            if (prune == "drop" && count == 0)
            {
                changes.Add(new IndexChangeInfo { Action = IndexActions.DropCollection, Index = name, Detail = "not declared (empty)" });
            }
            else
            {
                changes.Add(new IndexChangeInfo
                {
                    Action = IndexActions.Keep,
                    Index = name,
                    Detail = $"not declared{documents}",
                    Warning = prune == "drop" ? $"{name} isn't declared but isn't empty{documents}, so it isn't dropped." : null,
                });
            }
        }

        return changes;
    }

    public static bool IsView(BsonDocument entry) => entry.GetValue("type", "collection") == "view";

    private static bool IsState(string name, string? stateCollection) =>
        stateCollection is { Length: > 0 } && (name == stateCollection || name.StartsWith(stateCollection + ".", StringComparison.Ordinal));

    private static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.CultureInvariant);

    private static string Documents(long count) => count == 1 ? "1 document" : $"{count} documents";
}
