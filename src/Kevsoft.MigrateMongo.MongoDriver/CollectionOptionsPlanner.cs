using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.Protocol;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace Kevsoft.MigrateMongo.MongoDriver;

/// <summary>
/// Works out how to bring a collection's options and validator in line with its definition, from its <c>listCollections</c>
/// entry. A missing collection is created with every declared option in one <c>create</c>. On an existing one, only declared
/// options are compared: what MongoDB can change in place becomes a <c>collMod</c>, a plain collection declared capped becomes
/// <c>convertToCapped</c> (when allowed), and anything else is unchangeable: a problem, or skipped with a warning.
/// </summary>
internal static class CollectionOptionsPlanner
{
    /// <summary>MongoDB 5.3: clustered collections.</summary>
    internal const int ClusteredWireVersion = 16;

    /// <summary>MongoDB 6.0: change stream pre- and post-images, and resizing a capped collection.</summary>
    internal const int SixZeroWireVersion = 17;

    /// <summary>MongoDB 6.3: custom time series bucketing.</summary>
    internal const int CustomBucketingWireVersion = 20;

    /// <summary>MongoDB 8.1: the <c>errorAndLog</c> validation action.</summary>
    internal const int ErrorAndLogWireVersion = 26;

    /// <summary>MongoDB 9.0: the <c>constraint</c> validation level.</summary>
    internal const int ConstraintWireVersion = 29;

    private const string CantChange = "can't be changed on an existing collection";

    private static readonly JsonWriterSettings Canonical = new() { OutputMode = JsonOutputMode.CanonicalExtendedJson };

    /// <summary>
    /// Plans the collection's options and validator. <paramref name="existing"/> is its <c>listCollections</c> entry (null if
    /// it doesn't exist), <paramref name="filledCollation"/> the declared default collation as the server fills it in (if it
    /// could be explained), and <paramref name="invalid"/> the existing documents that fail the declared validator, when they
    /// were counted (see <see cref="ValidatorToCount"/>).
    /// </summary>
    public static CollectionOptionsPlan Plan(
        CollectionDeclaration declaration,
        BsonDocument? existing,
        CollectionPolicy policy,
        int wireVersion,
        BsonDocument? filledCollation = null,
        InvalidDocumentCount? invalid = null)
    {
        var name = declaration.Name;
        var plan = new CollectionOptionsPlan
        {
            Declaration = declaration.DeclaresOptions ? Hash(declaration) : "",
            Existing = existing?.GetValue("options", null) as BsonDocument,
        };

        if (existing is not null && existing.GetValue("type", "collection") == "view")
        {
            plan.IsView = true;
            plan.Problems.Add($"{name}: a view with this name exists, so it can't be managed as a collection. Drop the view or rename the definition.");
            return plan;
        }

        plan.Problems.AddRange(TooOld(declaration, wireVersion));
        if (plan.Problems.Count > 0)
        {
            return plan;
        }

        if (existing is null)
        {
            plan.Changes.Add(Create(declaration));
            return plan;
        }

        var server = plan.Existing ?? [];
        var isTimeSeries = existing.GetValue("type", "collection") == "timeseries" || server.Contains("timeseries");
        var isClustered = server.GetValue("clusteredIndex", null) is BsonDocument;
        var declared = declaration.Options;
        var context = new PlanContext(declaration, policy, plan);

        if (declared.GetValue("capped", false).ToBoolean())
        {
            PlanCapped(context, server, isTimeSeries, isClustered, wireVersion);
        }

        if (declared.GetValue("timeseries", null) is BsonDocument timeSeries)
        {
            PlanTimeSeries(context, timeSeries, server, isTimeSeries);
        }

        if (declared.GetValue("clusteredIndex", null) is BsonDocument clustered)
        {
            PlanClustered(context, clustered, server, isClustered);
        }

        if (declared.GetValue("collation", null) is BsonDocument collation)
        {
            PlanCollation(context, collation, filledCollation, server);
        }

        if (declared.GetValue("changeStreamPreAndPostImages", null) is BsonDocument images)
        {
            var enabled = images.GetValue("enabled", false).ToBoolean();
            var current = server.GetValue("changeStreamPreAndPostImages", null) is BsonDocument on && on.GetValue("enabled", false).ToBoolean();
            if (enabled != current)
            {
                context.Modify(
                    "changeStreamPreAndPostImages",
                    $"{Bool(current)} → {Bool(enabled)}",
                    new BsonDocument("changeStreamPreAndPostImages", new BsonDocument("enabled", current)),
                    new BsonDocument("changeStreamPreAndPostImages", new BsonDocument("enabled", enabled)));
            }
        }

        PlanValidator(context, server, invalid);
        return plan;
    }

