using Kevsoft.MigrateMongo.Protocol;
using Microsoft.Extensions.Hosting;

namespace Kevsoft.MigrateMongo.Hosting;

/// <summary>Thrown when migrations fail while the host is starting.</summary>
public sealed class MongoMigrationException(CommandResult result)
    : Exception(result.Message ?? $"Migrations failed with exit code {result.ExitCode}.")
{
    public CommandResult Result { get; } = result;
}

/// <summary>
/// Applies migrations in <see cref="IHostedLifecycleService.StartingAsync"/>, which the host runs before starting any hosted
/// service, so the app (e.g. the ASP.NET Core server) only starts once the database is up to date.
/// </summary>
internal sealed class MongoMigrationStartupService(IMongoMigrationRunner runner) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        var result = await runner.UpAsync(cancellationToken: cancellationToken);
        if (result.ExitCode != ExitCodes.Success)
        {
            throw new MongoMigrationException(result);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
