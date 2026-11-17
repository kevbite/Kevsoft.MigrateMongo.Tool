using System.Globalization;
using System.Runtime.ExceptionServices;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace Kevsoft.MigrateMongo.Scripting;

/// <summary>A BSON value JavaScript has no type for (ObjectId, Decimal128, Long, UUID, ...), carried through as-is.</summary>
internal sealed class BsonBox(Engine engine, BsonValue value) : ObjectInstance(engine)
{
    public BsonValue Value { get; } = value;
}

/// <summary>
/// One Jint engine with the <c>@kevsoft/migrate-mongo</c> module. The bridge is reflection-free so it works under Native
/// AOT: every native function is a <see cref="ClrFunction"/>, every value is converted by hand, and every driver call is
/// <see cref="BsonDocument"/>-typed. Without a <see cref="MigrationContext"/> it can only load files (to discover
/// migrations and read collection definitions); with one, database calls run through the context, so they're tracked,
/// use its session and show up in <c>show</c>.
/// </summary>
internal sealed class ScriptHost : IDisposable
{
    private static readonly JsonWriterSettings LogJson = new() { OutputMode = JsonOutputMode.RelaxedExtendedJson, Indent = false };

    private readonly Engine _engine;
    private readonly string _root;
    private readonly MigrationContext? _context;
    private readonly CancellationToken _cancellation;
    private readonly List<(Task Task, Action Settle)> _pending = [];
    private readonly Dictionary<int, IAsyncCursor<BsonDocument>> _cursors = [];
    private readonly Dictionary<JsValue, Exception> _driverErrors = new(ReferenceEqualityComparer.Instance);
    private readonly JsObject _boxPrototype;
    private readonly JsValue _settle;
    private readonly JsValue _makeError;
    private readonly JsValue _makeDate;
    private readonly JsValue _setPrototype;
    private ObjectInstance? _runtime;
    private Task _last = Task.CompletedTask;
    private int _nextCursor;

    public ScriptHost(string root, MigrationContext? context, CancellationToken cancellation)
    {
        _root = Path.GetFullPath(root);
        _context = context;
        _cancellation = cancellation;
        _engine = new Engine(o =>
        {
            o.EnableModules(_root, restrictToBasePath: true);
            o.CancellationToken(cancellation);
        });

        _settle = _engine.Evaluate("(p, ok, fail) => { Promise.resolve(p).then(ok, fail); }");
        _makeError = _engine.Evaluate("(message, code, codeName) => { const e = new Error(message); if (code !== undefined) { e.code = code; e.codeName = codeName; } return e; }");
        _makeDate = _engine.Evaluate("ms => new Date(ms)");
        _setPrototype = _engine.Evaluate("(o, p) => Object.setPrototypeOf(o, p)");
        _boxPrototype = BoxPrototype();

        _engine.SetValue("__mm", Natives());
        _engine.SetValue("console", Logger());
        _engine.Modules.Add(ScriptModule.Name, ScriptModule.Runtime);
    }

    public ObjectInstance Runtime => _runtime ??= _engine.Modules.Import(ScriptModule.Name);

    /// <summary>Imports a file under the root, given its path relative to the root with forward slashes.</summary>
    public ObjectInstance Import(string file)
    {
        var source = File.ReadAllText(Path.Combine(_root, file));
        return Run(file, () =>
        {
            if (ScriptSource.IsCommonJs(source, file))
            {
                var specifier = $"(commonjs)/{file}";
                _engine.Modules.Add(specifier, ScriptSource.WrapCommonJs(source));
                return _engine.Modules.Import(specifier);
            }

            return _engine.Modules.Import("./" + file);
        });
    }

    /// <summary>Calls <paramref name="function"/> and runs the event loop until the promise it returns settles.</summary>
    public JsValue CallAndWait(string file, JsValue function, params JsValue[] arguments) =>
        Run(file, () => Await(_engine.Invoke(function, JsValue.Undefined, arguments)));

    public JsValue Call(JsValue function, params JsValue[] arguments) => _engine.Invoke(function, JsValue.Undefined, arguments);

    public JsValue CreateDb() => Call(Runtime.Get("createDb"));

