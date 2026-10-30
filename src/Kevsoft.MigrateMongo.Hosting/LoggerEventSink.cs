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
                var level = ToLogLevel(e.Level);
                Log.Message(logger, level, e.Message);
                break;
            case EventTypes.LockWaiting:
                Log.Message(logger, LogLevel.Information, e.Message);
                break;
            case EventTypes.LockAcquired:
                Log.Message(logger, LogLevel.Debug, e.Message);
                break;
            case EventTypes.MigrationStarted when e.Direction == "down":
                Log.MigrationRollingBack(logger, e.MigrationId, e.Name);
                break;
            case EventTypes.MigrationStarted:
                Log.MigrationApplying(logger, e.MigrationId, e.Name);
                break;
            case EventTypes.MigrationCompleted when e.Direction == "down":
                Log.MigrationRolledBack(logger, e.MigrationId, e.Name, e.DurationMs);
                break;
            case EventTypes.MigrationCompleted:
                Log.MigrationApplied(logger, e.MigrationId, e.Name, e.DurationMs);
                break;
            case EventTypes.MigrationFailed:
                Log.MigrationFailed(logger, e.MigrationId, e.Name, e.Error);
                break;
            case EventTypes.CollectionStarted:
                Log.CollectionChanging(logger, e.Collection);
                break;
            case EventTypes.CollectionCompleted when e.Changes is { Count: > 0 } changes:
                Log.CollectionChanged(logger, e.Collection, e.DurationMs, changes.Count);
                break;
            case EventTypes.CollectionCompleted:
                Log.CollectionInSync(logger, e.Collection);
                break;
            case EventTypes.CollectionFailed:
                Log.CollectionFailed(logger, e.Collection, e.Error);
                break;
            case EventTypes.CollectionSkipped:
                Log.CollectionSkipped(logger, e.Message);
                break;
            case EventTypes.Result when e.Result is { } result:
                if (result.ExitCode == ExitCodes.Success)
                {
                    Log.Message(logger, LogLevel.Information, result.Message ?? "Migrations finished.");
                }
                else
                {
                    Log.MigrationsFailed(logger, result.ExitCode, result.Message);
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
