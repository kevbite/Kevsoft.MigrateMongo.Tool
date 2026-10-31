using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MongoDB.Driver;
using Shouldly;

namespace Kevsoft.MigrateMongo.IntegrationTests;

/// <summary>
/// A standalone MongoDB (not a replica set) can't run transactions: <c>transaction: auto</c> runs migrations without one,
/// and a migration that needs one fails before it starts.
/// </summary>
public sealed class StandaloneServerTests(IntegrationEnvironment environment, StandaloneServerTests.Server server)
    : IClassFixture<StandaloneServerTests.Server>
{
    [Fact]
    public async Task Auto_runs_migrations_without_transactions()
    {
        await using var workspace = server.CreateWorkspace(environment);

        var up = await workspace.RunAsync("up");

        // SeedDefaults checks it has a transaction (on a replica set `auto` gives it one), so here it shows it didn't get one.
        up.ExitCode.ShouldBe(1, up.ToString());
        up.Output.ShouldContain("Expected to run in a transaction.");
        workspace.Find("_migrations").Select(s => (s["_id"].AsString, s["state"].AsString, s["transactional"].AsBoolean)).ShouldBe([
            ("20260101000001", "applied", false),
            ("20260101000002", "applied", false),
            ("20260101000003", "failed", false),
        ]);
    }

    [Fact]
    public async Task A_migration_that_needs_a_transaction_fails_before_it_starts()
    {
        await using var workspace = server.CreateWorkspace(environment, "transaction: always");

        var up = await workspace.RunAsync("up");

        // The first two opt out of transactions themselves; the third uses the project's `always`.
        up.ExitCode.ShouldBe(1, up.ToString());
        up.Output.ShouldContain("requires a transaction, but the MongoDB deployment doesn't support transactions");
        workspace.Find("_migrations").Select(s => s["_id"].AsString).ShouldBe(["20260101000001", "20260101000002"]);
        workspace.Find("roles").ShouldBeEmpty();
    }

    public sealed class Server : IAsyncLifetime
    {
        private IContainer? _container;

        public string Url { get; private set; } = "";

        public IMongoClient Client { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            _container = new ContainerBuilder("mongo:8.0")
                .WithPortBinding(27017, assignRandomHostPort: true)
                // The image turns off glibc's rseq so TCMalloc can use per-CPU caches, but mongod 8.0 won't start with
                // those on Linux 6.19+ (SERVER-121912), which CI runners have. Let glibc keep it, as Atlas Local does.
                .WithEnvironment("GLIBC_TUNABLES", "glibc.pthread.rseq=1")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Waiting for connections"))
                .Build();
            await _container.StartAsync();
            Url = $"mongodb://{_container.Hostname}:{_container.GetMappedPublicPort(27017)}/";
            Client = new MongoClient(Url);
        }

        public ValueTask DisposeAsync() => _container?.DisposeAsync() ?? ValueTask.CompletedTask;

        public Workspace CreateWorkspace(IntegrationEnvironment environment, string extraConfig = "")
        {
            var workspace = Workspace.FromFixture(environment, "BasicMigrations", client: Client);
            workspace.WriteConfigFile($"""
                project: BasicMigrations
                mongodb:
                  url: {Url}
                  databaseName: {workspace.DatabaseName}
                {extraConfig}
                """);
            return workspace;
        }
    }
}
