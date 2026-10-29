using System.Collections;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Kevsoft.MigrateMongo;

/// <summary>How prominent a <see cref="MigrationResults.Note(string, NoteLevel)"/> is.</summary>
public enum NoteLevel
{
    Info,
    Warning,
}

/// <summary>A line of text recorded with <see cref="MigrationResults.Note(string, NoteLevel)"/>.</summary>
public sealed record ResultNote(DateTime At, NoteLevel Level, string Message);

/// <summary>An operation run through <see cref="MigrationContext.Track{T}"/> or <see cref="MigrationContext.Track(IMongoDatabase, string?, bool)"/>.</summary>
public sealed class TrackedOperation
{
    /// <summary>e.g. <c>updateMany</c>, <c>deleteOne</c>, <c>insertMany</c>, <c>bulkWrite</c>, <c>createIndex</c>, <c>dropIndex</c>, <c>runCommand</c>.</summary>
    public required string Kind { get; init; }

    public string? Collection { get; init; }

    public string? Label { get; init; }

    /// <summary>
    /// What was sent: any of <c>filter</c>, <c>update</c>, <c>replacement</c>, <c>keys</c>, <c>index</c>, <c>indexes</c>,
    /// <c>requests</c>, <c>command</c> and <c>options</c>. Null when statements aren't captured or it was too large.
    /// </summary>
    public BsonDocument? Statement { get; init; }

    /// <summary>True when the statement was over <see cref="MigrationResults.MaxStatementBytes"/> and was dropped.</summary>
    public bool StatementTruncated { get; init; }

    /// <summary>
    /// What was applied. For a failed operation, only what the server reports as applied before the error (bulk writes
    /// and <c>insertMany</c>).
    /// </summary>
    public required IReadOnlyDictionary<string, long> Counts { get; init; }

    /// <summary>Why the operation failed (the exception's type and message), or null when it succeeded.</summary>
    public string? Error { get; init; }

    public long DurationMs { get; init; }

    public DateTime At { get; init; }
}

/// <summary>
/// Named values, notes and operations a migration records. They're stored with the migration's state and history entry,
/// printed by <c>up</c>/<c>down</c> and shown in full by <c>migrate-mongo show &lt;id&gt;</c>.
/// </summary>
/// <remarks>
/// Values are converted to BSON when they're set, so later changes to an object don't affect what's recorded. Keys are
/// 1–64 characters of letters, digits, <c>_</c> and <c>-</c>, so they're plain field names you can query.
/// </remarks>
public sealed partial class MigrationResults
{
    public const int MaxKeyLength = 64;
    public const int MaxDepth = 10;
    public const int MaxNotes = 100;
    public const int MaxNoteLength = 1024;
    public const int MaxOperations = 200;
    public const int MaxStatementBytes = 4 * 1024;
    public const int MaxErrorLength = 1024;

    private readonly BsonDocument _values = [];
    private readonly List<ResultNote> _notes = [];
    private readonly List<TrackedOperation> _operations = [];
    private readonly object _gate = new();
    private ILogger? _logger;

    /// <param name="captureStatements">Whether <see cref="MigrationContext.Track{T}"/> records the statements it runs.</param>
    /// <param name="time">The clock used to time-stamp notes and operations.</param>
    public MigrationResults(bool captureStatements = true, TimeProvider? time = null)
    {
        CaptureStatements = captureStatements;
        Time = time ?? TimeProvider.System;
    }

    /// <summary>Whether tracked operations record their statements (the <c>results.captureStatements</c> setting).</summary>
    public bool CaptureStatements { get; }

    internal TimeProvider Time { get; }

    /// <summary>The value stored under <paramref name="key"/> (a copy), or null if there isn't one.</summary>
    public BsonValue? this[string key]
    {
        get
        {
            lock (_gate)
            {
                return _values.TryGetValue(key, out var value) ? value.DeepClone() : null;
            }
        }
    }

    public IReadOnlyList<ResultNote> Notes
    {
        get
        {
            lock (_gate)
            {
                return [.. _notes];
            }
        }
    }

