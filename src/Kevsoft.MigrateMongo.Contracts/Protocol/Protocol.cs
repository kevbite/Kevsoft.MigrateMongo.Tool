using System.Text.Json.Serialization;
using Kevsoft.MigrateMongo.Config;

namespace Kevsoft.MigrateMongo.Protocol;

public static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int PendingMigrations = 2;
    public const int LockTimeout = 3;
    public const int ValidationFailed = 4;
    public const int ProtocolMismatch = 5;
    public const int BuildFailed = 6;
}

public static class ProtocolVersions
{
    public const int Current = 1;

    public static readonly int[] Supported = [1];

    public const string HandshakeArgument = "--mm-handshake";
    public const string ProtocolArgument = "--mm-protocol";
}

/// <summary>
/// Optional features a runner advertises in its handshake. Older runners ignore request fields they don't know, so the
/// tool must check for a feature before sending a request that relies on it.
/// </summary>
public static class RunnerFeatures
{
    /// <summary><see cref="CommandArguments.DryRun"/> for <c>up</c> and <c>down</c>.</summary>
    public const string DryRun = "dryRun";

    public static readonly string[] Supported = [DryRun];
}

/// <summary>Printed (as one JSON line) by <c>runner --mm-handshake</c>.</summary>
public sealed class HandshakeInfo
{
    public int[] Protocol { get; set; } = [];

    /// <summary>See <see cref="RunnerFeatures"/>. Null for runners from before features were advertised.</summary>
    public string[]? Features { get; set; }

    public string Library { get; set; } = "";

    public string Driver { get; set; } = "";

    public string Runtime { get; set; } = "";
}

/// <summary>Sent by the tool as the first line of the runner's stdin.</summary>
public sealed class RunnerRequest
{
    public int Protocol { get; set; } = ProtocolVersions.Current;

    public string Command { get; set; } = "";

    public CommandArguments Arguments { get; set; } = new();

    public ResolvedConfig Config { get; set; } = new();

    public string? Tool { get; set; }
}

/// <summary>Control messages sent by the tool on stdin after the request.</summary>
public sealed class RunnerControl
{
    public string Type { get; set; } = "";
}

public sealed class CommandArguments
{
    public string? To { get; set; }

    public int? Count { get; set; }

    public bool Batch { get; set; }

    public string? Id { get; set; }

    /// <summary>For <c>mark</c>: true for <c>--applied</c>, false for <c>--unapplied</c>.</summary>
    public bool? Applied { get; set; }

    public bool Force { get; set; }

    public bool Check { get; set; }

    public int? Limit { get; set; }

    /// <summary>For <c>up</c> and <c>down</c>: report what would run without changing anything.</summary>
    public bool DryRun { get; set; }
}

public static class EventTypes
{
    public const string Log = "log";
    public const string LockWaiting = "lockWaiting";
    public const string LockAcquired = "lockAcquired";
    public const string MigrationStarted = "migrationStarted";
    public const string MigrationCompleted = "migrationCompleted";
    public const string MigrationFailed = "migrationFailed";
    public const string Result = "result";
}

/// <summary>One NDJSON line written by the runner.</summary>
public sealed class RunnerEvent
{
    public string Type { get; set; } = "";

    public string? Level { get; set; }

    public string? Message { get; set; }

    public string? MigrationId { get; set; }

    public string? Name { get; set; }

    public string? Direction { get; set; }

    public long? DurationMs { get; set; }

    public bool? Transactional { get; set; }

    public string? Error { get; set; }

    public CommandResult? Result { get; set; }
}

public sealed class CommandResult
{
    public int ExitCode { get; set; }

    public string? Message { get; set; }

    public int? Batch { get; set; }

    public List<MigrationStatusInfo>? Migrations { get; set; }

    public List<PlannedMigration>? Plan { get; set; }

    public List<string>? Applied { get; set; }

    public List<string>? RolledBack { get; set; }

    public List<ValidationIssue>? Issues { get; set; }

    public List<HistoryRecord>? History { get; set; }

    public LockInfo? Lock { get; set; }
}

public sealed class MigrationStatusInfo
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary><c>pending</c>, <c>applied</c>, <c>applying</c>, <c>rollingBack</c> or <c>failed</c>.</summary>
    public string State { get; set; } = "";

    public bool InCode { get; set; }

    public int? Batch { get; set; }

    public DateTime? AppliedAt { get; set; }

    public string? Checksum { get; set; }

    public bool ChecksumMismatch { get; set; }

    public bool? Transactional { get; set; }

    public string? Error { get; set; }

    public string? RunId { get; set; }

    public string? Host { get; set; }
}

public sealed class PlannedMigration
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Direction { get; set; } = "";

    public bool Transactional { get; set; }
}

public sealed class ValidationIssue
{
    /// <summary><c>error</c> or <c>warning</c>.</summary>
    public string Severity { get; set; } = "error";

    /// <summary>e.g. <c>checksumMismatch</c>, <c>missing</c>, <c>outOfOrder</c>, <c>blocked</c>, <c>unsupportedState</c>.</summary>
    public string Kind { get; set; } = "";

    public string? MigrationId { get; set; }

    public string Message { get; set; } = "";
}

public sealed class HistoryRecord
{
    public string MigrationId { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary><c>applied</c>, <c>rolledBack</c>, <c>failed</c>, <c>markedApplied</c>, <c>markedUnapplied</c> or <c>baselined</c>.</summary>
    public string Event { get; set; } = "";

    public string Direction { get; set; } = "";

    public string RunId { get; set; } = "";

    public int? Batch { get; set; }

    public DateTime At { get; set; }

    public long? DurationMs { get; set; }

    public string? Checksum { get; set; }

    public string? Error { get; set; }

    public string? Host { get; set; }
}

public sealed class LockInfo
{
    public string RunId { get; set; } = "";

    public string? Host { get; set; }

    public DateTime AcquiredAt { get; set; }

    public DateTime ExpiresAt { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(HandshakeInfo))]
[JsonSerializable(typeof(RunnerRequest))]
[JsonSerializable(typeof(RunnerControl))]
[JsonSerializable(typeof(RunnerEvent))]
public sealed partial class ProtocolJsonContext : JsonSerializerContext;
