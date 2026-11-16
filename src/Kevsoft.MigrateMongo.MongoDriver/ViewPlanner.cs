using System.Security.Cryptography;
using System.Text;
using Kevsoft.MigrateMongo.Protocol;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace Kevsoft.MigrateMongo.MongoDriver;

/// <summary>
/// Works out how to bring a view in line with its <c>[ManagedView]</c> definition, from its <c>listCollections</c> entry. A
/// missing view is created; a different pipeline or source is changed in place with <c>collMod</c>; a different collation,
/// which MongoDB can't change in place, drops the view and creates it again. A collection with the view's name is a problem.
/// </summary>
internal static class ViewPlanner
{
    private static readonly JsonWriterSettings Canonical = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };

    /// <summary>
    /// Plans the view. <paramref name="existing"/> is the <c>listCollections</c> entry with its name (null if there isn't one),
    /// and <paramref name="filledCollation"/> the declared collation as the server fills it in, if it could be explained.
    /// </summary>
    public static CollectionOptionsPlan Plan(string name, ViewDeclaration view, BsonDocument? existing, BsonDocument? filledCollation = null)
    {
        var plan = new CollectionOptionsPlan
        {
            Declaration = Hash(view),
            Existing = existing?.GetValue("options", null) as BsonDocument,
            IsView = true,
            ViewDefinition = true,
        };

        if (existing is null)
        {
            plan.Changes.Add(new CollectionOptionsChange
            {
                Info = new IndexChangeInfo { Action = IndexActions.CreateView, Index = "view", Detail = $"on {view.ViewOn}, {Stages(view.Pipeline.Count)}" },
                Commands = [Create(name, view)],
            });
            return plan;
        }

        if (existing.GetValue("type", "collection") != "view")
        {
            plan.Problems.Add($"{name}: a collection with this name exists, so it can't be managed as a view. Drop or rename the collection, or rename the definition.");
            return plan;
        }

        var server = plan.Existing ?? [];
        var previous = new BsonDocument
        {
            { "viewOn", server.GetValue("viewOn", "") },
            { "pipeline", server.GetValue("pipeline", new BsonArray()) },
        };
        if (server.GetValue("collation", null) is BsonDocument existingCollation)
        {
            previous["collation"] = existingCollation;
        }

        var declaredCollation = view.Collation is { } c && c.GetValue("locale", "simple") != "simple" ? c : null;
        if (!CollectionOptionsPlanner.SameCollation(declaredCollation is null ? null : filledCollation ?? declaredCollation,
                server.GetValue("collation", null) as BsonDocument, compareAll: filledCollation is not null))
        {
            var current = server.GetValue("collation", null) as BsonDocument;
            plan.Changes.Add(new CollectionOptionsChange
            {
                Info = new IndexChangeInfo
                {
                    Action = IndexActions.RecreateView,
                    Index = "collation",
                    Detail = $"{(current is null ? "none" : IndexPlanner.Display(new BsonDocument(current.Where(e => e.Name != "version"))))} → " +
                             $"{(declaredCollation is null ? "none" : IndexPlanner.Display(declaredCollation))}",
                },
                Commands = [new BsonDocument("drop", name), Create(name, view)],
                Previous = previous,
            });
            return plan;
        }

        var differences = new List<string>();
        var viewOn = server.GetValue("viewOn", "").ToString();
        if (viewOn != view.ViewOn)
        {
            differences.Add($"viewOn {viewOn} → {view.ViewOn}");
        }

        var pipeline = server.GetValue("pipeline", new BsonArray()) as BsonArray ?? [];
        differences.AddRange(PipelineDifferences(pipeline, view.Pipeline));
        if (differences.Count > 0)
        {
            plan.Changes.Add(new CollectionOptionsChange
            {
                Info = new IndexChangeInfo
                {
                    Action = IndexActions.ModifyView,
                    Index = viewOn != view.ViewOn && differences.Count == 1 ? "viewOn" : "pipeline",
                    Detail = string.Join("; ", differences),
                },
                Commands =
                [
                    new BsonDocument
                    {
                        { "collMod", name },
                        { "viewOn", view.ViewOn },
                        { "pipeline", new BsonArray(view.Pipeline) },
                    },
                ],
                Previous = previous,
            });
        }

        return plan;
    }

    private static BsonDocument Create(string name, ViewDeclaration view)
    {
        var create = new BsonDocument
        {
            { "create", name },
            { "viewOn", view.ViewOn },
            { "pipeline", new BsonArray(view.Pipeline) },
        };
        if (view.Collation is { } collation)
        {
            create["collation"] = collation;
        }

        return create;
    }

    /// <summary>The stages that differ, e.g. <c>$match: { status: "active" } → { status: "open" }</c>, compared as stored.</summary>
    private static IEnumerable<string> PipelineDifferences(BsonArray server, IReadOnlyList<BsonDocument> declared)
    {
        for (var i = 0; i < Math.Max(server.Count, declared.Count); i++)
        {
            var have = i < server.Count ? server[i] as BsonDocument : null;
            var want = i < declared.Count ? declared[i] : null;
            if (have is not null && want is not null && IndexPlanner.Normalise(have).Equals(IndexPlanner.Normalise(want)))
            {
                continue;
            }

            yield return (have, want) switch
            {
                (null, { } added) => $"+ {Stage(added)}",
                ({ } removed, null) => $"- {Stage(removed)}",
                ({ } a, { } b) when a.ElementCount == 1 && b.ElementCount == 1 && a.GetElement(0).Name == b.GetElement(0).Name =>
                    $"{a.GetElement(0).Name}: {IndexPlanner.Display(a[0])} → {IndexPlanner.Display(b[0])}",
                _ => $"{IndexPlanner.Display(server[i])} → {IndexPlanner.Display(want!)}",
            };
        }
    }

    private static string Stage(BsonDocument stage) =>
        stage.ElementCount == 1 ? $"{stage.GetElement(0).Name}: {IndexPlanner.Display(stage[0])}" : IndexPlanner.Display(stage);

    private static string Stages(int count) => count == 1 ? "1 stage" : $"{count} stages";

    private static string Hash(ViewDeclaration view)
    {
        var canonical = new BsonDocument
        {
            { "viewOn", view.ViewOn },
            { "pipeline", new BsonArray(view.Pipeline) },
            { "collation", view.Collation ?? (BsonValue)BsonNull.Value },
        }.ToJson(Canonical);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