    public ObjectInstance CreateContext(string id, string name)
    {
        var context = _context ?? throw new InvalidOperationException("No migration context.");
        var results = new JsObject(_engine);
        Fn(results, "set", a =>
        {
            context.Results.Set(Key(a), ToBson(Arg(a, 1)));
            return JsValue.Undefined;
        });
        Fn(results, "get", a => context.Results[Key(a)] is { } value ? FromBson(value) : JsValue.Undefined);
        Fn(results, "increment", a => new JsNumber(context.Results.Increment(Key(a), Arg(a, 1).IsUndefined() ? 1 : (long)Arg(a, 1).AsNumber())));
        Fn(results, "remove", a => context.Results.Remove(Key(a)) ? JsBoolean.True : JsBoolean.False);
        Fn(results, "note", a =>
        {
            var level = Arg(a, 1).IsUndefined() ? "info" : Arg(a, 1).ToString();
            context.Results.Note(Arg(a, 0).ToString(), level switch
            {
                "info" => NoteLevel.Info,
                "warning" or "warn" => NoteLevel.Warning,
                _ => throw new JavaScriptException(_engine.Intrinsics.TypeError, $"A note's level must be 'info' or 'warning', not '{level}'."),
            });
            return JsValue.Undefined;
        });

        var run = new JsObject(_engine);
        run.Set("id", context.Run.RunId);
        run.Set("batch", context.Run.Batch);
        run.Set("direction", context.Run.Direction == MigrationDirection.Up ? "up" : "down");
        run.Set("transactional", context.Run.Transactional ? JsBoolean.True : JsBoolean.False);
        run.Set("environment", context.Run.Environment is null ? JsValue.Null : new JsString(context.Run.Environment));

        var migration = new JsObject(_engine);
        migration.Set("id", id);
        migration.Set("name", name);

        var result = new JsObject(_engine);
        result.Set("results", results);
        result.Set("logger", Logger());
        result.Set("run", run);
        result.Set("migration", migration);
        return result;

        string Key(JsValue[] a) => Arg(a, 0).IsString() ? a[0].AsString() : throw new JavaScriptException(_engine.Intrinsics.TypeError, "A result key must be a string.");
    }

    public void Dispose()
    {
        foreach (var cursor in _cursors.Values)
        {
            cursor.Dispose();
        }

        _cursors.Clear();
        _engine.Dispose();
    }

    /// <summary>Runs the event loop until <paramref name="value"/> (usually a promise) settles, and returns what it settled with.</summary>
    private JsValue Await(JsValue value)
    {
        var done = false;
        var result = JsValue.Undefined;
        JsValue? error = null;
        var ok = new ClrFunction(_engine, "ok", (_, a) => { done = true; result = Arg(a, 0); return JsValue.Undefined; });
        var fail = new ClrFunction(_engine, "fail", (_, a) => { done = true; error = Arg(a, 0); return JsValue.Undefined; });
        _engine.Invoke(_settle, JsValue.Undefined, [value, ok, fail]);
        _engine.Advanced.ProcessTasks();

        while (!done)
        {
            if (_pending.Count == 0)
            {
                throw new JavaScriptException(_engine.Intrinsics.Error,
                    "The script is waiting for something that will never finish. Only database calls can be awaited.");
            }

            var finished = Task.WhenAny(_pending.Select(p => p.Task)).GetAwaiter().GetResult();
            var index = _pending.FindIndex(p => p.Task == finished);
            var pending = _pending[index];
            _pending.RemoveAt(index);
            pending.Settle();
            _engine.Advanced.ProcessTasks();
        }

        // A call that wasn't awaited must still finish before a transaction commits, and its failure can't go unnoticed.
        try
        {
            _last.GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ScriptException($"A database call that wasn't awaited failed: {e.Message}", e);
        }

        _cancellation.ThrowIfCancellationRequested();
        if (error is not null)
        {
            if (_driverErrors.TryGetValue(error, out var original))
            {
                // Rethrown as it is, so the runner still sees e.g. a transient transaction error and retries.
                ExceptionDispatchInfo.Capture(original).Throw();
            }

            throw new JavaScriptException(error);
        }

        return result;
    }

    private T Run<T>(string file, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (JavaScriptException e)
        {
            throw new ScriptException(Describe(file, e), e);
        }
        catch (ExecutionCanceledException)
        {
            throw new OperationCanceledException(_cancellation);
        }
        catch (JintException e)
        {
            throw new ScriptException($"{file}: {e.Message}", e);
        }
        catch (Acornima.ParseErrorException e)
        {
            throw new ScriptException($"{RelativeLocation(e.SourceFile ?? file)}:{e.LineNumber}:{e.Column}: {e.Description}", e);
        }
    }

    private string Describe(string file, JavaScriptException e)
    {
        var stack = e.JavaScriptStackTrace;
        var message = e.Error is ObjectInstance o && o.Get("message").IsString() && o.Get("name").IsString()
            ? $"{o.Get("name")}: {o.Get("message")}"
            : e.Message;
        if (string.IsNullOrWhiteSpace(stack))
        {
            var location = e.Location.SourceFile is { Length: > 0 } source ? $"{RelativeLocation(source)}:{e.Location.Start.Line}" : file;
            return $"{location}: {message}";
        }

        return $"{file}: {message}{Environment.NewLine}{RelativeLocation(stack.TrimEnd())}";
    }

    /// <summary>Shortens the absolute file URIs Jint reports to paths relative to the root.</summary>
    private string RelativeLocation(string text)
    {
        var rootUri = new Uri(_root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar).AbsoluteUri;
        var relative = text.Replace(rootUri, "", StringComparison.OrdinalIgnoreCase)
            .Replace(_root + Path.DirectorySeparatorChar, "", StringComparison.OrdinalIgnoreCase);
        return Path.DirectorySeparatorChar == '\\' ? relative.Replace('\\', '/') : relative;
    }

    private void Method(ObjectInstance target, string name, Func<JsValue, JsValue[], JsValue> body) =>
        target.Set(name, new ClrFunction(_engine, name, (self, a) => Guard(name, () => body(self, a))));

    private static JsValue Arg(JsValue[] arguments, int index) => arguments.Length > index ? arguments[index] : JsValue.Undefined;

