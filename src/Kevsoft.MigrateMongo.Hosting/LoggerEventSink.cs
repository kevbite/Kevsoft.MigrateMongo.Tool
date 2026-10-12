using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.Protocol;
using Microsoft.Extensions.Logging;

namespace Kevsoft.MigrateMongo.Hosting;

/// <summary>Writes engine events to the application's logging.</summary>
internal sealed class LoggerEventSink(ILogger logger) : IMigrationEventSink
{
    public void OnEvent(RunnerEvent e)
    {
        switch (e.Type)
        {
            case EventTypes.Log:
                logger.Log(ToLogLevel(e.Level), "{Message}", e.Message);
                break;
            case EventTypes.LockWaiting:
                logger.LogInformation("{Message}", e.Message);
                break;
            case EventTypes.LockAcquired:
                logger.LogDebug("{Message}", e.Message);
                break;
            case EventTypes.MigrationStarted when e.Direction == "down":
                logger.LogInformation("Rolling back migration {MigrationId} {MigrationName}", e.MigrationId, e.Name);
                break;
            case EventTypes.MigrationStarted:
                logger.LogInformation("Applying migration {MigrationId} {MigrationName}", e.MigrationId, e.Name);
                break;
            case EventTypes.MigrationCompleted when e.Direction == "down":
                logger.LogInformation("Rolled back migration {MigrationId} {MigrationName} in {DurationMs}ms", e.MigrationId, e.Name, e.DurationMs);
                break;
            case EventTypes.MigrationCompleted:
                logger.LogInformation("Applied migration {MigrationId} {MigrationName} in {DurationMs}ms", e.MigrationId, e.Name, e.DurationMs);
                break;
            case EventTypes.MigrationFailed:
                logger.LogError("Migration {MigrationId} {MigrationName} failed: {Error}", e.MigrationId, e.Name, e.Error);
                break;
            case EventTypes.Result when e.Result is { } result:
                if (result.ExitCode == ExitCodes.Success)
                {
                    logger.LogInformation("{Message}", result.Message ?? "Migrations finished.");
                }
                else
                {
                    logger.LogError("Migrations failed (exit code {ExitCode}): {Message}", result.ExitCode, result.Message);
                }

                break;
        }
    }

    private static LogLevel ToLogLevel(string? level) => level switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "warning" => LogLevel.Warning,
        "error" => LogLevel.Error,
        "critical" => LogLevel.Critical,
        _ => LogLevel.Information,
    };
}