    /// <summary>
    /// The validator to count existing documents against before it's applied, or null: only when a declared validator differs
    /// from an existing collection's and <see cref="CollectionPolicy.CountInvalidDocuments"/> is on.
    /// </summary>
    public static BsonDocument? ValidatorToCount(CollectionDeclaration declaration, BsonDocument? existing, CollectionPolicy policy)
    {
        if (!policy.CountInvalidDocuments || existing is null || existing.GetValue("type", "collection") == "view" ||
            declaration.Validator?.Validator is not { } validator)
        {
            return null;
        }

        var server = existing.GetValue("options", null) as BsonDocument ?? [];
        return SameValidator(validator, server.GetValue("validator", null) as BsonDocument) ? null : validator;
    }

    /// <summary>The first problem for an option the server is too old for, so nothing changes.</summary>
    private static List<string> TooOld(CollectionDeclaration declaration, int wireVersion)
    {
        var problems = new List<string>();
        void Needs(bool declared, int version, string what, string release)
        {
            if (declared && wireVersion < version)
            {
                problems.Add($"{declaration.Name}: {what} needs MongoDB {release} or later, and this server is older.");
            }
        }

        var options = declaration.Options;
        Needs(options.Contains("clusteredIndex"), ClusteredWireVersion, "a clustered collection", "5.3");
        Needs(options.Contains("changeStreamPreAndPostImages"), SixZeroWireVersion, "changeStreamPreAndPostImages", "6.0");
        Needs(options.GetValue("timeseries", null) is BsonDocument ts && ts.Contains("bucketMaxSpanSeconds"), CustomBucketingWireVersion, "custom time series bucketing", "6.3");
        Needs(declaration.Validator?.Action == "errorAndLog", ErrorAndLogWireVersion, "the errorAndLog validation action", "8.1");
        Needs(declaration.Validator?.Level == "constraint", ConstraintWireVersion, "the constraint validation level", "9.0");
        return problems;
    }

    private static CollectionOptionsChange Create(CollectionDeclaration declaration)
    {
        var command = new BsonDocument("create", declaration.Name);
        var after = new BsonDocument(declaration.Options.DeepClone().AsBsonDocument);
        if (declaration.Validator is { } validator)
        {
            if (validator.Validator is not null)
            {
                after.Add("validator", validator.Validator.DeepClone());
            }

            if (validator.Level is not null)
            {
                after.Add("validationLevel", validator.Level);
            }

            if (validator.Action is not null)
            {
                after.Add("validationAction", validator.Action);
            }
        }

        command.AddRange(after);
        return new CollectionOptionsChange
        {
            Info = new IndexChangeInfo
            {
                Action = IndexActions.CreateCollection,
                Index = "collection",
                Detail = after.ElementCount > 0 ? Summary(after) : null,
                After = after.ElementCount > 0 ? MigrationOutputMapper.ToJson(after) : null,
            },
            Commands = [command],
        };
    }