    private void Fn(ObjectInstance target, string name, Func<JsValue[], JsValue> body) =>
        target.Set(name, new ClrFunction(_engine, name, (_, a) => Guard(name, () => body(a))));

    /// <summary>.NET exceptions from a native function become JavaScript errors the script can catch.</summary>
    private JsValue Guard(string name, Func<JsValue> body)
    {
        try
        {
            return body();
        }
        catch (Exception e) when (e is not (JavaScriptException or JintException or OperationCanceledException))
        {
            throw new JavaScriptException(Error(e));
        }
    }

    private JsValue Error(Exception e)
    {
        var (code, codeName) = e is MongoCommandException command ? ((JsValue)new JsNumber(command.Code), (JsValue)new JsString(command.CodeName ?? "")) : (JsValue.Undefined, JsValue.Undefined);
        var error = _engine.Invoke(_makeError, JsValue.Undefined, [new JsString(e.Message), code, codeName]);
        if (e is MongoException)
        {
            _driverErrors[error] = e;
        }

        return error;
    }

    private JsObject Logger()
    {
        var logger = new JsObject(_engine);
        foreach (var (name, level) in new[]
                 {
                     ("log", LogLevel.Information), ("info", LogLevel.Information), ("debug", LogLevel.Debug),
                     ("warn", LogLevel.Warning), ("error", LogLevel.Error),
                 })
        {
            Fn(logger, name, a =>
            {
                if (_context is { } context && context.Logger.IsEnabled(level))
                {
                    var text = string.Join(" ", a.Select(Format));
                    Log.Message(context.Logger, level, text);
                }
                return JsValue.Undefined;
            });
        }

        return logger;
    }

    private string Format(JsValue value)
    {
        if (value.IsString())
        {
            return value.AsString();
        }

        if (value is ObjectInstance o && o.Get("stack") is { } stack && stack.IsString() && value is not BsonBox)
        {
            // Jint's stack has only the frames, where V8's starts with "Name: message".
            return $"{TypeConverter.ToString(o.Get("name"))}: {TypeConverter.ToString(o.Get("message"))}\n{RelativeLocation(stack.AsString().TrimEnd())}";
        }

        try
        {
            return ToBson(value) switch
            {
                BsonString s => s.Value,
                var bson => bson.ToJson(LogJson),
            };
        }
        catch (JavaScriptException)
        {
            return value.ToString();
        }
    }

    // ---- BSON values ----

    private JsObject BoxPrototype()
    {
        var prototype = new JsObject(_engine);
        Method(prototype, "toString", (self, _) => new JsString(Describe(Unbox(self))));
        Method(prototype, "toJSON", (self, _) => new JsString(Describe(Unbox(self))));
        Method(prototype, "toHexString", (self, _) => new JsString(Unbox(self).AsObjectId.ToString()));
        Method(prototype, "getTimestamp", (self, _) => _engine.Invoke(_makeDate, JsValue.Undefined,
            [new JsNumber(new DateTimeOffset(Unbox(self).AsObjectId.CreationTime).ToUnixTimeMilliseconds())]));
        Method(prototype, "toNumber", (self, _) => Unbox(self) switch
        {
            BsonInt64 l => new JsNumber(l.Value),
            BsonDecimal128 d => new JsNumber((double)d.Value),
            var other => throw new JavaScriptException(_engine.Intrinsics.TypeError, $"A {other.BsonType} has no number value."),
        });
        Method(prototype, "equals", (self, a) => a.Length > 0 && a[0] is BsonBox other && other.Value.Equals(Unbox(self))
            ? JsBoolean.True
            : JsBoolean.False);
        return prototype;

        static string Describe(BsonValue value) => value switch
        {
            BsonBinaryData { SubType: BsonBinarySubType.UuidStandard } uuid => uuid.ToGuid(GuidRepresentation.Standard).ToString(),
            BsonBinaryData binary => Convert.ToBase64String(binary.Bytes),
            BsonTimestamp timestamp => $"Timestamp({timestamp.Timestamp}, {timestamp.Increment})",
            _ => value.ToString() ?? "",
        };
    }

    private BsonValue Unbox(JsValue self) =>
        self is BsonBox box ? box.Value : throw new JavaScriptException(_engine.Intrinsics.TypeError, "Not a BSON value.");

    private BsonBox Box(BsonValue value)
    {
        var box = new BsonBox(_engine, value);
        _engine.Invoke(_setPrototype, JsValue.Undefined, [box, _boxPrototype]);
        return box;
    }

    /// <summary>
    /// JavaScript to BSON: whole numbers in the 32-bit range become Int32 and other numbers Double, as the Node.js driver
    /// stores them; BigInt becomes Int64, Date a UTC date, RegExp a regular expression; <c>undefined</c> properties are left out.
    /// </summary>
    public BsonValue ToBson(JsValue value) => ToBson(value, 0);

