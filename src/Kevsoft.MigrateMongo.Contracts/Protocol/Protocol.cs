using System.Text.Json;
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
    public const int Current = 2;

    public static readonly int[] Supported = [2];

    public const string HandshakeArgument = "--mm-handshake";
    public const string ProtocolArgument = "--mm-protocol";
}

/// <summary>Printed (as one JSON line) by <c>runner --mm-handshake</c>.</summary>
public sealed class HandshakeInfo
{
    public int[] Protocol { get; set; } = [];

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

    /// <summary>For <c>up</c> and <c>down</c>: don't record the statements run through <c>context.Track(...)</c>.</summary>
    public bool NoCaptureStatements { get; set; }

    /// <summary>For <c>show</c>: leave the recorded statements out.</summary>
    public bool NoStatements { get; set; }
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

    /// <summary>On <c>migrationCompleted</c> and <c>migrationFailed</c>: what the migration recorded.</summary>
    public MigrationOutputInfo? Output { get; set; }

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

    /// <summary>The <c>show</c> command's result.</summary>
    public ShowResult? Show { get; set; }
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

    /// <summary>What the last run recorded. <c>status</c> leaves out the operations; <c>show</c> includes them.</summary>
    public MigrationOutputInfo? Output { get; set; }
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

    /// <summary>What the migration recorded during this run, if anything.</summary>
    public MigrationOutputInfo? Output { get; set; }
}

/// <summary>
/// What a migration recorded with <c>context.Results</c> and <c>context.Track(...)</c>. Values are MongoDB Extended
/// JSON: canonical in-process, relaxed (plain numbers, ISO dates) on the runner's JSON output.
/// </summary>
public sealed class MigrationOutputInfo
{
    /// <summary>The named results, as an Extended JSON document (keys in the order they were first set).</summary>
    public JsonElement? Results { get; set; }

    public List<ResultNoteInfo>? Notes { get; set; }

    /// <summary>How many notes were dropped after the limit was reached.</summary>
    public int? NotesTruncated { get; set; }

    public List<OperationRecord>? Operations { get; set; }

    /// <summary>How many operations weren't recorded after the limit was reached.</summary>
    public int? OperationsTruncated { get; set; }

    /// <summary>
    /// Whether the migration's changes were persisted: always true on success; on failure true only when the migration
    /// wasn't transactional (a failed transaction is rolled back).
    /// </summary>
    public bool Committed { get; set; }
}

public sealed class ResultNoteInfo
{
    public DateTime At { get; set; }

    /// <summary><c>info</c> or <c>warning</c>.</summary>
    public string Level { get; set; } = "info";

    public string Message { get; set; } = "";
}

/// <summary>An operation run through <c>context.Track(...)</c>.</summary>
public sealed class OperationRecord
{
    /// <summary>e.g. <c>updateMany</c>, <c>deleteOne</c>, <c>insertMany</c>, <c>bulkWrite</c>, <c>createIndex</c>, <c>dropIndex</c>, <c>runCommand</c>.</summary>
    public string Kind { get; set; } = "";

    public string? Collection { get; set; }

    public string? Label { get; set; }

    /// <summary>
    /// The statement as an Extended JSON document with any of <c>filter</c>, <c>update</c>, <c>replacement</c>,
    /// <c>pipeline</c>, <c>keys</c>, <c>index</c>, <c>indexes</c>, <c>requests</c>, <c>command</c> and <c>options</c>.
    /// Null when statements aren't captured.
    /// </summary>
    public JsonElement? Statement { get; set; }

    /// <summary>True when the statement was too large to keep.</summary>
    public bool? StatementTruncated { get; set; }

    /// <summary>e.g. <c>matched</c>, <c>modified</c>, <c>upserted</c>, <c>deleted</c>, <c>inserted</c>, <c>created</c>, <c>dropped</c>.</summary>
    public Dictionary<string, long>? Counts { get; set; }

    public long DurationMs { get; set; }

    public DateTime At { get; set; }
}

/// <summary>Everything known about one migration (the <c>show</c> command).</summary>
public sealed class ShowResult
{
    /// <summary>The migration's state; <see cref="MigrationStatusInfo.Output"/> includes the operations.</summary>
    public MigrationStatusInfo Migration { get; set; } = new();

    public string? Source { get; set; }

    public string? Origin { get; set; }

    /// <summary>Direction of the last operation (<c>up</c> or <c>down</c>).</summary>
    public string? Direction { get; set; }

    public long? DurationMs { get; set; }

    /// <summary>The checksum of the migration in the code, when it's still there.</summary>
    public string? CodeChecksum { get; set; }

    public string? User { get; set; }

    public string? Tool { get; set; }

    public string? Library { get; set; }

    public string? Driver { get; set; }

    /// <summary>The most recent history entries for the migration, oldest first.</summary>
    public List<HistoryRecord> History { get; set; } = [];
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
