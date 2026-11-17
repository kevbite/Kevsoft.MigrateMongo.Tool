using Kevsoft.MigrateMongo.Scripting;
using Kevsoft.MigrateMongo.Tests.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Scripting;

public sealed class ScriptMigrationTests : IDisposable
{
    private readonly ScriptFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private static IMongoDatabase Shop(Func<string, object?> collectionMethod) =>
        DriverFake.Of<IMongoDatabase>(method => method.Name switch
        {
            "get_DatabaseNamespace" => new DatabaseNamespace("shop"),
            "GetCollection" => DriverFake.Of<IMongoCollection<BsonDocument>>(m => m.Name switch
            {
                "get_CollectionNamespace" => new CollectionNamespace("shop", "orders"),
                "get_DocumentSerializer" => BsonDocumentSerializer.Instance,
                "get_Settings" => new MongoCollectionSettings(),
                var name => collectionMethod(name),
            }),
            _ => throw new NotSupportedException(method.Name),
        });

    private MigrationContext Context(IMongoDatabase database, MigrationDirection direction = MigrationDirection.Up, ILogger? logger = null) =>
        new(database, null, logger ?? NullLogger.Instance, null!, new MigrationRunInfo("run-7", 3, direction, false, "staging"), new MigrationResults());

    private async Task<MigrationContext> Run(string code, IMongoDatabase? database = null, MigrationDirection direction = MigrationDirection.Up, ILogger? logger = null)
    {
        _folder.Write("migrations/20260101000000-test.js", code);
        var descriptor = new ScriptMigrationSource(_folder.Root).GetMigrations().Single();
        var context = Context(database ?? Shop(name => throw new NotSupportedException(name)), direction, logger);
        var migration = descriptor.Create(null!);
        await (direction == MigrationDirection.Up
            ? migration.UpAsync(context, TestContext.Current.CancellationToken)
            : migration.DownAsync(context, TestContext.Current.CancellationToken));
        return context;
    }

