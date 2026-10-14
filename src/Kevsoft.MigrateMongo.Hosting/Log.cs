using Microsoft.Extensions.Logging;

namespace Kevsoft.MigrateMongo.Hosting;

internal static partial class Log
{
    [LoggerMessage(EventId = 1, Message = "{Message}")]
    public static partial void Message(ILogger logger, LogLevel level, string? message);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Applying migration {MigrationId} {MigrationName}")]
    public static partial void MigrationApplying(ILogger logger, string? migrationId, string? migrationName);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Applied migration {MigrationId} {MigrationName} in {DurationMs}ms")]
    public static partial void MigrationApplied(ILogger logger, string? migrationId, string? migrationName, long? durationMs);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Rolling back migration {MigrationId} {MigrationName}")]
    public static partial void MigrationRollingBack(ILogger logger, string? migrationId, string? migrationName);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Rolled back migration {MigrationId} {MigrationName} in {DurationMs}ms")]
    public static partial void MigrationRolledBack(ILogger logger, string? migrationId, string? migrationName, long? durationMs);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Migration {MigrationId} {MigrationName} failed: {Error}")]
    public static partial void MigrationFailed(ILogger logger, string? migrationId, string? migrationName, string? error);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "Migrations failed (exit code {ExitCode}): {Message}")]
    public static partial void MigrationsFailed(ILogger logger, int exitCode, string? message);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information, Message = "Tunnel: {Tunnel}")]
    public static partial void TunnelOpened(ILogger logger, string tunnel);

    [LoggerMessage(EventId = 9, Level = LogLevel.Warning, Message = "Assembly {Assembly} doesn't contain any migrations.")]
    public static partial void AssemblyHasNoMigrations(ILogger logger, string? assembly);
}
