using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.Protocol;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace Kevsoft.MigrateMongo.MongoDriver;

/// <summary>
/// Works out what to change to bring a collection's indexes in line with its declaration. No I/O: it compares the rendered
/// declaration with what <c>listIndexes</c> returned.
/// </summary>
/// <remarks>
/// An index is identified by its keys, collation and partial filter (the server allows two indexes on the same keys only if
/// those differ), so a declared index matches an existing one whatever either is called. Other options are compared once
/// matched: TTL, <c>hidden</c> and conversion to unique change in place with <c>collMod</c>; anything else means a rebuild.
/// </remarks>
internal static class IndexPlanner
{
    private const string IdIndex = "_id_";

    private static readonly JsonWriterSettings Canonical = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };

    public static IndexPlan Plan(CollectionDeclaration declaration, IReadOnlyList<BsonDocument> existingIndexes, CollectionPolicy policy, bool canConvertToUnique)
    {
        var name = declaration.Name;
        List<IndexSpec> declared;
        try
        {
            declared = declaration.Indexes.Select(FromDeclaration).ToList();
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or InvalidCastException)
        {
            return IndexPlan.InvalidPlan($"{name}: {e.Message}");
        }

        var invalid = Validate(name, declared);
        if (invalid.Count > 0)
        {
            return new IndexPlan { Invalid = true, Problems = invalid };
        }

        var plan = new IndexPlan { Declaration = Hash(declaration, declared) };
        var existing = existingIndexes.Select(FromServer).ToList();
        var candidates = existing.Where(e => e.Name != IdIndex).ToList();
        var ignored = declaration.Ignored.Select(Glob).ToList();
        bool IsIgnored(string index) => ignored.Any(r => r.IsMatch(index));

        // 1. Same identity: adopt the existing index, whatever it's called.
        var matched = new Dictionary<IndexSpec, IndexSpec>();
        var unmatched = new List<IndexSpec>();
        foreach (var index in declared)
        {
            if (IsIdIndex(index))
            {
                continue;
            }

            var match = candidates.FirstOrDefault(c => !matched.ContainsValue(c) && SameIdentity(index, c));
            if (match is null)
            {
                unmatched.Add(index);
            }
            else
            {
                matched[index] = match;
            }
        }

        // 2. Same name (or the collection's one text index) but a different identity: it has to be rebuilt.
        var clashes = new Dictionary<IndexSpec, IndexSpec>();
        foreach (var index in unmatched.ToList())
        {
            var clash = candidates.FirstOrDefault(c => !matched.ContainsValue(c) && !clashes.ContainsValue(c) && c.Name == index.Name)
                        ?? (index.IsText ? candidates.FirstOrDefault(c => !matched.ContainsValue(c) && !clashes.ContainsValue(c) && c.IsText) : null);
            if (clash is not null)
            {
                clashes[index] = clash;
                unmatched.Remove(index);
            }
        }

        var claimed = matched.Values.Concat(clashes.Values).ToHashSet();
        foreach (var index in unmatched.Where(u => candidates.Any(c => c.Name == u.Name)))
        {
            plan.Problems.Add($"{name}: the declared index {Display(index.Keys)} is named {index.Name}, but the server's {index.Name} " +
                              "is another declared index. Give one of them a different name.");
        }

        var creates = new List<PlannedChange>();
        var modifies = new List<PlannedChange>();
        var rebuilds = new List<PlannedChange>();
        foreach (var (index, server) in matched)
        {
            var differences = Differences(index.Attributes, server.Attributes);
            if (differences.Count == 0)
            {
                continue;
            }

            if (differences.All(d => CanModify(d, canConvertToUnique)))
            {
                modifies.Add(Modify(server, differences));
            }
            else
            {
                rebuilds.Add(Rebuild(declaration, index, server, string.Join("; ", differences.Select(d => d.Describe())), policy, plan.Problems));
            }
        }

        foreach (var (index, server) in clashes)
        {
            rebuilds.Add(Rebuild(declaration, index, server, $"keys {Display(server.Keys)} → {Display(index.Keys)}", policy, plan.Problems));
        }

        foreach (var index in unmatched)
        {
            creates.Add(new PlannedChange
            {
                Info = Info(IndexActions.Create, index.Name, index.Keys, Summary(index.Options)),
                Create = CreateSpec(index, index.Name),
            });
        }

        // 3. Undeclared: prune them, unless they're ignored.
        var prune = declaration.Prune switch
        {
            IndexPrune.Hide => "hide",
            IndexPrune.Drop => "drop",
            IndexPrune.Report => "report",
            _ => policy.Prune,
        };
        var hides = new List<PlannedChange>();
        var drops = new List<PlannedChange>();
        var keeps = new List<PlannedChange>();
        foreach (var server in candidates.Where(c => !claimed.Contains(c) && !IsIgnored(c.Name)))
        {
            switch (prune)
            {
                case "drop":
                    drops.Add(Drop(server, "not declared"));
                    break;
                case "hide" when !server.Hidden:
                    plan.Hidden[server.Name] = policy.Now;
                    hides.Add(new PlannedChange
                    {
                        Info = Info(IndexActions.Hide, server.Name, server.Keys, "not declared"),
                        CollMods = [new BsonDocument { { "name", server.Name }, { "hidden", true } }],
                    });
                    break;
                case "hide":
                    var since = policy.Hidden.TryGetValue(server.Name, out var hiddenAt) ? hiddenAt : policy.Now;
                    if (since + policy.DropHiddenAfter <= policy.Now)
                    {
                        drops.Add(Drop(server, $"not declared; hidden since {Format(since)}"));
                    }
                    else
                    {
                        plan.Hidden[server.Name] = since;
                        keeps.Add(new PlannedChange { Info = Info(IndexActions.Keep, server.Name, server.Keys, $"hidden; dropped after {Format(since + policy.DropHiddenAfter)}") });
                    }

                    break;
                default:
                    keeps.Add(new PlannedChange { Info = Info(IndexActions.Keep, server.Name, server.Keys, "not declared (prune: report)") });
                    break;
            }
        }

        // Guard against a whole collection's indexes being replaced because the declared keys name different fields, e.g.
        // typed keys rendered without the app's class maps. Changing the keys of an index on the same fields doesn't trip it.
        // (A text index matches whatever its fields, so this looks at fields rather than matches.)
        var considered = candidates.Where(c => !IsIgnored(c.Name)).ToList();
        var declaredFields = declared.Where(d => !IsIdIndex(d)).SelectMany(Fields).ToHashSet(StringComparer.Ordinal);
        if (considered.Count > 0 && declaredFields.Count > 0 && !policy.AllowUnmatched && !declaredFields.Overlaps(considered.SelectMany(Fields)))
        {
            plan.Problems.Add($"{name}: none of the declared indexes use a field that the {considered.Count} existing index(es) use, so every declared index would be new. " +
                              "If the class maps or conventions that name the fields aren't registered in this process, typed keys render with different field names. " +
                              $"Check the plan, then run with --allow-unmatched {name}.");
        }

        plan.Changes.AddRange(creates);
        plan.Changes.AddRange(modifies);
        plan.Changes.AddRange(rebuilds);
        plan.Changes.AddRange(hides);
        plan.Changes.AddRange(drops);
        plan.Changes.AddRange(keeps);
        plan.Indexes.AddRange(existing.Select(e => Listing(e, matched, clashes, IsIgnored(e.Name), plan.Hidden)));
        return plan;
    }

    private static List<string> Validate(string collection, List<IndexSpec> declared)
    {
        var problems = new List<string>();
        for (var i = 0; i < declared.Count; i++)
        {
            for (var j = i + 1; j < declared.Count; j++)
            {
                if (SameIdentity(declared[i], declared[j]) && SameIdentity(declared[j], declared[i]))
                {
                    problems.Add($"{collection}: {Display(declared[i].Keys)} is declared more than once with the same collation and partial filter.");
                }
            }
        }

        foreach (var group in declared.GroupBy(d => d.Name).Where(g => g.Count() > 1))
        {
            problems.Add($"{collection}: more than one declared index is named {group.Key}.");
        }

        if (declared.Count(d => d.IsText) > 1)
        {
            problems.Add($"{collection}: a collection can only have one text index.");
        }

        return problems;
    }

    private static PlannedChange Modify(IndexSpec server, List<Difference> differences)
    {
        var change = new PlannedChange
        {
            Info = Info(IndexActions.Modify, server.Name, server.Keys, string.Join("; ", differences.Select(d => d.Describe()))),
        };
        foreach (var difference in differences)
        {
            if (difference.Name == "unique")
            {
                change.ConvertToUnique = true;
            }
            else
            {
                change.CollMods.Add(new BsonDocument { { "name", server.Name }, { difference.Name, difference.Declared ?? BsonBoolean.False } });
            }
        }

        return change;
    }

    private static PlannedChange Rebuild(CollectionDeclaration declaration, IndexSpec index, IndexSpec server, string detail, CollectionPolicy policy, List<string> problems)
    {
        // An unnamed declaration keeps the server's name, so queries that hint it by name keep working.
        var name = index.NameDeclared ? index.Name : server.Name;
        var change = new PlannedChange
        {
            Info = Info(IndexActions.Rebuild, server.Name, index.Keys, detail + " (drop, then create)"),
            Create = CreateSpec(index, name),
            DropName = server.Name,
            Existing = server.Source,
        };

        var source = index.OnRebuild == IndexRebuild.Fail || declaration.OnRebuild == IndexRebuild.Fail
            ? $"its definition sets {nameof(IndexRebuild)}.{nameof(IndexRebuild.Fail)}"
            : policy.OnRebuild == "fail" ? "indexes.onRebuild is fail" : null;
        if (source is not null && !policy.AllowRebuild.Contains(server.Name))
        {
            change.Info.Blocked = true;
            change.Info.Reason = source;
            problems.Add($"{declaration.Name}: rebuilding {server.Name} drops it before the new one builds, and {source}. " +
                         $"Give the declared index a new name so it builds alongside the old one, or run with --allow-rebuild {declaration.Name}.{server.Name}.");
        }

        return change;
    }

    private static PlannedChange Drop(IndexSpec server, string detail) => new()
    {
        Info = Info(IndexActions.Drop, server.Name, server.Keys, detail),
        DropName = server.Name,
        Existing = server.Source,
    };

    private static BsonDocument CreateSpec(IndexSpec index, string name)
    {
        var spec = new BsonDocument { { "key", index.Keys }, { "name", name } };
        spec.AddRange(index.Options!.Where(e => e.Name != "name"));
        return spec;
    }

    private static IndexInfo Listing(
        IndexSpec server,
        Dictionary<IndexSpec, IndexSpec> matched,
        Dictionary<IndexSpec, IndexSpec> clashes,
        bool ignored,
        Dictionary<string, DateTime> hidden)
    {
        var declaredAs = matched.FirstOrDefault(p => p.Value == server).Key;
        var options = new BsonDocument(server.Attributes);
        if (server.Collation is not null)
        {
            options.Add("collation", server.Collation);
        }

        if (server.PartialFilter is not null)
        {
            options.Add("partialFilterExpression", server.PartialFilter);
        }

        return new IndexInfo
        {
            Name = server.Name,
            Keys = MigrationOutputMapper.ToJson(server.Keys),
            Options = options.ElementCount > 0 ? MigrationOutputMapper.ToJson(options) : null,
            Status = server.Name == IdIndex ? "default"
                : declaredAs is not null || clashes.ContainsValue(server) ? "declared"
                : ignored ? "ignored"
                : "undeclared",
            DeclaredName = declaredAs is { NameDeclared: true } d && d.Name != server.Name ? d.Name : null,
            HiddenAt = hidden.TryGetValue(server.Name, out var at) ? at : null,
        };
    }

    private static IndexChangeInfo Info(string action, string index, BsonDocument keys, string? detail) => new()
    {
        Action = action,
        Index = index,
        Keys = MigrationOutputMapper.ToJson(keys),
        Detail = string.IsNullOrEmpty(detail) ? null : detail,
    };

    /// <summary>e.g. <c>unique, expireAfterSeconds 3600</c>.</summary>
    private static string? Summary(BsonDocument? options)
    {
        if (options is null)
        {
            return null;
        }

        var parts = options
            .Where(e => e.Name != "name")
            .Select(e => e.Value is BsonBoolean { Value: true } ? e.Name : $"{e.Name} {Display(e.Value)}")
            .ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// <summary>The fields an index covers (a text index's come from its weights).</summary>
    private static IEnumerable<string> Fields(IndexSpec index) =>
        index.Keys.Names.Where(n => n is not ("_fts" or "_ftsx"))
            .Concat(index.IsText && index.Attributes.GetValue("weights", null) is BsonDocument weights ? weights.Names : []);

    private static bool IsIdIndex(IndexSpec index) =>
        index.Key.ElementCount == 1 && index.Key.Contains("_id") && index.Key["_id"] is BsonInt64 { Value: 1 } &&
        index.Collation is null && index.PartialFilter is null;

    internal static bool SameIdentity(IndexSpec declared, IndexSpec server) =>
        declared.Key.Equals(server.Key) &&
        CollationMatches(declared.Collation, server.Collation) &&
        Equals(declared.PartialFilter, server.PartialFilter);

    /// <summary>
    /// The server fills in every collation field (strength, caseLevel, …), so a declared collation matches when the fields it
    /// sets agree.
    /// </summary>
    private static bool CollationMatches(BsonDocument? declared, BsonDocument? server) =>
        declared is null
            ? server is null
            : server is not null && declared.Where(e => e.Name != "version").All(e => server.TryGetValue(e.Name, out var value) && value.Equals(e.Value));

    private static List<Difference> Differences(BsonDocument declared, BsonDocument server) =>
        declared.Names.Union(server.Names)
            .Select(n => new Difference(n, declared.GetValue(n, null), server.GetValue(n, null)))
            .Where(d => !Equals(d.Declared, d.Server))
            .ToList();

    private static bool CanModify(Difference difference, bool canConvertToUnique) => difference.Name switch
    {
        "hidden" => true,
        "expireAfterSeconds" => difference.Declared is not null && difference.Server is not null,
        "unique" => canConvertToUnique && difference.Declared is BsonBoolean { Value: true },
        _ => false,
    };

    internal static IndexSpec FromDeclaration(IndexDeclaration declaration)
    {
        var options = declaration.Options;
        var key = new BsonDocument();
        var weights = new BsonDocument();
        var declaredWeights = options.GetValue("weights", null) as BsonDocument;
        foreach (var element in declaration.Keys)
        {
            if (element.Value is BsonString { Value: "text" })
            {
                if (!key.Contains("_fts"))
                {
                    key.Add("_fts", "text");
                    key.Add("_ftsx", 1L);
                }

                weights[element.Name] = declaredWeights?.GetValue(element.Name, null) ?? 1;
            }
            else
            {
                key.Add(element.Name, Normalise(element.Value));
            }
        }

        foreach (var weight in declaredWeights ?? new BsonDocument())
        {
            weights.Set(weight.Name, weight.Value);
        }

        var attributeSource = new BsonDocument(options);
        if (key.Contains("_fts"))
        {
            attributeSource["weights"] = weights;
        }

        var name = options.GetValue("name", null)?.AsString;
        return new IndexSpec
        {
            Name = name ?? DefaultName(declaration.Keys),
            NameDeclared = name is not null,
            Keys = declaration.Keys,
            Key = key,
            Collation = Collation(options),
            PartialFilter = options.GetValue("partialFilterExpression", null) is BsonDocument partial ? Normalise(partial).AsBsonDocument : null,
            Attributes = Attributes(attributeSource, key.Contains("_fts")),
            Source = declaration.Keys,
            Options = options,
            OnRebuild = declaration.OnRebuild,
        };
    }

    internal static IndexSpec FromServer(BsonDocument index)
    {
        var keys = index["key"].AsBsonDocument;
        var key = Normalise(keys).AsBsonDocument;
        var isText = key.Contains("_fts");
        return new IndexSpec
        {
            Name = index["name"].AsString,
            NameDeclared = true,
            Keys = keys,
            Key = key,
            Collation = Collation(index),
            PartialFilter = index.GetValue("partialFilterExpression", null) is BsonDocument partial ? Normalise(partial).AsBsonDocument : null,
            Attributes = Attributes(index, isText),
            Source = index,
        };
    }

    private static BsonDocument? Collation(BsonDocument document) =>
        document.GetValue("collation", null) is BsonDocument collation && collation.GetValue("locale", "simple") != "simple"
            ? Normalise(collation).AsBsonDocument
            : null;

    /// <summary>The options compared once an index is matched, with defaults left out.</summary>
    private static BsonDocument Attributes(BsonDocument document, bool isText)
    {
        var attributes = new BsonDocument();
        foreach (var flag in (string[])["unique", "sparse", "hidden"])
        {
            if (document.GetValue(flag, null) is { } value && value.ToBoolean())
            {
                attributes.Add(flag, true);
            }
        }

        if (document.GetValue("expireAfterSeconds", null) is { IsNumeric: true } ttl)
        {
            attributes.Add("expireAfterSeconds", Normalise(ttl));
        }

        if (isText)
        {
            if (document.GetValue("weights", null) is BsonDocument weights)
            {
                attributes.Add("weights", new BsonDocument(weights.OrderBy(w => w.Name, StringComparer.Ordinal).Select(w => new BsonElement(w.Name, Normalise(w.Value)))));
            }

            AddUnlessDefault(attributes, document, "default_language", "english");
            AddUnlessDefault(attributes, document, "language_override", "language");
        }

        if (document.GetValue("wildcardProjection", null) is BsonDocument projection)
        {
            attributes.Add("wildcardProjection", Normalise(projection, booleansAsNumbers: true));
        }

        AddUnlessDefault(attributes, document, "bits", 26L);
        AddUnlessDefault(attributes, document, "min", -180L);
        AddUnlessDefault(attributes, document, "max", 180L);
        if (document.GetValue("storageEngine", null) is BsonDocument storageEngine)
        {
            attributes.Add("storageEngine", storageEngine);
        }

        return attributes;
    }

    private static void AddUnlessDefault(BsonDocument attributes, BsonDocument document, string name, BsonValue defaultValue)
    {
        if (document.GetValue(name, null) is { } value && !Normalise(value).Equals(Normalise(defaultValue)))
        {
            attributes.Add(name, Normalise(value));
        }
    }

    /// <summary>Numbers compared by value (<c>1</c>, <c>1.0</c> and <c>NumberLong(1)</c> are equal).</summary>
    internal static BsonValue Normalise(BsonValue value, bool booleansAsNumbers = false) => value switch
    {
        BsonDocument document => new BsonDocument(document.Select(e => new BsonElement(e.Name, Normalise(e.Value, booleansAsNumbers)))),
        BsonArray array => new BsonArray(array.Select(v => Normalise(v, booleansAsNumbers))),
        BsonBoolean boolean when booleansAsNumbers => new BsonInt64(boolean.Value ? 1 : 0),
        { IsNumeric: true } when value.ToDouble() is var d && Math.Floor(d) == d && Math.Abs(d) < 9e15 => new BsonInt64((long)d),
        { IsNumeric: true } => new BsonDouble(value.ToDouble()),
        _ => value,
    };

    /// <summary>The name MongoDB gives an index without one, e.g. <c>companyId_1_status_-1</c> or <c>title_text</c>.</summary>
    internal static string DefaultName(BsonDocument keys) =>
        string.Join("_", keys.Select(e => e.Name + "_" + (e.Value.IsNumeric ? Normalise(e.Value).ToString() : e.Value.ToString())));

    /// <summary>A hash of what's declared, so a run can tell whether the definition changed since it was last applied.</summary>
    private static string Hash(CollectionDeclaration declaration, List<IndexSpec> declared)
    {
        var indexes = declared
            .Select(d =>
            {
                var document = new BsonDocument { { "key", d.Key }, { "attributes", d.Attributes } };
                if (d.NameDeclared)
                {
                    document.Add("name", d.Name);
                }

                if (d.Collation is not null)
                {
                    document.Add("collation", d.Collation);
                }

                if (d.PartialFilter is not null)
                {
                    document.Add("partialFilterExpression", d.PartialFilter);
                }

                if (d.OnRebuild is { } onRebuild)
                {
                    document.Add("onRebuild", onRebuild.ToString());
                }

                return document.ToJson(Canonical);
            })
            .Order(StringComparer.Ordinal);
        var canonical = new BsonDocument
        {
            { "indexes", new BsonArray(indexes) },
            { "ignored", new BsonArray(declaration.Ignored.Order(StringComparer.Ordinal)) },
            { "prune", declaration.Prune?.ToString() ?? "" },
            { "onRebuild", declaration.OnRebuild?.ToString() ?? "" },
        }.ToJson(Canonical);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static Regex Glob(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>A value as the shell would show it, e.g. <c>{ companyId: 1 }</c>.</summary>
    internal static string Display(BsonValue value)
    {
        using var json = System.Text.Json.JsonDocument.Parse(new BsonDocument("v", value).ToJson(Canonical));
        return ExtendedJson.Format(json.RootElement.GetProperty("v"), quoteStrings: true);
    }

    private static string Format(DateTime at) => at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private sealed record Difference(string Name, BsonValue? Declared, BsonValue? Server)
    {
        public string Describe() => (Server, Declared) switch
        {
            (null, BsonBoolean { Value: true }) => $"{Name}: false → true",
            (BsonBoolean { Value: true }, null) => $"{Name}: true → false",
            (null, { } added) => $"{Name}: none → {Display(added)}",
            ({ } removed, null) => $"{Name}: {Display(removed)} → none",
            _ => $"{Name} {Display(Server!)} → {Display(Declared!)}",
        };
    }
}

/// <summary>An index as the planner compares it: declared, or listed by the server.</summary>
internal sealed class IndexSpec
{
    /// <summary>The server's name, or the declared one (the default name when none was declared).</summary>
    public required string Name { get; init; }

    public required bool NameDeclared { get; init; }

    /// <summary>The keys as declared or listed, e.g. <c>{ title: "text" }</c>.</summary>
    public required BsonDocument Keys { get; init; }

    /// <summary>The keys as the server stores them, numbers normalised (text indexes become <c>{ _fts: "text", _ftsx: 1 }</c>).</summary>
    public required BsonDocument Key { get; init; }

    public BsonDocument? Collation { get; init; }

    public BsonDocument? PartialFilter { get; init; }

    public required BsonDocument Attributes { get; init; }

    /// <summary>For a server index, its <c>listIndexes</c> entry; for a declared one, its keys.</summary>
    public required BsonDocument Source { get; init; }

    /// <summary>For a declared index, its options.</summary>
    public BsonDocument? Options { get; init; }

    public IndexRebuild? OnRebuild { get; init; }

    public bool IsText => Key.Contains("_fts");

    public bool Hidden => Attributes.Contains("hidden");
}

/// <summary>A plan the applier can carry out.</summary>
internal sealed class IndexPlan
{
    public string Declaration { get; init; } = "";

    public bool Invalid { get; init; }

    public List<PlannedChange> Changes { get; } = [];

    public List<string> Problems { get; init; } = [];

    public List<IndexInfo> Indexes { get; } = [];

    public Dictionary<string, DateTime> Hidden { get; } = new(StringComparer.Ordinal);

    public static IndexPlan InvalidPlan(string problem) => new() { Invalid = true, Problems = [problem] };

    public CollectionPlan ToCollectionPlan() => new()
    {
        Declaration = Declaration,
        Changes = Changes.Select(c => c.Info).ToList(),
        Problems = Problems,
        Invalid = Invalid,
        Indexes = Indexes,
        Hidden = new Dictionary<string, DateTime>(Hidden, StringComparer.Ordinal),
    };
}

internal sealed class PlannedChange
{
    public required IndexChangeInfo Info { get; init; }

    /// <summary>For a create or rebuild: the index as <c>createIndexes</c> takes it (<c>key</c>, <c>name</c> and options).</summary>
    public BsonDocument? Create { get; init; }

    /// <summary>For a rebuild or drop: the server index to drop.</summary>
    public string? DropName { get; init; }

    /// <summary>For a rebuild or drop: the server's <c>listIndexes</c> entry, to restore it if the rebuild fails.</summary>
    public BsonDocument? Existing { get; init; }

    /// <summary>For a modify or hide: the <c>collMod</c> <c>index</c> documents to run.</summary>
    public List<BsonDocument> CollMods { get; init; } = [];

    /// <summary>For a modify: make the index unique (<c>prepareUnique</c>, then <c>unique</c>).</summary>
    public bool ConvertToUnique { get; set; }
}
