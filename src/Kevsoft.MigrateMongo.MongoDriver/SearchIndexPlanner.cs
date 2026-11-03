using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.Protocol;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace Kevsoft.MigrateMongo.MongoDriver;

/// <summary>
/// Works out what to change to bring a collection's Atlas Search and Vector Search indexes in line with its declaration. No
/// I/O: it compares the rendered declaration with what <c>$listSearchIndexes</c> returned.
/// </summary>
/// <remarks>
/// A search index is identified by name (unique per collection). Its definition is changed online with
/// <c>updateSearchIndex</c>, so there's no drop-and-rebuild gap; only a change of <c>type</c> needs a drop and create, since
/// <c>updateSearchIndex</c> can't change it. Search indexes are only managed when at least one is declared, so a collection
/// with none declared never has its search indexes listed or pruned.
/// </remarks>
internal static class SearchIndexPlanner
{
    private static readonly JsonWriterSettings Canonical = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };

    public static SearchIndexPlan Plan(CollectionDeclaration declaration, IReadOnlyList<BsonDocument> existingIndexes, CollectionPolicy policy)
    {
        var name = declaration.Name;
        var plan = new SearchIndexPlan { Declaration = Hash(declaration) };

        var duplicates = declaration.SearchIndexes
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
        foreach (var duplicate in duplicates)
        {
            plan.Problems.Add($"{name}: more than one declared search index is named {duplicate}.");
        }

        var existing = existingIndexes.Select(ServerIndex.From).ToList();
        var ignored = declaration.SearchIgnored.Select(Glob).ToList();
        bool IsIgnored(string index) => ignored.Any(r => r.IsMatch(index));

        var byName = existing
            .GroupBy(e => e.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var matched = new HashSet<string>(StringComparer.Ordinal);

        var changes = new List<SearchPlannedChange>();
        foreach (var declared in declaration.SearchIndexes)
        {
            if (!byName.TryGetValue(declared.Name, out var server))
            {
                changes.Add(Create(name, declared));
                continue;
            }

            matched.Add(server.Name);
            if (!string.Equals(server.Type, declared.Type, StringComparison.Ordinal))
            {
                changes.Add(Recreate(name, declared, server));
            }
            else if (!SameDefinition(server.Definition, declared.Definition))
            {
                changes.Add(Update(name, declared, server));
            }
        }

        var prune = declaration.SearchPrune switch
        {
            SearchIndexPrune.Drop => "drop",
            SearchIndexPrune.Report => "report",
            _ => policy.SearchPrune,
        };
        var drops = new List<SearchPlannedChange>();
        var keeps = new List<SearchPlannedChange>();
        foreach (var server in existing.Where(e => !matched.Contains(e.Name) && !IsIgnored(e.Name)))
        {
            if (prune == "drop")
            {
                drops.Add(Drop(name, server));
            }
            else
            {
                keeps.Add(new SearchPlannedChange { Info = Info(IndexActions.Keep, server.Name, "not declared (prune: report)") });
            }
        }

        plan.Changes.AddRange(changes);
        plan.Changes.AddRange(drops);
        plan.Changes.AddRange(keeps);
        plan.Indexes.AddRange(existing.Select(e => Listing(e, declaration, matched, IsIgnored(e.Name))));
        return plan;
    }

    private static SearchPlannedChange Create(string collection, SearchIndexDeclaration declared) => new()
    {
        Info = Info(IndexActions.CreateSearch, declared.Name, declared.Type),
        Commands = [CreateCommand(collection, declared)],
        WaitForName = declared.Name,
    };

    private static SearchPlannedChange Update(string collection, SearchIndexDeclaration declared, ServerIndex server) => new()
    {
        Info = Info(IndexActions.UpdateSearch, server.Name, "definition changed"),
        Commands =
        [
            new BsonDocument
            {
                { "updateSearchIndex", collection },
                { "name", server.Name },
                { "definition", declared.Definition },
            },
        ],
        WaitForName = server.Name,
    };

    private static SearchPlannedChange Recreate(string collection, SearchIndexDeclaration declared, ServerIndex server) => new()
    {
        Info = Info(IndexActions.UpdateSearch, server.Name, $"type {server.Type} → {declared.Type} (recreated)"),
        Commands =
        [
            new BsonDocument { { "dropSearchIndex", collection }, { "name", server.Name } },
            CreateCommand(collection, declared),
        ],
        WaitForName = declared.Name,
    };

    private static SearchPlannedChange Drop(string collection, ServerIndex server) => new()
    {
        Info = Info(IndexActions.DropSearch, server.Name, "not declared"),
        Commands = [new BsonDocument { { "dropSearchIndex", collection }, { "name", server.Name } }],
    };

    private static BsonDocument CreateCommand(string collection, SearchIndexDeclaration declared) => new()
    {
        { "createSearchIndexes", collection },
        {
            "indexes", new BsonArray
            {
                new BsonDocument
                {
                    { "name", declared.Name },
                    { "type", declared.Type },
                    { "definition", declared.Definition },
                },
            }
        },
    };

    private static IndexInfo Listing(ServerIndex server, CollectionDeclaration declaration, IReadOnlySet<string> matched, bool ignored)
    {
        var status = declaration.SearchIndexes.Any(d => string.Equals(d.Name, server.Name, StringComparison.Ordinal))
            ? "declared"
            : ignored ? "ignored" : "undeclared";
        return new IndexInfo
        {
            Name = server.Name,
            Options = MigrationOutputMapper.ToJson(new BsonDocument("type", server.Type)),
            Status = status,
        };
    }

    private static IndexChangeInfo Info(string action, string index, string? detail) => new()
    {
        Action = action,
        Index = index,
        Keys = null,
        Detail = string.IsNullOrEmpty(detail) ? null : detail,
    };

    private static bool SameDefinition(BsonDocument? server, BsonDocument declared) =>
        server is not null && Satisfies(declared, server);

    /// <summary>
    /// Whether the server's <c>latestDefinition</c> already matches everything the declaration asks for. The server fills a
    /// definition with defaults it didn't ask for (an empty <c>fields</c> for a dynamic mapping, per-field <c>store</c>,
    /// <c>norms</c> and <c>indexOptions</c> for a static one), so a declared value is in line when the server has the same
    /// field set to the same value; the extra defaults the server adds aren't a change. Object key order doesn't matter;
    /// arrays are compared in order.
    /// </summary>
    private static bool Satisfies(BsonValue declared, BsonValue server) => declared switch
    {
        BsonDocument document => server is BsonDocument serverDocument &&
            document.All(element => serverDocument.TryGetValue(element.Name, out var value) && Satisfies(element.Value, value)),
        BsonArray array => server is BsonArray serverArray && array.Count == serverArray.Count &&
            array.Zip(serverArray, Satisfies).All(matched => matched),
        _ => declared.Equals(server),
    };

    /// <summary>
    /// A definition with every object's keys in a fixed order, so a declaration that sets the same fields in a different
    /// order than the server returns them isn't seen as a change. Array order is kept.
    /// </summary>
    private static BsonValue Canonicalize(BsonValue value) => value switch
    {
        BsonDocument document => new BsonDocument(document
            .OrderBy(element => element.Name, StringComparer.Ordinal)
            .Select(element => new BsonElement(element.Name, Canonicalize(element.Value)))),
        BsonArray array => new BsonArray(array.Select(Canonicalize)),
        _ => value,
    };

    private static string Hash(CollectionDeclaration declaration)
    {
        var indexes = declaration.SearchIndexes
            .Select(s => new BsonDocument
            {
                { "name", s.Name },
                { "type", s.Type },
                { "definition", Canonicalize(s.Definition) },
            }.ToJson(Canonical))
            .Order(StringComparer.Ordinal);
        var canonical = new BsonDocument
        {
            { "searchIndexes", new BsonArray(indexes) },
            { "ignored", new BsonArray(declaration.SearchIgnored.Order(StringComparer.Ordinal)) },
            { "prune", declaration.SearchPrune?.ToString() ?? "" },
        }.ToJson(Canonical);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>One search index as <c>$listSearchIndexes</c> returned it.</summary>
    private sealed record ServerIndex(string Name, string Type, BsonDocument? Definition)
    {
        public static ServerIndex From(BsonDocument document)
        {
            var name = document.GetValue("name", "").AsString;
            var type = document.GetValue("type", BsonNull.Value) is BsonString t ? t.AsString : "search";
            var definition = document.GetValue("latestDefinition", BsonNull.Value) as BsonDocument;
            return new ServerIndex(name, type, definition);
        }
    }
}

/// <summary>A search index plan the applier can carry out.</summary>
internal sealed class SearchIndexPlan
{
    public string Declaration { get; init; } = "";

    public List<SearchPlannedChange> Changes { get; } = [];

    public List<string> Problems { get; } = [];

    public List<IndexInfo> Indexes { get; } = [];
}

internal sealed class SearchPlannedChange
{
    public required IndexChangeInfo Info { get; init; }

    /// <summary>The commands to run in order (a type change is a drop then a create).</summary>
    public List<BsonDocument> Commands { get; init; } = [];

    /// <summary>For a create or update: the index to wait for, when waiting is on.</summary>
    public string? WaitForName { get; init; }
}
