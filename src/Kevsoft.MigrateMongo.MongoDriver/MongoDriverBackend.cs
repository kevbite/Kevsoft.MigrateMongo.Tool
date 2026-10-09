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
        var client = new MongoClient(settings);
        var database = client.GetDatabase(config.DatabaseName);
        var store = new MongoMigrationStore(database, config.StateCollection);
        var executor = new MongoMigrationExecutor(client, database, services, loggerFactory);
        return new MongoDriverBackend(client, database, store, executor);
    }
}