    private BsonValue ToBson(JsValue value, int depth)
    {
        if (depth > 100)
        {
            throw new JavaScriptException(_engine.Intrinsics.TypeError, "The value is nested too deeply (or refers to itself) to store in MongoDB.");
        }

        switch (value)
        {
            case BsonBox box:
                return box.Value;
            case JsDate date:
                return double.IsNaN(date.DateValue)
                    ? throw new JavaScriptException(_engine.Intrinsics.TypeError, "An invalid Date can't be stored in MongoDB.")
                    : new BsonDateTime((long)date.DateValue);
            case JsRegExp regex:
                return new BsonRegularExpression(regex.Source, regex.Flags);
        }

        if (value.IsUndefined() || value.IsNull())
        {
            return BsonNull.Value;
        }

        if (value.IsBoolean())
        {
            return (BsonBoolean)value.AsBoolean();
        }

        if (value.IsString())
        {
            return new BsonString(value.AsString());
        }

        if (value.IsNumber())
        {
            var d = value.AsNumber();
            return d % 1 == 0 && d is >= int.MinValue and <= int.MaxValue && !(d == 0 && double.IsNegative(d))
                ? new BsonInt32((int)d)
                : new BsonDouble(d);
        }

        if (value.IsBigInt())
        {
            return new BsonInt64((long)TypeConverter.ToBigInt(value));
        }

        if (value.IsArray())
        {
            var array = value.AsArray();
            var result = new BsonArray();
            for (uint i = 0; i < array.Length; i++)
            {
                result.Add(ToBson(array.Get(i), depth + 1));
            }

            return result;
        }

        if (value is ObjectInstance o && !value.IsCallable())
        {
            var document = new BsonDocument();
            foreach (var key in o.GetOwnPropertyKeys(Types.String))
            {
                if (o.GetOwnProperty(key).Enumerable && o.Get(key) is var item && !item.IsUndefined())
                {
                    document[key.ToString()] = ToBson(item, depth + 1);
                }
            }

            return document;
        }

        throw new JavaScriptException(_engine.Intrinsics.TypeError, $"{(value.IsCallable() ? "A function" : value.ToString())} can't be stored in MongoDB.");
    }

    /// <summary>BSON to JavaScript, as the Node.js driver returns it: Int64 becomes a number, dates a Date, and BSON types JavaScript has no equivalent for keep their type.</summary>
    public JsValue FromBson(BsonValue value) => value.BsonType switch
    {
        BsonType.Document => FromDocument(value.AsBsonDocument),
        BsonType.Array => new JsArray(_engine, value.AsBsonArray.Select(FromBson).ToArray()),
        BsonType.Int32 => new JsNumber(value.AsInt32),
        BsonType.Int64 => new JsNumber(value.AsInt64),
        BsonType.Double => new JsNumber(value.AsDouble),
        BsonType.String => new JsString(value.AsString),
        BsonType.Boolean => value.AsBoolean ? JsBoolean.True : JsBoolean.False,
        BsonType.Null or BsonType.Undefined => JsValue.Null,
        BsonType.DateTime => _engine.Invoke(_makeDate, JsValue.Undefined, [new JsNumber(value.AsBsonDateTime.MillisecondsSinceEpoch)]),
        _ => Box(value),
    };

    private JsObject FromDocument(BsonDocument document)
    {
        var o = new JsObject(_engine);
        foreach (var element in document)
        {
            o.Set(element.Name, FromBson(element.Value));
        }

        return o;
    }

    private BsonDocument Document(JsValue value, string what) => value.IsUndefined() || value.IsNull()
        ? []
        : ToBson(value) as BsonDocument ?? throw new JavaScriptException(_engine.Intrinsics.TypeError, $"{what} must be an object.");

    private List<BsonDocument> Documents(JsValue value, string what) =>
        ToBson(value) is BsonArray array && array.All(d => d is BsonDocument)
            ? array.Select(d => d.AsBsonDocument).ToList()
            : throw new JavaScriptException(_engine.Intrinsics.TypeError, $"{what} must be an array of objects.");

    // ---- Natives ----

    private JsObject Natives()
    {
        var n = new JsObject(_engine);
        Fn(n, "objectId", a => Box(Arg(a, 0).IsNull() ? ObjectId.GenerateNewId() : ObjectId.Parse(a[0].AsString())));
        Fn(n, "objectIdFromTime", a => Box(ObjectId.GenerateNewId((int)Arg(a, 0).AsNumber())));
        Fn(n, "decimal128", a => Box(new BsonDecimal128(Decimal128.Parse(Arg(a, 0).AsString()))));
        Fn(n, "long", a => Box(new BsonInt64(long.Parse(Arg(a, 0).AsString(), NumberStyles.Integer, CultureInfo.InvariantCulture))));
        Fn(n, "uuid", a => Box(new BsonBinaryData(Arg(a, 0).IsNull() ? Guid.NewGuid() : Guid.Parse(a[0].AsString()), GuidRepresentation.Standard)));
        Fn(n, "timestamp", a => Box(new BsonTimestamp((int)Arg(a, 0).AsNumber(), (int)Arg(a, 1).AsNumber())));

        if (_context is not null)
        {
            AddDatabaseNatives(n, _context);
        }

        return n;
    }

