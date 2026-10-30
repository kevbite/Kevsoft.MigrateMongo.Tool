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
                Log.MigrationFailed(logger, e.MigrationId, e.Name, e.Error, e.Exception);
                break;
            case EventTypes.Result when e.Result is { } result:
                foreach (var issue in result.Issues ?? [])
                {
                    // A run stopped by errors already lists them in its message.
                    if (result.Message?.Contains(issue.Message, StringComparison.Ordinal) != true)
                    {
                        Log.Message(logger, issue.Severity == "error" ? LogLevel.Error : LogLevel.Warning, issue.Message);
                    }
                }

                if (result.ExitCode is ExitCodes.Success or ExitCodes.PendingMigrations)
                {
                    // history and show results have nothing to summarise.
                    if (result.Message is { } message)
                    {
                        Log.Message(logger, LogLevel.Information, message);
                    }
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
