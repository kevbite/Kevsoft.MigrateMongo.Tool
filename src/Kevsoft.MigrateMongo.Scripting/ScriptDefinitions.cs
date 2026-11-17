using MongoDB.Bson;
using MongoDB.Driver;

namespace Kevsoft.MigrateMongo.Scripting;

/// <summary>
/// Replays a collection or view definition built by the script runtime's <c>collection()</c> / <c>view()</c> (already
/// converted to BSON) onto the C# builders, so it's checked, rendered and applied exactly like a C# definition.
/// </summary>
internal static class ScriptDefinitions
{
    public static CollectionDescriptor ToDescriptor(BsonDocument definition, string origin)
    {
        var name = definition["name"].AsString;
        return definition["kind"].AsString switch
        {
            "collection" => CollectionDescriptor.Create(name, ScriptMigrationSource.SourceName, origin, builder => Collection(builder, definition, origin)),
            "view" => CollectionDescriptor.CreateView(name, definition["on"].AsString, ScriptMigrationSource.SourceName, origin, builder => View(builder, definition, origin)),
            var kind => throw new ScriptException($"{origin}: unknown definition kind '{kind}'."),
        };
    }

    internal static void Collection(CollectionBuilder<BsonDocument> builder, BsonDocument definition, string origin)
    {
        try
        {
            Indexes(builder.Indexes, definition["indexes"].AsBsonDocument);
            SearchIndexes(builder.SearchIndexes, definition["searchIndexes"].AsBsonDocument);
            Options(builder.Options, definition["options"].AsBsonDocument);
            Validator(builder.Validator, definition["validator"].AsBsonDocument);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException or InvalidCastException)
        {
            throw new ScriptException($"{origin}: collection '{builder.Name}': {e.Message}", e);
        }
    }

