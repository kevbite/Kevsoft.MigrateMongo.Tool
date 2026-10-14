using System.Reflection;
using Kevsoft.MigrateMongo.Config;
using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.MongoDriver;
using Kevsoft.MigrateMongo.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace Kevsoft.MigrateMongo.Hosting;

/// <summary>
/// Runs migrations from inside an application. It uses the same engine, state collections and lock as the migrate-mongo
/// tool, so app instances and CLI runs coordinate safely.
/// </summary>
public interface IMongoMigrationRunner
{
    /// <summary>Applies pending migrations, optionally stopping after <paramref name="to"/>.</summary>
    Task<CommandResult> UpAsync(string? to = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports what <see cref="UpAsync"/> would apply without changing anything (no lock, no writes, no migration code
    /// runs). For <c>down</c>, use <see cref="ExecuteAsync"/> with <see cref="CommandArguments.DryRun"/>.
    /// </summary>
    Task<CommandResult> DryRunUpAsync(string? to = null, CancellationToken cancellationToken = default);

    Task<CommandResult> StatusAsync(CancellationToken cancellationToken = default);

    /// <summary>What <see cref="UpAsync"/> would do. The exit code is <see cref="ExitCodes.PendingMigrations"/> when there is work.</summary>
    Task<CommandResult> PlanAsync(CancellationToken cancellationToken = default);

    Task<CommandResult> ValidateAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs any CLI command (<c>down</c>, <c>mark</c>, <c>baseline</c>, <c>unlock</c>, <c>history</c>, ...).</summary>
    Task<CommandResult> ExecuteAsync(string command, CommandArguments arguments, CancellationToken cancellationToken = default);
}

internal sealed class MongoMigrationRunner(IServiceProvider services, MongoMigrationRegistration registration) : IMongoMigrationRunner
{
    private static readonly string LibraryVersion =
        typeof(MigrationEngine).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(MigrationEngine).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public Task<CommandResult> UpAsync(string? to = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync("up", new CommandArguments { To = to }, cancellationToken);

    public Task<CommandResult> DryRunUpAsync(string? to = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync("up", new CommandArguments { To = to, DryRun = true }, cancellationToken);

    public Task<CommandResult> StatusAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("status", new CommandArguments(), cancellationToken);

    public Task<CommandResult> PlanAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("plan", new CommandArguments(), cancellationToken);

    public Task<CommandResult> ValidateAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync("validate", new CommandArguments(), cancellationToken);

    public async Task<CommandResult> ExecuteAsync(string command, CommandArguments arguments, CancellationToken cancellationToken = default)
    {
        var options = registration.CreateOptions(services);
        var loggerFactory = services.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
        var logger = loggerFactory.CreateLogger("Kevsoft.MigrateMongo");
        var migrations = registration.Discover(logger);

        IMongoClient client;
        MongoClient? ownedClient = null;
        using var tunnel = options.Tunnel is { } tunnelConfig && options.TunnelProvider is { } provider && options.ConnectionString is { } url
            ? await provider.OpenAsync(
                tunnelConfig,
                url,
                new MongoTunnelOptions { DriverVersion = MongoDriverBackend.DriverVersion, Warn = m => Log.Message(logger, LogLevel.Warning, m) },
                cancellationToken)
            : null;
        if (tunnel is not null)
        {
            Log.TunnelOpened(logger, tunnel.Description);
        }

        if ((tunnel?.MongoUrl ?? options.ConnectionString) is { } connectionString)
        {
            var settings = MongoClientSettings.FromConnectionString(connectionString);
            settings.ApplicationName ??= "Kevsoft.MigrateMongo";
            var created = new MongoClient(settings);
            client = created;
            ownedClient = created;
        }
        else
        {
            client = services.GetService<IMongoClient>()
                     ?? throw new InvalidOperationException(
                         $"Set {nameof(MongoMigrationOptions)}.{nameof(MongoMigrationOptions.ConnectionString)} or register an IMongoClient in the service collection.");
        }

        try
        {
            // A scope per run, so migrations can depend on scoped services.
            await using var scope = services.CreateAsyncScope();
            var backend = MongoDriverBackend.Create(client, options.DatabaseName, options.StateCollection, scope.ServiceProvider, loggerFactory);
            var engine = new MigrationEngine(backend.Store, backend.Executor, migrations, CreateEngineOptions(options), new LoggerEventSink(logger));
            return await engine.ExecuteAsync(command, arguments, cancellationToken);
        }
        finally
        {
            ownedClient?.Dispose();
        }
    }

    private EngineOptions CreateEngineOptions(MongoMigrationOptions options)
    {
        var host = services.GetService<IHostEnvironment>();
        return new EngineOptions
        {
            LockTtl = options.LockTtl,
            LockWaitTimeout = options.LockWaitTimeout,
            OnChecksumMismatch = options.OnChecksumMismatch switch
            {
                ChecksumMismatchBehavior.Warn => "warn",
                ChecksumMismatchBehavior.Ignore => "ignore",
                _ => "fail",
            },
            Transaction = options.Transaction switch
            {
                TransactionMode.Always => "always",
                TransactionMode.Never => "never",
                _ => "auto",
            },
            AllowOutOfOrder = options.AllowOutOfOrder,
            TransactionRetryTimeout = options.TransactionRetryTimeout,
            Environment = options.Environment ?? host?.EnvironmentName,
            Provenance = new Provenance
            {
                Host = System.Environment.MachineName,
                User = System.Environment.UserName,
                Tool = "app/" + (host?.ApplicationName ?? Assembly.GetEntryAssembly()?.GetName().Name ?? "unknown"),
                Library = LibraryVersion,
                Driver = MongoDriverBackend.DriverVersion,
                Runtime = System.Environment.Version.ToString(),
            },
        };
    }
}