    private static void PlanCapped(PlanContext context, BsonDocument server, bool isTimeSeries, bool isClustered, int wireVersion)
    {
        var declared = context.Declaration.Options;
        var size = declared["size"].ToInt64();
        var max = declared.GetValue("max", null)?.ToInt64();
        var name = context.Declaration.Name;

        if (isTimeSeries || isClustered)
        {
            var kind = isTimeSeries ? "time series" : "clustered";
            context.Unchangeable("capped", $"{kind} → capped", $"a {kind} collection can't become capped, so it");
            return;
        }

        if (!server.GetValue("capped", false).ToBoolean())
        {
            var command = new BsonDocument { { "convertToCapped", name }, { "size", size } };
            var allowed = context.Policy.AllowConvertToCapped ||
                          (context.Policy.OnConvertToCapped == "allow" && context.Declaration.OnConvertToCapped != ConvertToCapped.Fail);
            var change = new CollectionOptionsChange
            {
                Info = new IndexChangeInfo
                {
                    Action = IndexActions.ConvertToCapped,
                    Index = "capped",
                    Detail = $"not capped → capped, size {Number(size)}; keeps only _id_, so the indexes are created again",
                    Before = MigrationOutputMapper.ToJson(new BsonDocument("capped", false)),
                    After = MigrationOutputMapper.ToJson(new BsonDocument { { "capped", true }, { "size", size } }),
                    Warning = allowed
                        ? "converting to capped holds an exclusive database lock while it copies the data and loses the oldest documents beyond the size"
                        : null,
                },
                Commands = [command],
                Previous = new BsonDocument("options", server.DeepClone()),
            };
            if (!allowed)
            {
                change.Info.Blocked = true;
                change.Info.Reason = "converting to capped isn't allowed";
                context.Plan.Problems.Add(
                    $"{name}: converting to capped holds an exclusive database lock while it copies the data, keeps only the _id_ index " +
                    "and loses the oldest documents if the data is bigger than the size. " +
                    (context.Declaration.OnConvertToCapped == ConvertToCapped.Fail
                        ? "The definition sets OnConvertToCapped = Fail."
                        : $"Run with --allow-convert-to-capped {name}, or set collections.onConvertToCapped: allow."));
            }

            context.Plan.Changes.Add(change);
            if (max is { } cappedMax)
            {
                if (wireVersion >= SixZeroWireVersion)
                {
                    context.Modify("cappedMax", $"none → {Number(cappedMax)}", new BsonDocument("max", BsonNull.Value), new BsonDocument("max", cappedMax), command: "cappedMax", value: cappedMax);
                }
                else
                {
                    context.Unchangeable("cappedMax", $"none → {Number(cappedMax)}", "a capped collection's max can only change on MongoDB 6.0+, so it");
                }
            }

            return;
        }

        var serverSize = server.GetValue("size", 0).ToInt64();
        if (CappedSize(size) != CappedSize(serverSize))
        {
            ResizeCapped(context, wireVersion, "cappedSize", "size", serverSize, size);
        }

        var serverMax = server.GetValue("max", 0).ToInt64();
        if (max is { } newMax && newMax != serverMax)
        {
            ResizeCapped(context, wireVersion, "cappedMax", "max", serverMax, newMax);
        }
    }

    private static void ResizeCapped(PlanContext context, int wireVersion, string option, string field, long current, long declared)
    {
        var detail = $"{(current == 0 && field == "max" ? "none" : Number(current))} → {Number(declared)}";
        if (wireVersion >= SixZeroWireVersion)
        {
            context.Modify(option, detail, new BsonDocument(field, current), new BsonDocument(field, declared), command: option, value: declared);
        }
        else
        {
            context.Unchangeable(option, detail, $"a capped collection's {field} can only change on MongoDB 6.0+, so it");
        }
    }

    /// <summary>MongoDB raises a capped collection's size to a multiple of 256 bytes.</summary>
    internal static long CappedSize(long size) => (size + 255) / 256 * 256;