    internal static void View(ViewBuilder<BsonDocument> builder, BsonDocument definition, string origin)
    {
        try
        {
            if (Value(definition, "pipeline") is { } pipeline)
            {
                builder.Pipeline(pipeline.AsBsonArray.Select(s => s.AsBsonDocument));
            }

            if (Value(definition, "collation") is { } collation)
            {
                builder.Collation(MongoDB.Driver.Collation.FromBsonDocument(collation.AsBsonDocument));
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException or InvalidCastException)
        {
            throw new ScriptException($"{origin}: view '{builder.Name}': {e.Message}", e);
        }
    }

    private static void Indexes(IndexesBuilder<BsonDocument> indexes, BsonDocument definition)
    {
        indexes.Prune = Enum<IndexPrune>(definition, "prune");
        indexes.OnRebuild = Enum<IndexRebuild>(definition, "onRebuild");
        foreach (var pattern in definition["ignored"].AsBsonArray)
        {
            indexes.Ignore(pattern.AsString);
        }

        foreach (var index in definition["declared"].AsBsonArray.Select(i => i.AsBsonDocument))
        {
            indexes.Add(
                new BsonDocumentIndexKeysDefinition<BsonDocument>(index["keys"].AsBsonDocument),
                IndexOptions(index["options"].AsBsonDocument),
                Enum<IndexRebuild>(index, "onRebuild"));
        }
    }

    private static CreateIndexOptions<BsonDocument> IndexOptions(BsonDocument options)
    {
        var result = new CreateIndexOptions<BsonDocument>();
        foreach (var option in options)
        {
            var value = option.Value;
            if (value.IsBsonNull)
            {
                continue;
            }

            switch (option.Name)
            {
                case "name": result.Name = value.AsString; break;
                case "unique": result.Unique = value.ToBoolean(); break;
                case "sparse": result.Sparse = value.ToBoolean(); break;
                case "hidden": result.Hidden = value.ToBoolean(); break;
                case "expireAfterSeconds": result.ExpireAfter = TimeSpan.FromSeconds(value.ToDouble()); break;
                case "partialFilterExpression": result.PartialFilterExpression = new BsonDocumentFilterDefinition<BsonDocument>(value.AsBsonDocument); break;
                case "collation": result.Collation = MongoDB.Driver.Collation.FromBsonDocument(value.AsBsonDocument); break;
                case "weights": result.Weights = value.AsBsonDocument; break;
                case "default_language": result.DefaultLanguage = value.AsString; break;
                case "language_override": result.LanguageOverride = value.AsString; break;
                case "textIndexVersion": result.TextIndexVersion = value.ToInt32(); break;
                case "2dsphereIndexVersion": result.SphereIndexVersion = value.ToInt32(); break;
                case "bits": result.Bits = value.ToInt32(); break;
                case "min": result.Min = value.ToDouble(); break;
                case "max": result.Max = value.ToDouble(); break;
                case "wildcardProjection": result.WildcardProjection = new BsonDocumentProjectionDefinition<BsonDocument>(value.AsBsonDocument); break;
                case "storageEngine": result.StorageEngine = value.AsBsonDocument; break;
                default: throw new ArgumentException($"Unknown index option '{option.Name}'.");
            }
        }

        return result;
    }

    private static void SearchIndexes(SearchIndexesBuilder<BsonDocument> searchIndexes, BsonDocument definition)
    {
        searchIndexes.Prune = Enum<SearchIndexPrune>(definition, "prune");
        foreach (var pattern in definition["ignored"].AsBsonArray)
        {
            searchIndexes.Ignore(pattern.AsString);
        }

        foreach (var index in definition["declared"].AsBsonArray.Select(i => i.AsBsonDocument))
        {
            var name = Value(index, "name") is BsonString s ? s.Value : throw new ArgumentException("A search index needs a name.");
            var body = Value(index, "definition") as BsonDocument ?? throw new ArgumentException($"Search index '{name}' needs a definition object.");
            if (index["type"].AsString == "vectorSearch")
            {
                searchIndexes.AddVector(name, body);
            }
            else
            {
                searchIndexes.Add(name, body);
            }
        }
    }

    private static void Options(CollectionOptionsBuilder<BsonDocument> options, BsonDocument definition)
    {
        options.OnUnchangeable = Enum<UnchangeableOption>(definition, "onUnchangeable");
        options.OnConvertToCapped = Enum<ConvertToCapped>(definition, "onConvertToCapped");

        if (Value(definition, "capped") is BsonDocument capped)
        {
            var maxSize = Value(capped, "maxSize") ?? throw new ArgumentException("capped needs a maxSize (bytes).");
            options.Capped(maxSize.ToInt64(), Value(capped, "maxDocuments")?.ToInt64());
        }

        if (Value(definition, "timeSeries") is BsonDocument timeSeries)
        {
            var time = Value(timeSeries, "time") is BsonString t ? t.Value : throw new ArgumentException("timeSeries needs the time field's name.");
            var meta = Value(timeSeries, "meta")?.AsString;
            var span = Value(timeSeries, "bucketMaxSpan");
            var rounding = Value(timeSeries, "bucketRounding");
            if (span is not null || rounding is not null)
            {
                if (span is null || rounding is null || Value(timeSeries, "granularity") is not null)
                {
                    throw new ArgumentException("timeSeries takes a granularity, or both bucketMaxSpan and bucketRounding.");
                }

                options.TimeSeries(time, meta, TimeSpan.FromSeconds(span.ToDouble()), TimeSpan.FromSeconds(rounding.ToDouble()));
            }
            else
            {
                options.TimeSeries(time, meta, Enum<TimeSeriesGranularity>(timeSeries, "granularity"));
            }
        }

        if (Value(definition, "expireAfter") is { } expireAfter)
        {
            options.ExpireAfter(TimeSpan.FromSeconds(expireAfter.ToDouble()));
        }

        if (Value(definition, "clustered") is BsonDocument clustered)
        {
            options.Clustered(
                Value(clustered, "name")?.AsString,
                Value(clustered, "expireAfter") is { } after ? TimeSpan.FromSeconds(after.ToDouble()) : null);
        }

        if (Value(definition, "collation") is { } collation)
        {
            options.Collation(MongoDB.Driver.Collation.FromBsonDocument(collation.AsBsonDocument));
        }

        if (Value(definition, "changeStreamPreAndPostImages") is { } images)
        {
            options.ChangeStreamPreAndPostImages(images.ToBoolean());
        }
    }

    private static void Validator(ValidatorBuilder validator, BsonDocument definition)
    {
        if (Value(definition, "jsonSchema") is { } schema)
        {
            validator.JsonSchema(schema as BsonDocument ?? throw new ArgumentException("validator.jsonSchema needs an object."));
        }

        if (Enum<ValidationLevel>(definition, "level") is { } level)
        {
            validator.Level(level);
        }

        if (Enum<ValidationAction>(definition, "action") is { } action)
        {
            validator.Action(action);
        }
    }

    private static BsonValue? Value(BsonDocument document, string name) =>
        document.TryGetValue(name, out var value) && !value.IsBsonNull ? value : null;

    /// <summary>Script values are camelCase ('errorAndLog'); the C# enums are PascalCase.</summary>
    private static TEnum? Enum<TEnum>(BsonDocument document, string name)
        where TEnum : struct, Enum =>
        Value(document, name) is BsonString s
            ? System.Enum.TryParse<TEnum>(s.Value, ignoreCase: true, out var parsed) && !int.TryParse(s.Value, out _)
                ? parsed
                : throw new ArgumentException($"'{s.Value}' isn't a valid {name}.")
            : null;
}