    [Fact]
    public async Task Writes_are_converted_to_bson_tracked_and_return_driver_style_results()
    {
        var database = Shop(name => name == "UpdateManyAsync"
            ? Task.FromResult<UpdateResult>(new UpdateResult.Acknowledged(2, 2, null))
            : throw new NotSupportedException(name));

        var context = await Run("""
            import { ObjectId, Long, Decimal128 } from '@kevsoft/migrate-mongo';

            export async function up(db, context) {
              const result = await db.collection('orders', { label: 'archived' }).updateMany(
                {
                  status: 'complete', closedAt: { $lt: new Date(Date.UTC(2026, 0, 1)) }, owner: ObjectId('65f000000000000000000001'),
                  total: 1.5, count: 3, big: 9007199254740993n, long: Long('12'), price: Decimal128('9.99'), skip: undefined, code: /^A/i,
                },
                { $set: { archived: true, tags: ['a', null] } });
              context.results.set('matched', result.matchedCount);
              context.results.set('acknowledged', result.acknowledged);
            }
            """, database);

        var operation = context.Results.Operations.ShouldHaveSingleItem();
        (operation.Kind, operation.Collection, operation.Label).ShouldBe(("updateMany", "orders", "archived"));
        operation.Statement.ShouldBe(new BsonDocument
        {
            {
                "filter", new BsonDocument
                {
                    { "status", "complete" },
                    { "closedAt", new BsonDocument("$lt", new BsonDateTime(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))) },
                    { "owner", new ObjectId("65f000000000000000000001") },
                    { "total", 1.5 },
                    { "count", 3 },
                    { "big", 9007199254740993L },
                    { "long", 12L },
                    { "price", new BsonDecimal128(Decimal128.Parse("9.99")) },
                    { "code", new BsonRegularExpression("^A", "i") },
                }
            },
            { "update", new BsonDocument("$set", new BsonDocument { { "archived", true }, { "tags", new BsonArray { "a", BsonNull.Value } } }) },
        });
        context.Results["matched"].ShouldBe(new BsonInt32(2));
        context.Results["acknowledged"].ShouldBe(BsonBoolean.True);
    }

    [Fact]
    public async Task The_context_exposes_results_the_run_and_the_migration()
    {
        var context = await Run("""
            export async function up(db, context) {
              context.results.set('run', { id: context.run.id, batch: context.run.batch, direction: context.run.direction,
                transactional: context.run.transactional, environment: context.run.environment });
              context.results.set('migration', context.migration);
              context.results.set('database', db.databaseName);
              context.results.increment('seen');
              context.results.increment('seen', 4);
              context.results.set('gone', 1);
              context.results.remove('gone');
              context.results.set('previous', context.results.get('seen'));
              context.results.note('heads up', 'warning');
            }
            """);

        context.Results["run"].ShouldBe(BsonDocument.Parse("{ id: 'run-7', batch: 3, direction: 'up', transactional: false, environment: 'staging' }"));
        context.Results["migration"].ShouldBe(BsonDocument.Parse("{ id: '20260101000000', name: 'test' }"));
        context.Results["database"].ShouldBe(new BsonString("shop"));
        context.Results["seen"].ShouldBe(new BsonInt64(5));
        context.Results["gone"].ShouldBeNull();
        context.Results["previous"].ShouldBe(new BsonInt32(5));
        context.Results.Notes.Select(n => (n.Level, n.Message)).ShouldBe([(NoteLevel.Warning, "heads up")]);
    }

    [Fact]
    public async Task Console_and_the_logger_write_to_the_migration_log()
    {
        var logger = new ListLogger();

        await Run("""
            export async function up(db, context) {
              console.log('hello', { a: 1, at: new Date(0) }, 2);
              context.logger.warn('careful');
              console.error(new Error('bad'));
            }
            """, logger: logger);

        logger.Entries[0].ShouldBe((LogLevel.Information, """hello { "a" : 1, "at" : { "$date" : "1970-01-01T00:00:00Z" } } 2"""));
        logger.Entries[1].ShouldBe((LogLevel.Warning, "careful"));
        logger.Entries[2].Level.ShouldBe(LogLevel.Error);
        logger.Entries[2].Message.ShouldStartWith("Error: bad\n    at up (migrations/20260101000000-test.js:4:");
    }

    [Fact]
    public async Task Down_runs_the_down_function()
    {
        var context = await Run("""
            export async function up(db, context) { context.results.set('ran', 'up'); }
            export async function down(db, context) { context.results.set('ran', context.run.direction); }
            """, direction: MigrationDirection.Down);

        context.Results["ran"].ShouldBe(new BsonString("down"));
    }

    [Fact]
    public async Task A_migration_without_down_cant_be_rolled_back()
    {
        var error = await Should.ThrowAsync<ScriptException>(() => Run("export async function up() {}", direction: MigrationDirection.Down));

        error.Message.ShouldStartWith("migrations/20260101000000-test.js has no down function");
    }

    [Fact]
    public async Task Errors_thrown_by_the_script_name_the_file()
    {
        var error = await Should.ThrowAsync<ScriptException>(() => Run("""
            export async function up() {
              throw new RangeError('out of range');
            }
            """));

        error.Message.ShouldStartWith("migrations/20260101000000-test.js: RangeError: out of range");
        error.Message.ShouldContain("migrations/20260101000000-test.js:2");
    }

    [Fact]
    public async Task Driver_errors_can_be_caught_or_reach_the_runner_unchanged()
    {
        var failure = new MongoException("not primary");
        failure.AddErrorLabel("TransientTransactionError");
        var database = Shop(name => name == "DeleteManyAsync"
            ? Task.FromException<DeleteResult>(failure)
            : throw new NotSupportedException(name));

        var caught = await Run("""
            export async function up(db, context) {
              try { await db.collection('orders').deleteMany({}); }
              catch (e) { context.results.set('caught', e.message); }
            }
            """, database);
        caught.Results["caught"].ShouldBe(new BsonString("not primary"));

        _folder.Dispose();
        using var other = new ScriptFolder().Write("migrations/1-x.js", "export async function up(db) { await db.collection('orders').deleteMany({}); }");
        var migration = new ScriptMigrationSource(other.Root).GetMigrations().Single().Create(null!);
        var thrown = await Should.ThrowAsync<MongoException>(() => migration.UpAsync(Context(database), TestContext.Current.CancellationToken));
        thrown.ShouldBeSameAs(failure);
    }

    [Fact]
    public async Task Awaiting_something_that_never_finishes_fails_instead_of_hanging()
    {
        var error = await Should.ThrowAsync<ScriptException>(() => Run("export async function up() { await new Promise(() => {}); }"));

        error.Message.ShouldContain("The script is waiting for something that will never finish. Only database calls can be awaited.");
    }

    [Fact]
    public async Task Writes_that_arent_awaited_still_finish_before_the_migration_does()
    {
        var calls = 0;
        var database = Shop(name => name == "InsertOneAsync"
            ? Task.Delay(50).ContinueWith(_ => Interlocked.Increment(ref calls), TaskScheduler.Default)
            : throw new NotSupportedException(name));

        var context = await Run("export async function up(db) { db.collection('orders').insertOne({ a: 1 }); }", database);

        calls.ShouldBe(1);
        context.Results.Operations.ShouldHaveSingleItem().Kind.ShouldBe("insertOne");
    }

    [Fact]
    public async Task A_cancelled_run_stops_the_script()
    {
        _folder.Write("migrations/1-forever.js", "export async function up() { while (true) {} }");
        var migration = new ScriptMigrationSource(_folder.Root).GetMigrations().Single().Create(null!);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Should.ThrowAsync<OperationCanceledException>(() => migration.UpAsync(Context(Shop(n => throw new NotSupportedException(n))), cancellation.Token));
    }

    [Fact]
    public async Task Bson_helpers_round_trip_through_documents()
    {
        var context = await Run("""
            import { ObjectId, UUID, Timestamp } from '@kevsoft/migrate-mongo';

            export async function up(db, context) {
              const id = ObjectId.createFromTime(1700000000);
              context.results.set('values', {
                id: ObjectId('65f000000000000000000001'),
                hex: ObjectId('65f000000000000000000001').toHexString(),
                valid: ObjectId.isValid('65f000000000000000000001'),
                invalid: ObjectId.isValid('nope'),
                seconds: id.getTimestamp().getTime() / 1000,
                same: ObjectId('65f000000000000000000001').equals(ObjectId('65f000000000000000000001')),
                uuid: UUID('0f8fad5b-d9cb-469f-a165-70867728950e'),
                uuidText: String(UUID('0f8fad5b-d9cb-469f-a165-70867728950e')),
                ts: Timestamp({ t: 5, i: 2 }),
              });
            }
            """);

        context.Results["values"].ShouldBe(new BsonDocument
        {
            { "id", new ObjectId("65f000000000000000000001") },
            { "hex", "65f000000000000000000001" },
            { "valid", true },
            { "invalid", false },
            { "seconds", 1700000000 },
            { "same", true },
            { "uuid", new BsonBinaryData(Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), GuidRepresentation.Standard) },
            { "uuidText", "0f8fad5b-d9cb-469f-a165-70867728950e" },
            { "ts", new BsonTimestamp(5, 2) },
        });
    }

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