    private static void PlanTimeSeries(PlanContext context, BsonDocument declared, BsonDocument server, bool isTimeSeries)
    {
        if (!isTimeSeries)
        {
            context.Unchangeable("timeseries", "not time series → time series", "a collection can't become a time series collection, so it");
            return;
        }

        var current = server.GetValue("timeseries", null) as BsonDocument ?? [];
        var timeField = declared["timeField"].AsString;
        var serverTimeField = current.GetValue("timeField", "").AsString;
        if (timeField != serverTimeField)
        {
            context.Unchangeable("timeseries.timeField", $"{serverTimeField} → {timeField}", "the time series timeField");
        }

        var metaField = declared.GetValue("metaField", null)?.AsString;
        var serverMetaField = current.GetValue("metaField", null)?.AsString;
        if (metaField != serverMetaField)
        {
            context.Unchangeable("timeseries.metaField", $"{serverMetaField ?? "none"} → {metaField ?? "none"}", "the time series metaField");
        }

        var (wanted, wantedLabel) = Bucketing(declared);
        var (have, haveLabel) = Bucketing(current);
        if (wantedLabel != haveLabel)
        {
            var detail = $"{haveLabel} → {wantedLabel}";
            if (wanted.MaxSpan >= have.MaxSpan && wanted.Rounding >= have.Rounding)
            {
                var timeseries = new BsonDocument();
                if (declared.GetValue("bucketMaxSpanSeconds", null) is { } maxSpan)
                {
                    timeseries.Add("bucketMaxSpanSeconds", maxSpan);
                    timeseries.Add("bucketRoundingSeconds", declared["bucketRoundingSeconds"]);
                }
                else
                {
                    timeseries.Add("granularity", declared.GetValue("granularity", "seconds"));
                }

                context.Modify("timeseries.granularity", detail, Granularity(current), timeseries, command: "timeseries", value: timeseries);
            }
            else
            {
                context.Unchangeable("timeseries.granularity", detail, "the time series granularity can only be made coarser, so it");
            }
        }

        PlanExpireAfter(context, server);
    }

    private static BsonDocument Granularity(BsonDocument timeseries)
    {
        var document = new BsonDocument();
        foreach (var field in new[] { "granularity", "bucketMaxSpanSeconds", "bucketRoundingSeconds" })
        {
            if (timeseries.GetValue(field, null) is { } value)
            {
                document.Add(field, value);
            }
        }

        return document;
    }

    /// <summary>A time series collection's bucketing: its span and rounding in seconds, and how it's shown.</summary>
    private static ((long MaxSpan, long Rounding) Spans, string Label) Bucketing(BsonDocument timeseries)
    {
        if (timeseries.GetValue("granularity", null) is BsonString { Value: var granularity })
        {
            return (GranularitySpans(granularity), granularity);
        }

        if (timeseries.GetValue("bucketMaxSpanSeconds", null) is { IsNumeric: true } maxSpan &&
            timeseries.GetValue("bucketRoundingSeconds", null) is { IsNumeric: true } rounding)
        {
            return ((maxSpan.ToInt64(), rounding.ToInt64()),
                $"bucketMaxSpanSeconds {Number(maxSpan.ToInt64())}, bucketRoundingSeconds {Number(rounding.ToInt64())}");
        }

        return (GranularitySpans("seconds"), "seconds");
    }

    private static (long MaxSpan, long Rounding) GranularitySpans(string granularity) => granularity switch
    {
        "minutes" => (86400, 3600),
        "hours" => (2592000, 86400),
        _ => (3600, 60),
    };

    /// <summary>Time series and clustered TTL: <c>expireAfterSeconds</c>, added, changed or removed (<c>"off"</c>) in place.</summary>
    private static void PlanExpireAfter(PlanContext context, BsonDocument server)
    {
        var declared = context.Declaration.Options.GetValue("expireAfterSeconds", null)?.ToInt64();
        var current = server.GetValue("expireAfterSeconds", null) is { IsNumeric: true } value ? value.ToInt64() : (long?)null;
        if (declared == current)
        {
            return;
        }

        context.Modify(
            "expireAfterSeconds",
            $"{(current is { } c ? Number(c) : "none")} → {(declared is { } d ? Number(d) : "none")}",
            new BsonDocument("expireAfterSeconds", current is { } before ? before : BsonNull.Value),
            new BsonDocument("expireAfterSeconds", declared is { } after ? after : BsonNull.Value),
            command: "expireAfterSeconds",
            value: declared is { } seconds ? (BsonValue)seconds : "off");
    }

