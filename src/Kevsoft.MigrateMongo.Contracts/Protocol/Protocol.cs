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
    public const int Current = 1;

    public static readonly int[] Supported = [1];

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

    /// <summary>For <c>up</c> and <c>plan</c>: <c>migrations</c> or <c>collections</c> to do only that part (default both).</summary>
    public string? Only { get; set; }

    /// <summary>For <c>up</c> and <c>plan</c>: indexes (<c>collection.index</c>) that may be rebuilt even when <c>onRebuild</c> is <c>fail</c>.</summary>
    public List<string>? AllowRebuild { get; set; }

    /// <summary>For <c>up</c> and <c>plan</c>: collections that may change even though none of their existing indexes match the declared ones.</summary>
    public List<string>? AllowUnmatched { get; set; }

    /// <summary>For <c>up</c> and <c>plan</c>: apply collection definitions that a newer run has already replaced.</summary>
    public bool AllowSuperseded { get; set; }
}

public static class OnlyScopes
{
    public const string Migrations = "migrations";
    public const string Collections = "collections";
}

public static class EventTypes
{
    public const string Log = "log";
    public const string LockWaiting = "lockWaiting";
    public const string LockAcquired = "lockAcquired";
    public const string MigrationStarted = "migrationStarted";
    public const string MigrationCompleted = "migrationCompleted";
    public const string MigrationFailed = "migrationFailed";
    public const string CollectionStarted = "collectionStarted";
    public const string CollectionCompleted = "collectionCompleted";
    public const string CollectionFailed = "collectionFailed";
    public const string CollectionSkipped = "collectionSkipped";
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

    /// <summary>On the <c>collection*</c> events: the collection.</summary>
    public string? Collection { get; set; }

    /// <summary>On <c>collectionCompleted</c>: the changes that were made.</summary>
    public List<IndexChangeInfo>? Changes { get; set; }

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

    /// <summary>For <c>status</c>, <c>plan</c> and <c>up --dry-run</c>: each collection definition and what it would change.</summary>
    public List<CollectionInfo>? Collections { get; set; }

    /// <summary>For <c>up</c>: the collections whose indexes were changed.</summary>
    public List<string>? Reconciled { get; set; }

    /// <summary>The <c>show</c> command's result for a collection.</summary>
    public CollectionInfo? ShowCollection { get; set; }
}

public static class CollectionStates
{
    /// <summary>The indexes match the definition.</summary>
    public const string InSync = "inSync";

    /// <summary><c>up</c> would change something.</summary>
    public const string Changes = "changes";

    /// <summary>A change isn't allowed (e.g. a rebuild with <c>onRebuild: fail</c>), or the definition is invalid.</summary>
    public const string Blocked = "blocked";

    /// <summary>A newer run has replaced this definition; <c>up</c> skips it.</summary>
    public const string Superseded = "superseded";
}

/// <summary>A collection definition and how the database compares with it.</summary>
public sealed class CollectionInfo
{
    public string Name { get; set; } = "";

    public string? Source { get; set; }

    public string? Origin { get; set; }

    /// <summary><c>inSync</c>, <c>changes</c>, <c>blocked</c> or <c>superseded</c>.</summary>
    public string State { get; set; } = "";

    /// <summary>
    /// What <c>up</c> would do (or, in an event, did). <c>keep</c> entries are undeclared indexes left alone; they aren't changes.
    /// </summary>
    public List<IndexChangeInfo> Changes { get; set; } = [];

    /// <summary>Why the definition is blocked, if it is.</summary>
    public List<string>? Problems { get; set; }

    /// <summary>A hash of what the definition declares.</summary>
    public string? Declaration { get; set; }

    /// <summary>When <c>up</c> last changed the collection or recorded a new declaration.</summary>
    public DateTime? ReconciledAt { get; set; }

    public string? RunId { get; set; }

    public string? Host { get; set; }

    public string? User { get; set; }

    public long? DurationMs { get; set; }

    /// <summary>For <c>show</c>: the indexes on the server and how each relates to the definition.</summary>
    public List<IndexInfo>? Indexes { get; set; }

    /// <summary>For <c>show</c>: what the last run that changed something recorded.</summary>
    public MigrationOutputInfo? Output { get; set; }

    /// <summary>For <c>show</c>: the most recent history entries, oldest first.</summary>
    public List<HistoryRecord>? History { get; set; }
}

public static class IndexActions
{
    public const string Create = "create";

    /// <summary>Changed in place with <c>collMod</c>.</summary>
    public const string Modify = "modify";

    /// <summary>Dropped, then created again.</summary>
    public const string Rebuild = "rebuild";

    public const string Hide = "hide";
    public const string Drop = "drop";

    /// <summary>Not declared, and left alone.</summary>
    public const string Keep = "keep";
}

/// <summary>One change to a collection's indexes.</summary>
public sealed class IndexChangeInfo
{
    /// <summary><c>create</c>, <c>modify</c>, <c>rebuild</c>, <c>hide</c>, <c>drop</c> or <c>keep</c>.</summary>
    public string Action { get; set; } = "";

    /// <summary>The index's name: the server's for an existing index, otherwise the declared or default one.</summary>
    public string Index { get; set; } = "";

    /// <summary>The index keys, as Extended JSON.</summary>
    public JsonElement? Keys { get; set; }

    /// <summary>What changes, e.g. <c>expireAfterSeconds 2592000 → 604800</c> or <c>not declared</c>.</summary>
    public string? Detail { get; set; }

    /// <summary>True when the change isn't allowed; see <see cref="Reason"/>.</summary>
    public bool? Blocked { get; set; }

    public string? Reason { get; set; }
}

/// <summary>An index on the server (the <c>show</c> command).</summary>
public sealed class IndexInfo
{
    public string Name { get; set; } = "";

    public JsonElement? Keys { get; set; }

    /// <summary>The options that matter, e.g. <c>{ unique: true }</c>, as Extended JSON.</summary>
    public JsonElement? Options { get; set; }

    /// <summary><c>declared</c>, <c>undeclared</c>, <c>ignored</c> or <c>default</c> (the <c>_id_</c> index).</summary>
    public string Status { get; set; } = "";

    /// <summary>For a declared index: the name it was declared with, when the server's differs.</summary>
    public string? DeclaredName { get; set; }

    /// <summary>For an undeclared index this tool hid: when it was hidden.</summary>
    public DateTime? HiddenAt { get; set; }
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
    /// <summary>The migration id, or for a collection definition (<see cref="Kind"/> <c>collection</c>) the collection name.</summary>
    public string MigrationId { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary><c>collection</c> for a collection definition; null for a migration.</summary>
    public string? Kind { get; set; }

    /// <summary>
    /// <c>applied</c>, <c>rolledBack</c>, <c>failed</c>, <c>markedApplied</c>, <c>markedUnapplied</c> or <c>baselined</c>;
    /// <c>reconciled</c> or <c>failed</c> for a collection definition.
    /// </summary>
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