    private void AddDatabaseNatives(JsObject n, MigrationContext context)
    {
        var database = context.Database;
        n.Set("databaseName", database.DatabaseNamespace.DatabaseName);

        Fn(n, "write", a =>
        {
            var kind = Arg(a, 0).AsString();
            var tracked = Track(context, Arg(a, 1).AsString(), Arg(a, 2));
            return Write(kind, tracked, Arg(a, 3), Arg(a, 4), Arg(a, 5));
        });

        Fn(n, "command", a =>
        {
            var command = Document(Arg(a, 0), "The command");
            var tracked = Track(context, database, Arg(a, 1));
            return Async(() => tracked.RunCommandAsync(command, cancellationToken: _cancellation), FromBson);
        });

        Fn(n, "adminCommand", a =>
        {
            var command = Document(Arg(a, 0), "The command");
            var tracked = Track(context, database.Client.GetDatabase("admin"), Arg(a, 1));
            return Async(() => tracked.RunCommandAsync(command, cancellationToken: _cancellation), FromBson);
        });

        Fn(n, "openCursor", a => OpenCursor(context, Arg(a, 0).AsString(), Arg(a, 1), Arg(a, 2), Arg(a, 3)));

        Fn(n, "nextBatch", a =>
        {
            var handle = (int)Arg(a, 0).AsNumber();
            if (!_cursors.TryGetValue(handle, out var cursor))
            {
                throw new JavaScriptException(_engine.Intrinsics.Error, "The cursor is closed.");
            }

            return Async(async () => await cursor.MoveNextAsync(_cancellation) ? cursor.Current.ToList() : null, batch =>
            {
                if (batch is null)
                {
                    CloseCursor(handle);
                    return JsValue.Null;
                }

                return new JsArray(_engine, batch.Select(d => FromBson(d)).ToArray());
            });
        });

        Fn(n, "closeCursor", a =>
        {
            CloseCursor((int)Arg(a, 0).AsNumber());
            return JsValue.Undefined;
        });

        Fn(n, "count", a =>
        {
            var collection = Collection(context, Arg(a, 0));
            var filter = Document(Arg(a, 1), "The filter");
            var options = Document(Arg(a, 2), "The options");
            var countOptions = new CountOptions
            {
                Limit = options.TryGetValue("limit", out var limit) ? limit.ToInt64() : null,
                Skip = options.TryGetValue("skip", out var skip) ? skip.ToInt64() : null,
                Hint = options.GetValue("hint", null),
                Collation = Collation(options),
            };
            return Async(() => context.Session is { } session
                ? collection.CountDocumentsAsync(session, filter, countOptions, _cancellation)
                : collection.CountDocumentsAsync(filter, countOptions, _cancellation), count => new JsNumber(count));
        });

        Fn(n, "estimatedCount", a =>
        {
            var collection = Collection(context, Arg(a, 0));
            return Async(() => collection.EstimatedDocumentCountAsync(null, _cancellation), count => new JsNumber(count));
        });

        Fn(n, "distinct", a =>
        {
            var collection = Collection(context, Arg(a, 0));
            var field = Arg(a, 1).IsString() ? a[1].AsString() : throw new JavaScriptException(_engine.Intrinsics.TypeError, "distinct needs a field name.");
            var filter = Document(Arg(a, 2), "The filter");
            return Async(async () =>
            {
                using var cursor = context.Session is { } session
                    ? await collection.DistinctAsync<BsonValue>(session, field, filter, null, _cancellation)
                    : await collection.DistinctAsync<BsonValue>(field, filter, null, _cancellation);
                return await cursor.ToListAsync(_cancellation);
            }, values => new JsArray(_engine, values.Select(FromBson).ToArray()));
        });
    }

    private static IMongoCollection<BsonDocument> Collection(MigrationContext context, JsValue name) =>
        context.Database.GetCollection<BsonDocument>(name.AsString());

    private static TrackedCollection<BsonDocument> Track(MigrationContext context, string name, JsValue options)
    {
        var (label, capture) = Tracking(options);
        return context.Track(context.Database.GetCollection<BsonDocument>(name), label, capture);
    }

    private static TrackedDatabase Track(MigrationContext context, IMongoDatabase database, JsValue options)
    {
        var (label, capture) = Tracking(options);
        return context.Track(database, label, capture);
    }

    private static (string? Label, bool Capture) Tracking(JsValue options)
    {
        if (options is not ObjectInstance o)
        {
            return (null, true);
        }

        var label = o.Get("label");
        var capture = o.Get("captureStatement");
        return (label.IsUndefined() || label.IsNull() ? null : label.ToString(), capture.IsUndefined() || capture.AsBoolean());
    }

    private static Collation? Collation(BsonDocument options) =>
        options.TryGetValue("collation", out var collation) ? MongoDB.Driver.Collation.FromBsonDocument(collation.AsBsonDocument) : null;