    private static void PlanClustered(PlanContext context, BsonDocument declared, BsonDocument server, bool isClustered)
    {
        if (!isClustered)
        {
            context.Unchangeable("clusteredIndex", "not clustered → clustered", "a collection can't become clustered, so it");
            return;
        }

        var current = (BsonDocument)server["clusteredIndex"];
        if (declared.GetValue("name", null)?.AsString is { } name && current.GetValue("name", null)?.AsString is var serverName && name != serverName)
        {
            context.Unchangeable("clusteredIndex.name", $"{serverName ?? "none"} → {name}", "the clustered index's name");
        }

        PlanExpireAfter(context, server);
    }

    private static void PlanCollation(PlanContext context, BsonDocument declared, BsonDocument? filled, BsonDocument server)
    {
        var current = server.GetValue("collation", null) as BsonDocument;
        var wanted = declared.GetValue("locale", "simple") == "simple" ? null : filled ?? declared;
        if (SameCollation(wanted, current, compareAll: filled is not null))
        {
            return;
        }

        context.Unchangeable(
            "collation",
            $"{(current is null ? "none" : IndexPlanner.Display(WithoutVersion(current)))} → {(wanted is null ? "none" : IndexPlanner.Display(declared))}",
            "the default collation");
    }

    /// <summary>
    /// Whether the declared default collation is the server's. The server fills in every field, so a declared one the server
    /// explained is compared in full; one it couldn't explain only by the fields it sets. <c>version</c> is left out.
    /// </summary>
    private static bool SameCollation(BsonDocument? declared, BsonDocument? server, bool compareAll)
    {
        if (declared is null || server is null)
        {
            return declared is null && server is null;
        }

        var wanted = WithoutVersion(declared);
        var have = WithoutVersion(server);
        if (!compareAll)
        {
            have = new BsonDocument(have.Where(e => wanted.Contains(e.Name)));
        }

        return Sorted(wanted).Equals(Sorted(have));
    }

    private static BsonDocument Sorted(BsonDocument document) =>
        (BsonDocument)IndexPlanner.Normalise(new BsonDocument(document.OrderBy(e => e.Name, StringComparer.Ordinal)), booleansAsNumbers: true);

    private static BsonDocument WithoutVersion(BsonDocument collation) => new(collation.Where(e => e.Name != "version"));

