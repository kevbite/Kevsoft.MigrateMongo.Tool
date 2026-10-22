using Kevsoft.MigrateMongo.Engine;
using Kevsoft.MigrateMongo.Protocol;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Kevsoft.MigrateMongo.MongoDriver;

/// <summary>
/// Stores migration state in <c>{name}</c>, history in <c>{name}.history</c> and the lock in <c>{name}.lock</c>.
/// Documents are mapped by hand (no class maps) so nothing depends on driver serialization conventions.
/// </summary>
public sealed class MongoMigrationStore : IMigrationStore
{
    private const string LockId = "lock";

    private readonly IMongoCollection<BsonDocument> _states;
    private readonly IMongoCollection<BsonDocument> _history;
    private readonly IMongoCollection<BsonDocument> _lock;
    private bool _initialized;

    public MongoMigrationStore(IMongoDatabase database, string stateCollection)
    {
        _states = database.GetCollection<BsonDocument>(stateCollection);
        _history = database.GetCollection<BsonDocument>(stateCollection + ".history");
        _lock = database.GetCollection<BsonDocument>(stateCollection + ".lock");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        // Creating the indexes also creates the collections, so they exist before any transaction writes to them.
        await _states.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(new BsonDocument("batch", 1), new CreateIndexOptions { Name = "batch_1" }),
            cancellationToken: cancellationToken);
        await _history.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(new BsonDocument { { "migrationId", 1 }, { "at", -1 } }, new CreateIndexOptions { Name = "migrationId_1_at_-1" }),
            cancellationToken: cancellationToken);
        await _lock.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(new BsonDocument("expiresAt", 1), new CreateIndexOptions { Name = "expiresAt_1" }),
            cancellationToken: cancellationToken);
        _initialized = true;
    }

    public async Task<IReadOnlyList<StoredMigrationState>> GetStatesAsync(CancellationToken cancellationToken)
    {
        var documents = await _states.Find(FilterDefinition<BsonDocument>.Empty).ToListAsync(cancellationToken);
        return documents.Select(ToState).ToList();
    }

    public Task SaveStateAsync(StoredMigrationState state, IExecutionScope? scope, CancellationToken cancellationToken)
    {
        var filter = new BsonDocument("_id", state.Id);
        var options = new ReplaceOptions { IsUpsert = true };
        var document = ToDocument(state);
        return SessionOf(scope) is { } session
            ? _states.ReplaceOneAsync(session, filter, document, options, cancellationToken)
            : _states.ReplaceOneAsync(filter, document, options, cancellationToken);
    }

    public Task DeleteStateAsync(string id, IExecutionScope? scope, CancellationToken cancellationToken)
    {
        var filter = new BsonDocument("_id", id);
        return SessionOf(scope) is { } session
            ? _states.DeleteOneAsync(session, filter, options: null, cancellationToken)
            : _states.DeleteOneAsync(filter, cancellationToken);
    }

    public Task AppendHistoryAsync(HistoryRecord record, IExecutionScope? scope, CancellationToken cancellationToken)
    {
        var document = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "migrationId", record.MigrationId },
            { "name", record.Name },
            { "event", record.Event },
            { "direction", record.Direction },
            { "runId", record.RunId },
            { "at", record.At },
        };
        AddIfNotNull(document, "batch", record.Batch);
        AddIfNotNull(document, "durationMs", record.DurationMs);
        AddIfNotNull(document, "checksum", record.Checksum);
        AddIfNotNull(document, "error", record.Error);
        AddIfNotNull(document, "host", record.Host);
        MigrationOutputMapper.Write(document, record.Output);

        return SessionOf(scope) is { } session
            ? _history.InsertOneAsync(session, document, options: null, cancellationToken)
            : _history.InsertOneAsync(document, options: null, cancellationToken);
    }

    public async Task<IReadOnlyList<HistoryRecord>> GetHistoryAsync(string? migrationId, int limit, CancellationToken cancellationToken)
    {
        var filter = migrationId is null ? new BsonDocument() : new BsonDocument("migrationId", migrationId);
        var documents = await _history.Find(filter)
            .Sort(new BsonDocument { { "at", -1 }, { "_id", -1 } })
            .Limit(limit)
            .ToListAsync(cancellationToken);

        return documents.AsEnumerable().Reverse().Select(d => new HistoryRecord
        {
            MigrationId = d["migrationId"].AsString,
            Name = GetString(d, "name") ?? "",
            Event = GetString(d, "event") ?? "",
            Direction = GetString(d, "direction") ?? "",
            RunId = GetString(d, "runId") ?? "",
            At = d["at"].ToUniversalTime(),
            Batch = d.TryGetValue("batch", out var batch) && batch.IsNumeric ? batch.ToInt32() : null,
            DurationMs = d.TryGetValue("durationMs", out var duration) && duration.IsNumeric ? duration.ToInt64() : null,
            Checksum = GetString(d, "checksum"),
            Error = GetString(d, "error"),
            Host = GetString(d, "host"),
            Output = MigrationOutputMapper.Read(d),
        }).ToList();
    }

    public async Task<bool> TryAcquireLockAsync(LockInfo lockInfo, DateTime now, CancellationToken cancellationToken)
    {
        var document = new BsonDocument
        {
            { "_id", LockId },
            { "runId", lockInfo.RunId },
            { "host", (BsonValue?)lockInfo.Host ?? BsonNull.Value },
            { "acquiredAt", lockInfo.AcquiredAt },
            { "expiresAt", lockInfo.ExpiresAt },
            { "heartbeatAt", lockInfo.AcquiredAt },
        };

        try
        {
            await _lock.InsertOneAsync(document, options: null, cancellationToken);
            return true;
        }
        catch (MongoWriteException e) when (e.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
        }

        // Take over a lock whose holder stopped renewing it.
        var expired = new BsonDocument { { "_id", LockId }, { "expiresAt", new BsonDocument("$lt", now) } };
        var takeover = new BsonDocument("$set", new BsonDocument(document.Where(e => e.Name != "_id")));
        var result = await _lock.FindOneAndUpdateAsync<BsonDocument>(
            expired,
            takeover,
            new FindOneAndUpdateOptions<BsonDocument> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
        return result is not null;
    }

    public async Task<bool> RenewLockAsync(string runId, DateTime expiresAt, CancellationToken cancellationToken)
    {
        var result = await _lock.UpdateOneAsync(
            new BsonDocument { { "_id", LockId }, { "runId", runId } },
            new BsonDocument("$set", new BsonDocument { { "expiresAt", expiresAt }, { "heartbeatAt", DateTime.UtcNow } }),
            options: null,
            cancellationToken);
        return result.MatchedCount == 1;
    }

    public Task ReleaseLockAsync(string runId, CancellationToken cancellationToken) =>
        _lock.DeleteOneAsync(new BsonDocument { { "_id", LockId }, { "runId", runId } }, cancellationToken);

    public async Task<LockInfo?> GetLockAsync(CancellationToken cancellationToken)
    {
        var document = await _lock.Find(new BsonDocument("_id", LockId)).FirstOrDefaultAsync(cancellationToken);
        return document is null
            ? null
            : new LockInfo
            {
                RunId = GetString(document, "runId") ?? "",
                Host = GetString(document, "host"),
                AcquiredAt = document["acquiredAt"].ToUniversalTime(),
                ExpiresAt = document["expiresAt"].ToUniversalTime(),
            };
    }

    public async Task<bool> DeleteLockAsync(DateTime? expiredBefore, CancellationToken cancellationToken)
    {
        var filter = new BsonDocument("_id", LockId);
        if (expiredBefore is not null)
        {
            filter.Add("expiresAt", new BsonDocument("$lt", expiredBefore.Value));
        }

        var result = await _lock.DeleteOneAsync(filter, cancellationToken);
        return result.DeletedCount == 1;
    }

    private static IClientSessionHandle? SessionOf(IExecutionScope? scope) =>
        scope is MongoExecutionScope { Transactional: true } mongoScope ? mongoScope.Session : null;

    private static BsonDocument ToDocument(StoredMigrationState state)
    {
        var document = new BsonDocument
        {
            { "_id", state.Id },
            { "v", state.V },
            { "name", state.Name },
            { "source", state.Source },
            { "origin", state.Origin },
            { "checksum", state.Checksum },
            { "state", state.State },
            { "direction", state.Direction },
            { "batch", state.Batch },
            { "transactional", state.Transactional },
        };
        AddIfNotNull(document, "appliedAt", state.AppliedAt);
        document.Add("updatedAt", state.UpdatedAt);
        AddIfNotNull(document, "durationMs", state.DurationMs);
        document.Add("runId", state.RunId);
        AddIfNotNull(document, "error", state.Error);

        var appliedBy = new BsonDocument
        {
            { "host", state.AppliedBy.Host },
            { "user", state.AppliedBy.User },
        };
        AddIfNotNull(appliedBy, "tool", state.AppliedBy.Tool);
        appliedBy.Add("library", state.AppliedBy.Library);
        appliedBy.Add("driver", state.AppliedBy.Driver);
        appliedBy.Add("runtime", state.AppliedBy.Runtime);
        document.Add("appliedBy", appliedBy);
        MigrationOutputMapper.Write(document, state.Output);
        return document;
    }

    private static StoredMigrationState ToState(BsonDocument d)
    {
        var appliedBy = d.TryGetValue("appliedBy", out var by) && by.IsBsonDocument ? by.AsBsonDocument : new BsonDocument();
        return new StoredMigrationState
        {
            Id = d["_id"].ToString()!,
            V = d.TryGetValue("v", out var v) && v.IsNumeric ? v.ToInt32() : 1,
            Name = GetString(d, "name") ?? "",
            Source = GetString(d, "source") ?? "",
            Origin = GetString(d, "origin") ?? "",
            Checksum = GetString(d, "checksum") ?? "",
            State = GetString(d, "state") ?? "",
            Direction = GetString(d, "direction") ?? "up",
            Batch = d.TryGetValue("batch", out var batch) && batch.IsNumeric ? batch.ToInt32() : 0,
            Transactional = d.TryGetValue("transactional", out var tx) && tx.IsBoolean && tx.AsBoolean,
            AppliedAt = d.TryGetValue("appliedAt", out var appliedAt) && appliedAt.IsValidDateTime ? appliedAt.ToUniversalTime() : null,
            UpdatedAt = d.TryGetValue("updatedAt", out var updatedAt) && updatedAt.IsValidDateTime ? updatedAt.ToUniversalTime() : default,
            DurationMs = d.TryGetValue("durationMs", out var duration) && duration.IsNumeric ? duration.ToInt64() : null,
            RunId = GetString(d, "runId") ?? "",
            Error = GetString(d, "error"),
            AppliedBy = new Provenance
            {
                Host = GetString(appliedBy, "host") ?? "",
                User = GetString(appliedBy, "user") ?? "",
                Tool = GetString(appliedBy, "tool"),
                Library = GetString(appliedBy, "library") ?? "",
                Driver = GetString(appliedBy, "driver") ?? "",
                Runtime = GetString(appliedBy, "runtime") ?? "",
            },
            Output = MigrationOutputMapper.Read(d),
        };
    }

    private static string? GetString(BsonDocument document, string name) =>
        document.TryGetValue(name, out var value) && value.IsString ? value.AsString : null;

    private static void AddIfNotNull(BsonDocument document, string name, object? value)
    {
        switch (value)
        {
            case null:
                return;
            case string s:
                document.Add(name, s);
                break;
            case int i:
                document.Add(name, i);
                break;
            case long l:
                document.Add(name, l);
                break;
            case DateTime dt:
                document.Add(name, dt);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value.GetType(), "Unsupported value type.");
        }
    }
}