    private JsValue Write(string kind, TrackedCollection<BsonDocument> tracked, JsValue a, JsValue b, JsValue c)
    {
        switch (kind)
        {
            case "insertOne":
                {
                    var document = Document(a, "The document");
                    var id = EnsureId(document);
                    return Async(async () =>
                    {
                        await tracked.InsertOneAsync(document, null, _cancellation);
                        return id;
                    }, inserted => Result(("acknowledged", JsBoolean.True), ("insertedId", FromBson(inserted))));
                }

            case "insertMany":
                {
                    var documents = Documents(a, "The documents");
                    var ids = documents.Select(EnsureId).ToList();
                    var options = Document(b, "The options");
                    var insertOptions = new InsertManyOptions { IsOrdered = options.GetValue("ordered", true).ToBoolean() };
                    return Async(async () =>
                    {
                        await tracked.InsertManyAsync(documents, insertOptions, _cancellation);
                        return ids;
                    }, inserted =>
                    {
                        var insertedIds = new JsObject(_engine);
                        for (var i = 0; i < inserted.Count; i++)
                        {
                            insertedIds.Set(i.ToString(CultureInfo.InvariantCulture), FromBson(inserted[i]));
                        }

                        return Result(("acknowledged", JsBoolean.True), ("insertedCount", new JsNumber(inserted.Count)), ("insertedIds", insertedIds));
                    });
                }

            case "updateOne" or "updateMany":
                {
                    var filter = new BsonDocumentFilterDefinition<BsonDocument>(Document(a, "The filter"));
                    var update = Update(b);
                    var options = Document(c, "The options");
                    var updateOptions = new UpdateOptions
                    {
                        IsUpsert = options.GetValue("upsert", false).ToBoolean(),
                        Hint = options.GetValue("hint", null),
                        Collation = Collation(options),
                        ArrayFilters = options.TryGetValue("arrayFilters", out var arrayFilters)
                            ? arrayFilters.AsBsonArray.Select(f => (ArrayFilterDefinition)new BsonDocumentArrayFilterDefinition<BsonDocument>(f.AsBsonDocument)).ToList()
                            : null,
                    };
                    return Async(
                        () => kind == "updateOne"
                            ? tracked.UpdateOneAsync(filter, update, updateOptions, _cancellation)
                            : tracked.UpdateManyAsync(filter, update, updateOptions, _cancellation),
                        UpdateResult);
                }

            case "replaceOne":
                {
                    var filter = new BsonDocumentFilterDefinition<BsonDocument>(Document(a, "The filter"));
                    var replacement = Document(b, "The replacement");
                    var options = Document(c, "The options");
                    var replaceOptions = new ReplaceOptions
                    {
                        IsUpsert = options.GetValue("upsert", false).ToBoolean(),
                        Hint = options.GetValue("hint", null),
                        Collation = Collation(options),
                    };
                    return Async(() => tracked.ReplaceOneAsync(filter, replacement, replaceOptions, _cancellation), r => r.IsAcknowledged
                        ? Result(
                            ("acknowledged", JsBoolean.True),
                            ("matchedCount", new JsNumber(r.MatchedCount)),
                            ("modifiedCount", new JsNumber(r.IsModifiedCountAvailable ? r.ModifiedCount : 0)),
                            ("upsertedCount", new JsNumber(r.UpsertedId is null ? 0 : 1)),
                            ("upsertedId", r.UpsertedId is null ? JsValue.Null : FromBson(r.UpsertedId)))
                        : Result(("acknowledged", JsBoolean.False)));
                }

            case "deleteOne" or "deleteMany":
                {
                    var filter = new BsonDocumentFilterDefinition<BsonDocument>(Document(a, "The filter"));
                    var options = Document(b, "The options");
                    var deleteOptions = new DeleteOptions { Hint = options.GetValue("hint", null), Collation = Collation(options) };
                    return Async(
                        () => kind == "deleteOne"
                            ? tracked.DeleteOneAsync(filter, deleteOptions, _cancellation)
                            : tracked.DeleteManyAsync(filter, deleteOptions, _cancellation),
                        r => r.IsAcknowledged
                            ? Result(("acknowledged", JsBoolean.True), ("deletedCount", new JsNumber(r.DeletedCount)))
                            : Result(("acknowledged", JsBoolean.False)));
                }

            case "bulkWrite":
                {
                    var requests = (ToBson(a) as BsonArray ?? throw new JavaScriptException(_engine.Intrinsics.TypeError, "bulkWrite needs an array of operations."))
                        .Select(WriteModel)
                        .ToList();
                    var options = Document(b, "The options");
                    var bulkOptions = new BulkWriteOptions { IsOrdered = options.GetValue("ordered", true).ToBoolean() };
                    return Async(() => tracked.BulkWriteAsync(requests, bulkOptions, _cancellation), r => r.IsAcknowledged
                        ? Result(
                            ("acknowledged", JsBoolean.True),
                            ("insertedCount", new JsNumber(r.InsertedCount)),
                            ("matchedCount", new JsNumber(r.MatchedCount)),
                            ("modifiedCount", new JsNumber(r.IsModifiedCountAvailable ? r.ModifiedCount : 0)),
                            ("deletedCount", new JsNumber(r.DeletedCount)),
                            ("upsertedCount", new JsNumber(r.Upserts.Count)))
                        : Result(("acknowledged", JsBoolean.False)));
                }

            case "createIndex":
                {
                    var keys = Document(a, "The index keys");
                    if (keys.ElementCount == 0)
                    {
                        throw new JavaScriptException(_engine.Intrinsics.TypeError, "createIndex needs the index keys, e.g. { email: 1 }.");
                    }

                    var options = Document(b, "The options");
                    var name = options.GetValue("name", null)?.AsString ?? string.Join("_", keys.Select(k => $"{k.Name}_{k.Value}"));
                    var index = new BsonDocument { { "key", keys }, { "name", name } };
                    index.AddRange(options.Where(e => e.Name != "name"));
                    return Async(() => tracked.CreateIndexAsync(index, _cancellation), _ => new JsString(name));
                }

            case "dropIndex":
                {
                    var name = a.IsString() ? a.AsString() : throw new JavaScriptException(_engine.Intrinsics.TypeError, "dropIndex needs the index name.");
                    return Async(async () =>
                    {
                        await tracked.DropIndexAsync(name, cancellationToken: _cancellation);
                        return true;
                    }, _ => JsValue.Undefined);
                }

            case "dropIndexes":
                return Async(async () =>
                {
                    await tracked.DropAllIndexesAsync(_cancellation);
                    return true;
                }, _ => JsValue.Undefined);

            default:
                throw new JavaScriptException(_engine.Intrinsics.TypeError, $"Unknown write '{kind}'.");
        }
    }

