using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;

namespace HostingApp;

/// <summary>Stands in for the app's real work: reports what it can see when it starts, then shuts the app down.</summary>
public sealed class AppStartedService(IMongoClient client, IHostApplicationLifetime lifetime) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var database = client.GetDatabase(Environment.GetEnvironmentVariable("DATABASE_NAME"));
        var applied = await database.GetCollection<BsonDocument>("_migrations")
            .CountDocumentsAsync(new BsonDocument("state", "applied"), cancellationToken: cancellationToken);
        Console.WriteLine($"App started; applied migrations: {applied}");
        lifetime.StopApplication();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