    /// <summary>Notes that weren't kept because there were already <see cref="MaxNotes"/>.</summary>
    public int NotesTruncated { get; private set; }

    public IReadOnlyList<TrackedOperation> Operations
    {
        get
        {
            lock (_gate)
            {
                return [.. _operations];
            }
        }
    }

    /// <summary>Operations that weren't recorded because there were already <see cref="MaxOperations"/>.</summary>
    public int OperationsTruncated { get; private set; }

    /// <summary>True when nothing has been recorded.</summary>
    public bool IsEmpty
    {
        get
        {
            lock (_gate)
            {
                return _values.ElementCount == 0 && _notes.Count == 0 && _operations.Count == 0 && NotesTruncated == 0 && OperationsTruncated == 0;
            }
        }
    }

    /// <summary>A copy of the named values, in the order they were first set.</summary>
    public BsonDocument ToBsonDocument()
    {
        lock (_gate)
        {
            return (BsonDocument)_values.DeepClone();
        }
    }

    public void Set(string key, string? value) => Store(key, value is null ? BsonNull.Value : new BsonString(value));

    public void Set(string key, int value) => Store(key, new BsonInt32(value));

    public void Set(string key, long value) => Store(key, new BsonInt64(value));

    public void Set(string key, double value) => Store(key, new BsonDouble(value));

    /// <summary>Stored as a <c>Decimal128</c>.</summary>
    public void Set(string key, decimal value) => Store(key, new BsonDecimal128(value));

    public void Set(string key, bool value) => Store(key, BsonBoolean.Create(value));

    /// <summary>Stored as UTC: local times are converted and unspecified times are taken to be UTC already.</summary>
    public void Set(string key, DateTime value) => Store(key, new BsonDateTime(ToUtc(value)));

    /// <summary>Stored as a standard (subtype 4) UUID.</summary>
    public void Set(string key, Guid value) => Store(key, new BsonBinaryData(value, GuidRepresentation.Standard));

    public void Set(string key, ObjectId value) => Store(key, new BsonObjectId(value));

    /// <summary>Stores any BSON value, e.g. a <see cref="BsonDocument"/> or <see cref="BsonArray"/> (a copy is stored).</summary>
    public void Set(string key, BsonValue? value) => Store(key, value?.DeepClone() ?? BsonNull.Value);

    /// <summary>Stores the items as an array, each serialised like <see cref="Set{T}(string, T)"/>.</summary>
    public void Set<T>(string key, IEnumerable<T>? values)
    {
        if (values is null)
        {
            Store(key, BsonNull.Value);
            return;
        }

        Store(key, new BsonArray(values.Select(v => Serialize(v))));
    }

    /// <summary>
    /// Stores an object (a record, class, anonymous type, list or dictionary) using the driver's serializer for it, so it
    /// looks the same as it would in a collection (class maps, <c>[BsonElement]</c> and conventions apply). Lists and
    /// arrays are stored item by item, so the <see cref="Guid"/>s and <see cref="DateTime"/>s in them are stored like
    /// <see cref="Set(string, Guid)"/> and <see cref="Set(string, DateTime)"/> store them.
    /// </summary>
    public void Set<T>(string key, T value) => Store(key, Serialize(value));

    /// <summary>Adds <paramref name="by"/> to a whole-number value (starting from 0) and returns the new total.</summary>
    public long Increment(string key, long by = 1)
    {
        ValidateKey(key);
        lock (_gate)
        {
            var current = 0L;
            if (_values.TryGetValue(key, out var existing))
            {
                current = existing switch
                {
                    BsonInt32 i => i.Value,
                    BsonInt64 l => l.Value,
                    _ => throw new InvalidOperationException($"Result '{key}' is a {existing.BsonType}, so it can't be incremented."),
                };
            }

            var total = checked(current + by);
            _values[key] = new BsonInt64(total);
            return total;
        }
    }

    public bool Remove(string key)
    {
        lock (_gate)
        {
            if (!_values.Contains(key))
            {
                return false;
            }

            _values.Remove(key);
            return true;
        }
    }