    private JsObject UpdateResult(UpdateResult r) => r.IsAcknowledged
        ? Result(
            ("acknowledged", JsBoolean.True),
            ("matchedCount", new JsNumber(r.MatchedCount)),
            ("modifiedCount", new JsNumber(r.IsModifiedCountAvailable ? r.ModifiedCount : 0)),
            ("upsertedCount", new JsNumber(r.UpsertedId is null ? 0 : 1)),
            ("upsertedId", r.UpsertedId is null ? JsValue.Null : FromBson(r.UpsertedId)))
        : Result(("acknowledged", JsBoolean.False));

    private UpdateDefinition<BsonDocument> Update(JsValue value) => ToBson(value) switch
    {
        BsonArray stages when stages.All(s => s is BsonDocument) =>
            new PipelineUpdateDefinition<BsonDocument>(PipelineDefinition<BsonDocument, BsonDocument>.Create(stages.Select(s => s.AsBsonDocument))),
        BsonDocument document => new BsonDocumentUpdateDefinition<BsonDocument>(document),
        _ => throw new JavaScriptException(_engine.Intrinsics.TypeError, "The update must be an update document or an aggregation pipeline."),
    };

    private WriteModel<BsonDocument> WriteModel(BsonValue operation)
    {
        if (operation is not BsonDocument { ElementCount: 1 } document || document.GetElement(0).Value is not BsonDocument body)
        {
            throw new JavaScriptException(_engine.Intrinsics.TypeError, "Each bulkWrite operation must be like { insertOne: { document } } or { updateOne: { filter, update } }.");
        }

        var kind = document.GetElement(0).Name;
        var filter = new BsonDocumentFilterDefinition<BsonDocument>(body.GetValue("filter", new BsonDocument()).AsBsonDocument);
        var upsert = body.GetValue("upsert", false).ToBoolean();
        var hint = body.GetValue("hint", null);
        var collation = Collation(body);
        UpdateDefinition<BsonDocument> UpdateOf() => body.GetValue("update", null) switch
        {
            BsonArray stages => new PipelineUpdateDefinition<BsonDocument>(PipelineDefinition<BsonDocument, BsonDocument>.Create(stages.Select(s => s.AsBsonDocument))),
            BsonDocument update => new BsonDocumentUpdateDefinition<BsonDocument>(update),
            _ => throw new JavaScriptException(_engine.Intrinsics.TypeError, $"bulkWrite {kind} needs an update."),
        };
        List<ArrayFilterDefinition>? ArrayFiltersOf() => body.TryGetValue("arrayFilters", out var filters)
            ? filters.AsBsonArray.Select(f => (ArrayFilterDefinition)new BsonDocumentArrayFilterDefinition<BsonDocument>(f.AsBsonDocument)).ToList()
            : null;

        return kind switch
        {
            "insertOne" => new InsertOneModel<BsonDocument>(body.GetValue("document", null) as BsonDocument
                                                            ?? throw new JavaScriptException(_engine.Intrinsics.TypeError, "bulkWrite insertOne needs a document.")),
            "updateOne" => new UpdateOneModel<BsonDocument>(filter, UpdateOf()) { IsUpsert = upsert, Hint = hint, Collation = collation, ArrayFilters = ArrayFiltersOf() },
            "updateMany" => new UpdateManyModel<BsonDocument>(filter, UpdateOf()) { IsUpsert = upsert, Hint = hint, Collation = collation, ArrayFilters = ArrayFiltersOf() },
            "replaceOne" => new ReplaceOneModel<BsonDocument>(filter, body.GetValue("replacement", null) as BsonDocument
                                                                      ?? throw new JavaScriptException(_engine.Intrinsics.TypeError, "bulkWrite replaceOne needs a replacement."))
            {
                IsUpsert = upsert,
                Hint = hint,
                Collation = collation,
            },
            "deleteOne" => new DeleteOneModel<BsonDocument>(filter) { Hint = hint, Collation = collation },
            "deleteMany" => new DeleteManyModel<BsonDocument>(filter) { Hint = hint, Collation = collation },
            _ => throw new JavaScriptException(_engine.Intrinsics.TypeError, $"Unknown bulkWrite operation '{kind}'."),
        };
    }

