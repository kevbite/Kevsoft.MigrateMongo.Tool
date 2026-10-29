using Kevsoft.MigrateMongo.Hosting;
using Kevsoft.MigrateMongo.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Kevsoft.MigrateMongo.Tests.Hosting;

public sealed class HostingTests
{
    [Fact]
    public void AddMongoMigrations_registers_a_runner_without_running_on_startup()
    {
        var services = new ServiceCollection();

        services.AddMongoMigrations(o => o.DatabaseName = "app");

        using var provider = services.BuildServiceProvider();
        provider.GetService<IMongoMigrationRunner>().ShouldNotBeNull();
        provider.GetServices<IHostedService>().ShouldBeEmpty();
    }

    [Fact]
    public void RunOnStartup_registers_one_startup_service_even_when_called_repeatedly()
    {
        var services = new ServiceCollection();

        services.AddMongoMigrations(o => o.DatabaseName = "app").RunOnStartup().RunOnStartup();
        services.AddMongoMigrations(o => o.LockTtl = TimeSpan.FromMinutes(1)).RunOnStartup();

        using var provider = services.BuildServiceProvider();
        provider.GetServices<IHostedService>().ShouldHaveSingleItem().ShouldBeAssignableTo<IHostedLifecycleService>();
        services.Count(d => d.ServiceType == typeof(IMongoMigrationRunner)).ShouldBe(1);
    }

