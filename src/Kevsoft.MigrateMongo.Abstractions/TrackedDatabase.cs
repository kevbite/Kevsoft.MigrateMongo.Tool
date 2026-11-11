using System.Diagnostics;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kevsoft.MigrateMongo;

/// <summary>
/// Runs database commands and records each one (the command and its duration) in <see cref="MigrationContext.Results"/>.
/// The migration's transaction session is passed automatically.
/// </summary>
public sealed class TrackedDatabase
{
    private readonly IClientSessionHandle? _session;
    private readonly MigrationResults _results;
    private readonly bool _captureStatement;

    internal TrackedDatabase(IMongoDatabase database, IClientSessionHandle? session, MigrationResults results, string? label, bool captureStatement)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (label is not null)
        {
            MigrationResults.ValidateKey(label);
        }

        Database = database;
        _session = session;
        _results = results;
        Label = label;
        _captureStatement = captureStatement && results.CaptureStatements;
    }

    /// <summary>The underlying database, for operations that don't need recording.</summary>
    public IMongoDatabase Database { get; }

    public string? Label { get; }

    /// <summary>
    /// Runs a command (e.g. <c>collMod</c>) and records it. <paramref name="recordStatement"/> records a richer statement
    /// than the command alone, e.g. the full definition of a search index a drop command only names.
    /// </summary>
    public async Task<BsonDocument> RunCommandAsync(
        BsonDocument command,
        ReadPreference? readPreference = null,
        BsonDocument? recordStatement = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var statement = _captureStatement ? (recordStatement ?? new BsonDocument("command", command.DeepClone())) : null;
        var at = _results.Time.GetUtcNow().UtcDateTime;
        var started = Stopwatch.GetTimestamp();
        BsonDocument result;
        try
        {
            result = await (_session is null
                ? Database.RunCommandAsync<BsonDocument>(command, readPreference, cancellationToken)
                : Database.RunCommandAsync<BsonDocument>(_session, command, readPreference, cancellationToken));
        }
        catch (Exception e)
        {
            Record(command, statement, at, started, e);
            throw;
        }

        Record(command, statement, at, started, null);
        return result;
    }

    private void Record(BsonDocument command, BsonDocument? statement, DateTime at, long started, Exception? error)
    {
        var elapsed = Stopwatch.GetElapsedTime(started);
        var truncated = statement is not null && statement.ToBson().Length > MigrationResults.MaxStatementBytes;
        _results.Record(new TrackedOperation
        {
            Kind = "runCommand",
            Collection = command.ElementCount > 0 && command[0].IsString ? command[0].AsString : null,
            Label = Label,
            Statement = truncated ? null : statement,
            StatementTruncated = truncated,
            Counts = new Dictionary<string, long>(),
            Error = error is null ? null : MigrationResults.DescribeError(error),
            DurationMs = (long)elapsed.TotalMilliseconds,
            At = at,
        });
    }
}