    private static void PlanValidator(PlanContext context, BsonDocument server, InvalidDocumentCount? invalid)
    {
        var name = context.Declaration.Name;
        var currentValidator = server.GetValue("validator", null) as BsonDocument;
        var currentLevel = server.GetValue("validationLevel", "strict").AsString;
        var currentAction = server.GetValue("validationAction", "error").AsString;
        var before = new BsonDocument
        {
            { "validator", currentValidator is null ? BsonNull.Value : currentValidator.DeepClone() },
            { "validationLevel", currentLevel },
            { "validationAction", currentAction },
        };

        if (context.Declaration.Validator is not { } declared)
        {
            if (currentValidator is not { ElementCount: > 0 })
            {
                return;
            }

            if (context.Policy.ValidatorPrune == "remove")
            {
                context.Plan.Changes.Add(new CollectionOptionsChange
                {
                    Info = new IndexChangeInfo
                    {
                        Action = IndexActions.RemoveValidator,
                        Index = "validator",
                        Detail = "not declared",
                        Before = MigrationOutputMapper.ToJson(before),
                        After = MigrationOutputMapper.ToJson(new BsonDocument("validator", new BsonDocument())),
                    },
                    Commands = [new BsonDocument { { "collMod", name }, { "validator", new BsonDocument() } }],
                    Previous = before,
                });
            }
            else
            {
                context.Plan.Changes.Add(new CollectionOptionsChange
                {
                    Info = new IndexChangeInfo { Action = IndexActions.Keep, Index = "validator", Detail = "not declared" },
                });
            }

            return;
        }

        var details = new List<string>();
        var command = new BsonDocument("collMod", name);
        var after = new BsonDocument();
        var validatorChanged = declared.Validator is not null && !SameValidator(declared.Validator, currentValidator);
        if (validatorChanged)
        {
            details.Add(currentValidator is { ElementCount: > 0 } ? "$jsonSchema changed" : "none → $jsonSchema");
            command.Add("validator", declared.Validator!.DeepClone());
            after.Add("validator", declared.Validator.DeepClone());
        }

        if (declared.Level is { } level && level != currentLevel)
        {
            details.Add($"validationLevel {currentLevel} → {level}");
            command.Add("validationLevel", level);
            after.Add("validationLevel", level);
        }

        if (declared.Action is { } action && action != currentAction)
        {
            details.Add($"validationAction {currentAction} → {action}");
            command.Add("validationAction", action);
            after.Add("validationAction", action);
        }

        if (details.Count == 0)
        {
            return;
        }

        var commands = new List<BsonDocument>();
        if (declared.Level == "constraint" && currentLevel != "constraint")
        {
            commands.Add(new BsonDocument { { "collMod", name }, { "prepareConstraintValidationLevel", true } });
        }

        commands.Add(command);
        var change = new CollectionOptionsChange
        {
            Info = new IndexChangeInfo
            {
                Action = IndexActions.SetValidator,
                Index = "validator",
                Detail = string.Join("; ", details),
                Before = MigrationOutputMapper.ToJson(before),
                After = MigrationOutputMapper.ToJson(after),
            },
            Commands = commands,
            Previous = before,
        };

        if (validatorChanged && invalid is { Count: > 0 } found)
        {
            var examples = found.SampleIds.Count > 0 ? $" (e.g. {string.Join(", ", found.SampleIds.Select(IndexPlanner.Display))})" : "";
            var count = $"{Number(found.Count)} existing document{(found.Count == 1 ? " doesn't" : "s don't")} match the validator{examples}";
            if (context.Policy.OnInvalidDocuments == "fail" && !context.Policy.AllowInvalidDocuments)
            {
                change.Info.Blocked = true;
                change.Info.Reason = count;
                context.Plan.Problems.Add(
                    $"{name}: {count}. Fix them first (e.g. in a migration), or run with --allow-invalid-documents {name} to apply the validator anyway; " +
                    "MongoDB only checks later writes.");
            }
            else
            {
                change.Info.Warning = count;
            }
        }

        context.Plan.Changes.Add(change);
    }

    private static bool SameValidator(BsonDocument declared, BsonDocument? server) =>
        IndexPlanner.Normalise(declared).Equals(IndexPlanner.Normalise(server ?? []));

