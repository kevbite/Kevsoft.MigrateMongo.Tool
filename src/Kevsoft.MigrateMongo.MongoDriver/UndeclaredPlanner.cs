using System.Text.RegularExpressions;
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
    /// <remarks>
    /// A namespace that a retained view reads from (directly or transitively, through <c>viewOn</c> or a
    /// <c>$lookup</c>/<c>$unionWith</c>/<c>$graphLookup</c>/<c>$facet</c> pipeline) is protected too, so pruning never drops a
    /// dependency a kept view still reads from. A view is retained not only when it's declared but also when it's ignored or
    /// one of this tool's own or <c>system.*</c> namespaces, so each of those view's dependency chains is protected as well.
    /// </remarks>
    public static List<BsonDocument> Undeclared(IEnumerable<BsonDocument> entries, IReadOnlySet<string> declared, IReadOnlyList<string> ignore, string? stateCollection)
    {
        var all = entries as IReadOnlyCollection<BsonDocument> ?? entries.ToList();
        var ignored = ignore.Select(Glob).ToList();

        // Everything kept for any reason seeds the dependency traversal, so the chain under an ignored, state or system view
        // is protected too, not just the sources of declared views.
        var retained = new HashSet<string>(declared, StringComparer.Ordinal);
        foreach (var entry in all)
        {
            if (entry.GetValue("name", "").ToString() is { Length: > 0 } name &&
                (name.StartsWith("system.", StringComparison.Ordinal) || IsState(name, stateCollection) || ignored.Any(r => r.IsMatch(name))))
            {
                retained.Add(name);
            }
        }

        var @protected = Protect(all, retained);
        return all
            .Where(e => e.GetValue("name", "").ToString() is { Length: > 0 } name && !@protected.Contains(name))
            .OrderBy(e => e["name"].AsString, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The retained namespaces plus every namespace reachable from them through view dependencies. A view that a protected
    /// name reads from is protected in turn, so an undeclared view a kept view depends on, and whatever it reads from, are
    /// all kept.
    /// </summary>
    private static HashSet<string> Protect(IReadOnlyCollection<BsonDocument> entries, IReadOnlySet<string> retained)
    {
        var views = entries
            .Where(IsView)
            .Where(e => e.GetValue("name", "").ToString() is { Length: > 0 })
            .ToDictionary(e => e["name"].AsString, StringComparer.Ordinal);
        var @protected = new HashSet<string>(retained, StringComparer.Ordinal);
        var pending = new Queue<string>(@protected);
        while (pending.Count > 0)
        {
            if (views.TryGetValue(pending.Dequeue(), out var view) && view.GetValue("options", null) is BsonDocument options)
            {
                foreach (var dependency in ViewDependencies.ReferencedBy(options))
                {
                    if (@protected.Add(dependency))
                    {
                        pending.Enqueue(dependency);
                    }
                }
            }
        }

        return @protected;
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