    /// <summary>
    /// Records a line of text (also written to the migration's logger, so it shows while the migration runs). Up to
    /// <see cref="MaxNotes"/> notes of <see cref="MaxNoteLength"/> characters are kept.
    /// </summary>
    public void Note(string message, NoteLevel level = NoteLevel.Info)
    {
        ArgumentNullException.ThrowIfNull(message);
        message = Truncate(message, MaxNoteLength);

        lock (_gate)
        {
            if (_notes.Count >= MaxNotes)
            {
                NotesTruncated++;
            }
            else
            {
                _notes.Add(new ResultNote(Time.GetUtcNow().UtcDateTime, level, message));
            }
        }

        if (_logger is { } logger)
        {
            if (level == NoteLevel.Warning)
            {
                LogWarningNote(logger, message);
            }
            else
            {
                LogInfoNote(logger, message);
            }
        }
    }

    /// <summary>Adds the counts to <c>results[label]</c> as <c>{ matched, modified, upserted }</c>. Unacknowledged writes are ignored.</summary>
    public void Track(UpdateResult result, string label)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsAcknowledged)
        {
            AddCounts(label, Counts(result));
        }
    }

    /// <summary>Adds the counts to <c>results[label]</c> as <c>{ matched, modified, upserted }</c>. Unacknowledged writes are ignored.</summary>
    public void Track(ReplaceOneResult result, string label)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsAcknowledged)
        {
            AddCounts(label, Counts(result));
        }
    }

    /// <summary>Adds the count to <c>results[label]</c> as <c>{ deleted }</c>. Unacknowledged writes are ignored.</summary>
    public void Track(DeleteResult result, string label)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsAcknowledged)
        {
            AddCounts(label, Counts(result));
        }
    }

    /// <summary>
    /// Adds the counts to <c>results[label]</c> as <c>{ inserted, matched, modified, deleted, upserted }</c>.
    /// Unacknowledged writes are ignored.
    /// </summary>
    public void Track<T>(BulkWriteResult<T> result, string label)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsAcknowledged)
        {
            AddCounts(label, Counts(result));
        }
    }

    internal void AttachLogger(ILogger logger) => _logger = logger;

    internal static Dictionary<string, long> Counts(UpdateResult result) => new()
    {
        ["matched"] = result.MatchedCount,
        ["modified"] = result.IsModifiedCountAvailable ? result.ModifiedCount : 0,
        ["upserted"] = result.UpsertedId is null ? 0 : 1,
    };

    internal static Dictionary<string, long> Counts(ReplaceOneResult result) => new()
    {
        ["matched"] = result.MatchedCount,
        ["modified"] = result.IsModifiedCountAvailable ? result.ModifiedCount : 0,
        ["upserted"] = result.UpsertedId is null ? 0 : 1,
    };

    internal static Dictionary<string, long> Counts(DeleteResult result) => new() { ["deleted"] = result.DeletedCount };

    internal static Dictionary<string, long> Counts<T>(BulkWriteResult<T> result) => new()
    {
        ["inserted"] = result.InsertedCount,
        ["matched"] = result.MatchedCount,
        ["modified"] = result.IsModifiedCountAvailable ? result.ModifiedCount : 0,
        ["deleted"] = result.DeletedCount,
        ["upserted"] = result.Upserts.Count,
    };

    /// <summary>Adds each count to the document under <paramref name="label"/>, creating it if needed.</summary>
    internal void AddCounts(string label, IReadOnlyDictionary<string, long> counts)
    {
        ValidateKey(label);
        lock (_gate)
        {
            if (!_values.TryGetValue(label, out var existing))
            {
                existing = new BsonDocument();
                _values[label] = existing;
            }
            else if (existing is not BsonDocument)
            {
                throw new InvalidOperationException($"Result '{label}' is a {existing.BsonType}, so counts can't be added to it.");
            }

            var document = (BsonDocument)existing;
            foreach (var (name, count) in counts)
            {
                var current = document.TryGetValue(name, out var value) && value.IsNumeric ? value.ToInt64() : 0;
                document[name] = new BsonInt64(current + count);
            }
        }
    }

    internal void Record(TrackedOperation operation)
    {
        lock (_gate)
        {
            if (_operations.Count >= MaxOperations)
            {
                OperationsTruncated++;
            }
            else
            {
                _operations.Add(operation);
            }
        }
    }

    /// <summary>The error recorded for a failed operation: the exception's type and message, cut to <see cref="MaxErrorLength"/>.</summary>
    internal static string DescribeError(Exception exception)
    {
        // Generic exceptions (MongoBulkWriteException`1) are named without their arity.
        var type = exception.GetType().Name;
        var arity = type.IndexOf('`', StringComparison.Ordinal);
        return Truncate($"{(arity < 0 ? type : type[..arity])}: {exception.Message}", MaxErrorLength);
    }

    /// <summary>Throws if <paramref name="key"/> isn't a valid result key.</summary>
    internal static void ValidateKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!KeyPattern().IsMatch(key))
        {
            throw new ArgumentException(
                $"Result key '{key}' is invalid: use 1–{MaxKeyLength} letters, digits, '_' or '-'.", nameof(key));
        }
    }

    private void Store(string key, BsonValue value)
    {
        ValidateKey(key);
        Validate(key, value, 1);
        lock (_gate)
        {
            _values[key] = value;
        }
    }

    private static void Validate(string path, BsonValue value, int depth)
    {
        switch (value)
        {
            case BsonDocument document:
                if (depth > MaxDepth)
                {
                    throw new ArgumentException($"Result '{path}' is nested more than {MaxDepth} levels deep.");
                }

                foreach (var element in document)
                {
                    if (element.Name.Length == 0 || element.Name.StartsWith('$') || element.Name.Contains('.', StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Result '{path}' has the field name '{element.Name}'; field names can't be empty, start with '$' or contain '.'.");
                    }

                    Validate($"{path}.{element.Name}", element.Value, depth + 1);
                }

                break;
            case BsonArray array:
                if (depth > MaxDepth)
                {
                    throw new ArgumentException($"Result '{path}' is nested more than {MaxDepth} levels deep.");
                }

                for (var i = 0; i < array.Count; i++)
                {
                    Validate($"{path}.{i}", array[i], depth + 1);
                }

                break;
        }
    }

    private static BsonValue Serialize<T>(T value)
    {
        switch (value)
        {
            case null:
                return BsonNull.Value;
            case BsonValue bson:
                return bson.DeepClone();
            case DateTime dateTime:
                return new BsonDateTime(ToUtc(dateTime));
            case Guid guid:
                return new BsonBinaryData(guid, GuidRepresentation.Standard);
        }

        // Interfaces and object say little about the value, so use its actual type (e.g. an anonymous type), and turn
        // sequences such as LINQ queries into arrays.
        var loose = typeof(T) == typeof(object) || typeof(T).IsInterface;
        if (loose && value is IEnumerable items and not string and not IDictionary)
        {
            return new BsonArray(items.Cast<object?>().Select(Serialize));
        }

        var serializer = loose ? BsonSerializer.LookupSerializer(value.GetType()) : BsonSerializer.LookupSerializer<T>();

        // Lists and arrays are stored item by item, so Guids and dates in them are stored like single values. The driver's
        // own Guid serializer throws unless a representation has been configured.
        if (serializer is IBsonArraySerializer and not IBsonDictionarySerializer && value is IEnumerable sequence)
        {
            return new BsonArray(sequence.Cast<object?>().Select(Serialize));
        }

        var wrapper = new BsonDocument();
        using (var writer = new BsonDocumentWriter(wrapper))
        {
            writer.WriteStartDocument();
            writer.WriteName("v");
            serializer.Serialize(BsonSerializationContext.CreateRoot(writer), new BsonSerializationArgs(serializer.ValueType, false, false), value);
            writer.WriteEndDocument();
        }

        return wrapper["v"];
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length > maxLength ? text[..(maxLength - 1)] + "…" : text;

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex KeyPattern();

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "{Note}")]
    private static partial void LogInfoNote(ILogger logger, string note);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "{Note}")]
    private static partial void LogWarningNote(ILogger logger, string note);
}
