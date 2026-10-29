using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.MongoDriver;
using Kevsoft.MigrateMongo.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests.Collections;

/// <summary>
/// Runs the engine with collection definitions written in the test against the integration MongoDB, in its own database.
/// Faster than going through the tool, so every index option and change can be checked against a real server.
/// </summary>
internal sealed class CollectionHarness(IntegrationEnvironment environment) : IAsyncDisposable
{
    public const string Things = "things";

    public string DatabaseName { get; } = "it_coll_" + Guid.NewGuid().ToString("N")[..16];

    public IMongoDatabase Database => environment.Client.GetDatabase(DatabaseName);

    public IMongoCollection<BsonDocument> Collection(string name = Things) => Database.GetCollection<BsonDocument>(name);

    public List<RunnerEvent> Events { get; } = [];

    /// <summary>A definition of <see cref="Things"/> (or <paramref name="name"/>).</summary>
    public static CollectionDescriptor Define(Action<IndexesBuilder<BsonDocument>> indexes, string name = Things) =>
        Define<BsonDocument>(c => indexes(c.Indexes), name);

    public static CollectionDescriptor Define<T>(Action<CollectionBuilder<T>> define, string name = Things) =>
        CollectionDescriptor.Create<Definition<T>, T>(name, $"Tests.{name}", _ => new Definition<T>(define));

    public Task<CommandResult> UpAsync(CollectionDescriptor collection, Action<EngineOptions>? options = null, CommandArguments? arguments = null) =>
        RunAsync("up", [collection], options, arguments);

    public Task<CommandResult> PlanAsync(CollectionDescriptor collection, Action<EngineOptions>? options = null, CommandArguments? arguments = null) =>
        RunAsync("plan", [collection], options, arguments);

    public Task<CommandResult> RunAsync(
        string command,
        IEnumerable<CollectionDescriptor> collections,
        Action<EngineOptions>? options = null,
        CommandArguments? arguments = null,
        IMongoClient? client = null,
        CancellationToken cancellationToken = default)
    {
        var engineOptions = new EngineOptions
        {
            LockWaitTimeout = TimeSpan.FromSeconds(5),
            Provenance = new Provenance { Host = "it-host", User = "it", Library = "test", Driver = "test", Runtime = "test" },
        };
        options?.Invoke(engineOptions);
        var backend = MongoDriverBackend.Create(client ?? environment.Client, DatabaseName, "_migrations", new NoServices(), NullLoggerFactory.Instance);
        var engine = new MigrationEngine(backend.Store, backend.Executor, [], engineOptions, new Sink(Events), collections: collections);
        return engine.ExecuteAsync(command, arguments ?? new CommandArguments(), cancellationToken);
    }

    /// <summary>
    /// A client that cancels <paramref name="cancel"/> as soon as a <paramref name="commandName"/> command on this database
    /// succeeds, so a run can be cancelled between two of its operations.
    /// </summary>
    public MongoClient CancellingAfter(string commandName, CancellationTokenSource cancel)
    {
        var settings = MongoClientSettings.FromConnectionString(environment.MongoUrl);
        settings.ClusterConfigurator = builder => builder.Subscribe<CommandSucceededEvent>(e =>
        {
            if (e.CommandName == commandName && e.DatabaseNamespace.DatabaseName == DatabaseName)
            {
                cancel.Cancel();
            }
        });
        return new MongoClient(settings);
    }

    /// <summary>Applies <paramref name="collection"/> and checks that it changed something and that it's in line afterwards.</summary>
    public async Task<CommandResult> ApplyAsync(CollectionDescriptor collection, Action<EngineOptions>? options = null, CommandArguments? arguments = null)
    {
        var up = await UpAsync(collection, options, arguments);
        up.ExitCode.ShouldBe(ExitCodes.Success, up.Message + string.Concat(up.Issues?.Select(i => Environment.NewLine + i.Message) ?? []));
        await ShouldBeInSyncAsync(collection, options);
        return up;
    }

    /// <summary>A plan finds nothing to change and a second <c>up</c> records nothing.</summary>
    public async Task ShouldBeInSyncAsync(CollectionDescriptor collection, Action<EngineOptions>? options = null)
    {
        var plan = await PlanAsync(collection, options);
        var info = plan.Collections.ShouldNotBeNull().ShouldHaveSingleItem();
        info.State.ShouldBe(CollectionStates.InSync, string.Join(Environment.NewLine, info.Changes.Select(c => $"{c.Action} {c.Index}: {c.Detail}")));
        plan.ExitCode.ShouldBe(ExitCodes.Success);

        var history = History().Count;
        var again = await UpAsync(collection, options);
        again.ExitCode.ShouldBe(ExitCodes.Success, again.Message);
        again.Reconciled.ShouldBeEmpty();
        History().Count.ShouldBe(history);
    }

    /// <summary>The collection's indexes on the server, by name.</summary>
    public Dictionary<string, BsonDocument> Indexes(string collection = Things) =>
        Collection(collection).Indexes.List().ToList().ToDictionary(i => i["name"].AsString);

    public BsonDocument State(string collection = Things) =>
        Database.GetCollection<BsonDocument>("_migrations.collections").Find(new BsonDocument("_id", collection)).Single();

    /// <summary>The kinds of the operations the last change recorded, e.g. <c>createIndex</c> or <c>runCommand</c>.</summary>
    public List<string> Operations(string collection = Things) =>
        State(collection)["operations"].AsBsonArray.Select(o => o["kind"].AsString).ToList();

    public List<BsonDocument> History() =>
        Database.GetCollection<BsonDocument>("_migrations.history").Find(new BsonDocument("kind", "collection")).ToList();

    public static List<string> Changes(CommandResult result) =>
        result.Collections.ShouldNotBeNull().Single().Changes.Select(c => $"{c.Action} {c.Index}").ToList();

    public async ValueTask DisposeAsync() => await environment.Client.DropDatabaseAsync(DatabaseName);

    private sealed class Definition<T>(Action<CollectionBuilder<T>> define) : ICollectionDefinition<T>
    {
        public void Define(CollectionBuilder<T> collection) => define(collection);
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class Sink(List<RunnerEvent> events) : IMigrationEventSink
    {
        public void OnEvent(RunnerEvent runnerEvent)
        {
            lock (events)
            {
                events.Add(runnerEvent);
            }
        }
    }
}