    private static BsonValue EnsureId(BsonDocument document)
    {
        if (!document.TryGetValue("_id", out var id))
        {
            id = ObjectId.GenerateNewId();
            document.InsertAt(0, new BsonElement("_id", id));
        }

        return id;
    }

    private JsValue OpenCursor(MigrationContext context, string kind, JsValue collectionName, JsValue a, JsValue b)
    {
        var session = context.Session;
        Func<Task<IAsyncCursor<BsonDocument>>> open;
        switch (kind)
        {
            case "find":
                {
                    var collection = Collection(context, collectionName);
                    var filter = new BsonDocumentFilterDefinition<BsonDocument>(Document(a, "The filter"));
                    var options = Document(b, "The options");
                    var findOptions = new FindOptions<BsonDocument, BsonDocument>
                    {
                        Sort = options.TryGetValue("sort", out var sort) ? new BsonDocumentSortDefinition<BsonDocument>(sort.AsBsonDocument) : null,
                        Projection = options.TryGetValue("projection", out var projection)
                            ? new BsonDocumentProjectionDefinition<BsonDocument, BsonDocument>(projection.AsBsonDocument)
                            : null,
                        Skip = options.TryGetValue("skip", out var skip) ? skip.ToInt32() : null,
                        Limit = options.TryGetValue("limit", out var limit) ? limit.ToInt32() : null,
                        BatchSize = options.TryGetValue("batchSize", out var batchSize) ? batchSize.ToInt32() : null,
                        Hint = options.GetValue("hint", null),
                        Collation = Collation(options),
                    };
                    open = () => session is null
                        ? collection.FindAsync(filter, findOptions, _cancellation)
                        : collection.FindAsync(session, filter, findOptions, _cancellation);
                    break;
                }

            case "aggregate":
                {
                    var collection = Collection(context, collectionName);
                    var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(Documents(a, "The pipeline"));
                    var options = Document(b, "The options");
                    var aggregateOptions = new AggregateOptions
                    {
                        AllowDiskUse = options.TryGetValue("allowDiskUse", out var allowDiskUse) ? allowDiskUse.ToBoolean() : null,
                        BatchSize = options.TryGetValue("batchSize", out var batchSize) ? batchSize.ToInt32() : null,
                        Hint = options.GetValue("hint", null),
                        Collation = Collation(options),
                        Let = options.TryGetValue("let", out var let) ? let.AsBsonDocument : null,
                    };
                    open = () => session is null
                        ? collection.AggregateAsync(pipeline, aggregateOptions, _cancellation)
                        : collection.AggregateAsync(session, pipeline, aggregateOptions, _cancellation);
                    break;
                }

            case "listIndexes":
                {
                    var collection = Collection(context, collectionName);
                    open = () => session is null
                        ? collection.Indexes.ListAsync(_cancellation)
                        : collection.Indexes.ListAsync(session, _cancellation);
                    break;
                }

            case "listCollections":
                {
                    var listOptions = new ListCollectionsOptions { Filter = new BsonDocumentFilterDefinition<BsonDocument>(Document(a, "The filter")) };
                    open = () => session is null
                        ? context.Database.ListCollectionsAsync(listOptions, _cancellation)
                        : context.Database.ListCollectionsAsync(session, listOptions, _cancellation);
                    break;
                }

            default:
                throw new JavaScriptException(_engine.Intrinsics.TypeError, $"Unknown cursor '{kind}'.");
        }

        return Async(open, cursor =>
        {
            var handle = ++_nextCursor;
            _cursors[handle] = cursor;
            return new JsNumber(handle);
        });
    }

    private void CloseCursor(int handle)
    {
        if (_cursors.Remove(handle, out var cursor))
        {
            cursor.Dispose();
        }
    }

    /// <summary>
    /// Starts <paramref name="operation"/> after the previous one finishes, so a session is never used concurrently, and
    /// returns a promise that settles (on the script's thread, in <see cref="Await"/>) with what <paramref name="map"/> makes of the result.
    /// </summary>
    private JsValue Async<T>(Func<Task<T>> operation, Func<T, JsValue> map)
    {
        var task = _last.ContinueWith(_ => operation(), _cancellation, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        _last = task.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        var (promise, resolve, reject) = _engine.Advanced.RegisterPromise();
        _pending.Add((task, () =>
        {
            if (task.IsCompletedSuccessfully)
            {
                resolve(Guard("result", () => map(task.Result)));
            }
            else
            {
                reject(Error(task.Exception?.GetBaseException() ?? new OperationCanceledException(_cancellation)));
            }
        }
        ));
        return promise;
    }

    private JsObject Result(params (string Name, JsValue Value)[] properties)
    {
        var o = new JsObject(_engine);
        foreach (var (name, value) in properties)
        {
            o.Set(name, value);
        }

        return o;
    }
}

internal static partial class Log
{
    [LoggerMessage(Message = "{Message}")]
    public static partial void Message(ILogger logger, LogLevel level, string message);
}