    /// <summary>A hash of the declared options and validator, folded into the collection's declaration hash.</summary>
    private static string Hash(CollectionDeclaration declaration)
    {
        var canonical = new BsonDocument
        {
            { "options", declaration.Options },
            { "validator", declaration.Validator?.Validator ?? (BsonValue)BsonNull.Value },
            { "validationLevel", declaration.Validator?.Level ?? "" },
            { "validationAction", declaration.Validator?.Action ?? "" },
            { "onUnchangeable", declaration.OnUnchangeable?.ToString() ?? "" },
            { "onConvertToCapped", declaration.OnConvertToCapped?.ToString() ?? "" },
        }.ToJson(Canonical);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string Summary(BsonDocument options) =>
        string.Join(", ", options.Select(e => e.Name switch
        {
            "validator" => "validator",
            _ => $"{e.Name}: {IndexPlanner.Display(e.Value)}",
        }));

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? "true" : "false";

    private sealed class PlanContext(CollectionDeclaration declaration, CollectionPolicy policy, CollectionOptionsPlan plan)
    {
        public CollectionDeclaration Declaration { get; } = declaration;

        public CollectionPolicy Policy { get; } = policy;

        public CollectionOptionsPlan Plan { get; } = plan;

        /// <summary>A change made in place with <c>collMod</c>, setting <paramref name="command"/> (the option itself by default).</summary>
        public void Modify(string option, string detail, BsonDocument before, BsonDocument after, string? command = null, BsonValue? value = null)
        {
            var collMod = new BsonDocument("collMod", Declaration.Name);
            if (command is null)
            {
                collMod.AddRange(after);
            }
            else
            {
                collMod.Add(command, value);
            }

            Plan.Changes.Add(new CollectionOptionsChange
            {
                Info = new IndexChangeInfo
                {
                    Action = IndexActions.ModifyCollection,
                    Index = option,
                    Detail = detail,
                    Before = MigrationOutputMapper.ToJson(before),
                    After = MigrationOutputMapper.ToJson(after),
                },
                Commands = [collMod],
                Previous = before,
            });
        }

        /// <summary>
        /// A difference MongoDB can't make on an existing collection: skipped with a warning when <c>--skip-unchangeable</c>,
        /// the definition or the config says so, otherwise a problem.
        /// </summary>
        /// <param name="option">The option, e.g. <c>timeseries.metaField</c>.</param>
        /// <param name="detail">The server's value and the declared one, e.g. <c>sensor → device</c>.</param>
        /// <param name="subject">What can't change, completing "… can't change in place", e.g. <c>the time series metaField</c>.</param>
        public void Unchangeable(string option, string detail, string subject)
        {
            var name = Declaration.Name;
            var skip = Policy.SkipUnchangeable ||
                       (Declaration.OnUnchangeable is { } own ? own == UnchangeableOption.Skip : Policy.OnUnchangeable == "skip");
            var info = new IndexChangeInfo { Action = skip ? IndexActions.Skip : IndexActions.ModifyCollection, Index = option, Detail = detail, Reason = CantChange };
            if (skip)
            {
                info.Warning = $"{option} {detail} {CantChange}, so it's skipped";
            }
            else
            {
                info.Blocked = true;
                Plan.Problems.Add(
                    $"{name}: {subject} can't change in place. Write a migration that copies the data into a new collection with the new " +
                    $"options and renames it, drop the collection if the data can go, or skip the change with --skip-unchangeable {name}.");
            }

            Plan.Changes.Add(new CollectionOptionsChange { Info = info });
        }
    }
}

/// <summary>Existing documents that fail a declared validator: how many, and a few of their <c>_id</c>s.</summary>
internal sealed record InvalidDocumentCount(long Count, IReadOnlyList<BsonValue> SampleIds);

/// <summary>What bringing a collection's options and validator in line would run.</summary>
internal sealed class CollectionOptionsPlan
{
    /// <summary>A hash of the declared options and validator; empty when the definition declares none.</summary>
    public string Declaration { get; init; } = "";

    /// <summary>The collection's options on the server (null when it doesn't exist).</summary>
    public BsonDocument? Existing { get; init; }

    /// <summary>The name is a view, so nothing else is planned.</summary>
    public bool IsView { get; set; }

    public List<CollectionOptionsChange> Changes { get; } = [];

    public List<string> Problems { get; } = [];

    /// <summary>Whether applying the plan runs any command.</summary>
    public bool RunsCommands => Changes.Any(c => c.Commands.Count > 0);
}

internal sealed class CollectionOptionsChange
{
    public required IndexChangeInfo Info { get; init; }

    /// <summary>The commands to run, in order; none for a skipped, blocked or kept change.</summary>
    public List<BsonDocument> Commands { get; init; } = [];

    /// <summary>The server's values before the change, recorded with each command.</summary>
    public BsonDocument? Previous { get; init; }
}