    [Fact]
    public void Configure_delegates_are_applied_in_order_and_can_use_services()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new AppSettings("from-services"));

        var builder = services.AddMongoMigrations(o =>
        {
            o.DatabaseName = "first";
            o.StateCollection = "_changes";
        });
        services.AddMongoMigrations((sp, o) => o.DatabaseName = sp.GetRequiredService<AppSettings>().DatabaseName);

        using var provider = services.BuildServiceProvider();
        var options = builder.Registration.CreateOptions(provider);
        options.DatabaseName.ShouldBe("from-services");
        options.StateCollection.ShouldBe("_changes");
        options.LockTtl.ShouldBe(TimeSpan.FromMinutes(5));
        options.LockWaitTimeout.ShouldBe(TimeSpan.FromMinutes(1));
        options.TransactionRetryTimeout.ShouldBe(TimeSpan.FromSeconds(120));
        options.OnChecksumMismatch.ShouldBe(ChecksumMismatchBehavior.Fail);
        options.Transaction.ShouldBe(TransactionMode.Auto);
    }

    [Fact]
    public async Task Missing_database_name_is_reported()
    {
        var runner = Runner(services => services.AddMongoMigrations(_ => { }).AddSource(new EmptySource()));

        var error = await Should.ThrowAsync<InvalidOperationException>(() => runner.UpAsync());

        error.Message.ShouldBe("Invalid Kevsoft.MigrateMongo options: DatabaseName is required.");
    }

    [Fact]
    public async Task Invalid_timeouts_are_reported_together()
    {
        var runner = Runner(services => services.AddMongoMigrations(o =>
        {
            o.DatabaseName = "app";
            o.LockTtl = TimeSpan.Zero;
            o.LockWaitTimeout = TimeSpan.FromSeconds(-1);
            o.TransactionRetryTimeout = TimeSpan.FromSeconds(-1);
            o.ResultsMaxBytes = 0;
        }).AddSource(new EmptySource()));

        var error = await Should.ThrowAsync<InvalidOperationException>(() => runner.UpAsync());

        error.Message.ShouldBe("Invalid Kevsoft.MigrateMongo options: LockTtl must be positive. LockWaitTimeout can't be negative. TransactionRetryTimeout can't be negative. ResultsMaxBytes must be positive.");
    }

    [Fact]
    public async Task A_tunnel_needs_a_connection_string_and_a_provider()
    {
        var runner = Runner(services => services.AddMongoMigrations(o =>
        {
            o.DatabaseName = "app";
            o.Tunnel = new Kevsoft.MigrateMongo.Config.TunnelConfig { Host = "bastion", User = "u", Password = "p" };
        }));

        var error = await Should.ThrowAsync<InvalidOperationException>(() => runner.UpAsync());

        error.Message.ShouldBe("Invalid Kevsoft.MigrateMongo options: Tunnel needs ConnectionString; a registered IMongoClient can't be tunnelled. " +
                               "Tunnel needs a TunnelProvider: add the Kevsoft.MigrateMongo.Tunnel package and set `TunnelProvider = new SshTunnelProvider()`.");
    }

    [Fact]
    public async Task Not_choosing_any_migrations_is_reported()
    {
        var runner = Runner(services => services.AddMongoMigrations(o => o.DatabaseName = "app"));

        var error = await Should.ThrowAsync<InvalidOperationException>(() => runner.UpAsync());

        error.Message.ShouldBe("No migrations have been configured. Call FromAssemblyContaining<T>() (or FromAssembly/AddSource) after AddMongoMigrations(...).");
    }

    [Fact]
    public async Task No_connection_string_and_no_registered_client_is_reported()
    {
        var runner = Runner(services => services.AddMongoMigrations(o => o.DatabaseName = "app").AddSource(new EmptySource()));

        var error = await Should.ThrowAsync<InvalidOperationException>(() => runner.UpAsync());

        error.Message.ShouldBe("Set MongoMigrationOptions.ConnectionString or register an IMongoClient in the service collection.");
    }

    [Fact]
    public void An_assembly_without_migrations_logs_a_warning()
    {
        var services = new ServiceCollection();
        var builder = services.AddMongoMigrations(o => o.DatabaseName = "app").FromAssemblyContaining<HostingTests>();
        var logger = new RecordingLogger();

        builder.Registration.Discover(logger).Migrations.ShouldBeEmpty();

        logger.Entries.ShouldBe([(LogLevel.Warning, "Assembly Kevsoft.MigrateMongo.Tests doesn't contain any migrations.")]);
    }

    [Fact]
    public async Task Startup_service_applies_migrations_before_the_host_starts()
    {
        var runner = new FakeRunner(new CommandResult { ExitCode = 0 });
        var service = StartupService(runner);

        await service.StartingAsync(CancellationToken.None);

        runner.UpCalls.ShouldBe(1);
        runner.UpArguments!.Only.ShouldBeNull();
    }

    [Fact]
    public async Task Startup_service_can_leave_collection_definitions_to_a_deploy_step()
    {
        var runner = new FakeRunner(new CommandResult { ExitCode = 0 });
        var service = StartupService(runner, o => o.Collections = false);

        await service.StartingAsync(CancellationToken.None);

        runner.UpArguments!.Only.ShouldBe("migrations");
    }

    [Fact]
    public async Task Startup_service_stops_the_host_when_migrations_fail()
    {
        var result = new CommandResult { ExitCode = 3, Message = "Timed out waiting for the lock." };
        var service = StartupService(new FakeRunner(result));

        var error = await Should.ThrowAsync<MongoMigrationException>(() => service.StartingAsync(CancellationToken.None));

        error.Message.ShouldBe("Timed out waiting for the lock.");
        error.Result.ShouldBeSameAs(result);
    }

    [Fact]
    public async Task Startup_failure_without_a_message_reports_the_exit_code()
    {
        var service = StartupService(new FakeRunner(new CommandResult { ExitCode = 1 }));

        var error = await Should.ThrowAsync<MongoMigrationException>(() => service.StartingAsync(CancellationToken.None));

        error.Message.ShouldBe("Migrations failed with exit code 1.");
    }

    [Fact]
    public void Engine_events_are_written_to_the_application_log()
    {
        var logger = new RecordingLogger();
        var sink = new LoggerEventSink(logger);

        sink.OnEvent(new RunnerEvent { Type = EventTypes.LockWaiting, Message = "Waiting for lock held by run abc." });
        sink.OnEvent(new RunnerEvent { Type = EventTypes.LockAcquired, Message = "Acquired lock (run def)." });
        sink.OnEvent(new RunnerEvent { Type = EventTypes.MigrationStarted, MigrationId = "20260101000001", Name = "create-users", Direction = "up" });
        sink.OnEvent(new RunnerEvent { Type = EventTypes.Log, Level = "warning", Message = "careful" });
        sink.OnEvent(new RunnerEvent { Type = EventTypes.MigrationCompleted, MigrationId = "20260101000001", Name = "create-users", Direction = "up", DurationMs = 12 });
        sink.OnEvent(new RunnerEvent { Type = EventTypes.MigrationStarted, MigrationId = "20260101000002", Name = "seed", Direction = "down" });
        sink.OnEvent(new RunnerEvent { Type = EventTypes.MigrationCompleted, MigrationId = "20260101000002", Name = "seed", Direction = "down", DurationMs = 5 });
        sink.OnEvent(new RunnerEvent { Type = EventTypes.MigrationFailed, MigrationId = "20260101000003", Name = "broken", Direction = "up", Error = "E11000 duplicate key" });
        sink.OnEvent(new RunnerEvent { Type = EventTypes.Result, Result = new CommandResult { ExitCode = 0, Message = "Applied 1 migration." } });
        sink.OnEvent(new RunnerEvent { Type = EventTypes.Result, Result = new CommandResult { ExitCode = 1, Message = "1 migration failed." } });

        logger.Entries.ShouldBe([
            (LogLevel.Information, "Waiting for lock held by run abc."),
            (LogLevel.Debug, "Acquired lock (run def)."),
            (LogLevel.Information, "Applying migration 20260101000001 create-users"),
            (LogLevel.Warning, "careful"),
            (LogLevel.Information, "Applied migration 20260101000001 create-users in 12ms"),
            (LogLevel.Information, "Rolling back migration 20260101000002 seed"),
            (LogLevel.Information, "Rolled back migration 20260101000002 seed in 5ms"),
            (LogLevel.Error, "Migration 20260101000003 broken failed: E11000 duplicate key"),
            (LogLevel.Information, "Applied 1 migration."),
            (LogLevel.Error, "Migrations failed (exit code 1): 1 migration failed."),
        ]);
    }

    [Fact]
    public void Collection_events_are_written_to_the_application_log()
    {
        var logger = new RecordingLogger();
        var sink = new LoggerEventSink(logger);

        sink.OnEvent(new RunnerEvent { Type = EventTypes.CollectionStarted, Collection = "cases" });
        sink.OnEvent(new RunnerEvent
        {
            Type = EventTypes.CollectionCompleted,
            Collection = "cases",
            DurationMs = 40,
            Changes = [new IndexChangeInfo { Action = "create", Index = "status_1" }, new IndexChangeInfo { Action = "hide", Index = "old_1" }],
        });
        sink.OnEvent(new RunnerEvent { Type = EventTypes.CollectionCompleted, Collection = "users", Changes = [] });
        sink.OnEvent(new RunnerEvent { Type = EventTypes.CollectionSkipped, Collection = "orders", Message = "orders: superseded." });
        sink.OnEvent(new RunnerEvent { Type = EventTypes.CollectionFailed, Collection = "events", Error = "rebuild blocked" });

        logger.Entries.ShouldBe([
            (LogLevel.Information, "Changing collection cases's indexes"),
            (LogLevel.Information, "Made 2 change(s) to collection cases's indexes in 40ms"),
            (LogLevel.Debug, "Collection users's indexes match its definition"),
            (LogLevel.Warning, "orders: superseded."),
            (LogLevel.Error, "Collection events failed: rebuild blocked"),
        ]);
    }

    private static IMongoMigrationRunner Runner(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        return services.BuildServiceProvider().GetRequiredService<IMongoMigrationRunner>();
    }

    private static IHostedLifecycleService StartupService(IMongoMigrationRunner runner, Action<MongoMigrationStartupOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddMongoMigrations(o => o.DatabaseName = "app").RunOnStartup(configure);
        services.AddSingleton(runner);
        return (IHostedLifecycleService)services.BuildServiceProvider().GetServices<IHostedService>().Single();
    }

    private sealed record AppSettings(string DatabaseName);

    private sealed class EmptySource : IMigrationSource
    {
        public IReadOnlyCollection<MigrationDescriptor> GetMigrations() => [];
    }

    private sealed class FakeRunner(CommandResult result) : IMongoMigrationRunner
    {
        public int UpCalls { get; private set; }

        public Task<CommandResult> UpAsync(string? to = null, CancellationToken cancellationToken = default)
        {
            UpCalls++;
            return Task.FromResult(result);
        }

        public Task<CommandResult> DryRunUpAsync(string? to = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CommandResult> StatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CommandResult> PlanAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CommandResult> ValidateAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CommandResult> ShowAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public CommandArguments? UpArguments { get; private set; }

        public Task<CommandResult> ExecuteAsync(string command, CommandArguments arguments, CancellationToken cancellationToken = default)
        {
            if (command != "up")
            {
                throw new NotSupportedException();
            }

            UpArguments = arguments;
            return UpAsync(arguments.To, cancellationToken);
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
