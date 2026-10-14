using System.Reflection;
using Kevsoft.MigrateMongo.Config;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Kevsoft.MigrateMongo.MongoDriver;

/// <summary>Creates the store and executor for a resolved configuration.</summary>
public sealed class MongoDriverBackend
{
    private MongoDriverBackend(IMongoClient client, IMongoDatabase database, MongoMigrationStore store, MongoMigrationExecutor executor)
    {
        Client = client;
        Database = database;
        Store = store;
        Executor = executor;
    }

    public IMongoClient Client { get; }

    public IMongoDatabase Database { get; }

    public MongoMigrationStore Store { get; }

    public MongoMigrationExecutor Executor { get; }

    /// <summary>Version of the MongoDB driver loaded at runtime (which may be newer than the one this assembly was compiled against).</summary>
    public static string DriverVersion =>
        typeof(MongoClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(MongoClient).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public static MongoDriverBackend Create(ResolvedConfig config, IServiceProvider services, ILoggerFactory loggerFactory)
    {
        var settings = MongoClientSettings.FromConnectionString(config.MongoUrl);
        settings.ApplicationName ??= "Kevsoft.MigrateMongo";
        return Create(new MongoClient(settings), config.DatabaseName, config.StateCollection, services, loggerFactory);
    }

    /// <summary>Uses an existing client (e.g. the application's own); the caller owns its lifetime.</summary>
    public static MongoDriverBackend Create(IMongoClient client, string databaseName, string stateCollection, IServiceProvider services, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(client);

        // Connections are opened lazily, so registering the mechanism now still applies to this client.
        MongoAuthentication.EnsureSupported(client.Settings);
        var database = client.GetDatabase(databaseName);
        var store = new MongoMigrationStore(database, stateCollection);
        var executor = new MongoMigrationExecutor(client, database, services, loggerFactory);
        return new MongoDriverBackend(client, database, store, executor);
    }
}
