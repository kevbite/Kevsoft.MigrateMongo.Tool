A migration can record what it did: named values, notes, and every operation it ran with its statement and counts.
These are stored with the migration's state and history, printed after the run, and listed in full by
[`show`](../reference/commands/Command-show.md). That way you can answer "how many documents did that change?" months later.

## A full example

```csharp
[Migration("20261022120000", "tier-customers")]
public sealed class TierCustomers : IMigration
{
    public async Task UpAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        var customers = context.Track(context.Database.GetCollection<BsonDocument>("customers"), "gold");
        await customers.UpdateManyAsync(
            Builders<BsonDocument>.Filter.Gte("spend", 10_000),
            Builders<BsonDocument>.Update.Set("tier", "gold"),
            cancellationToken: cancellationToken);

        var untiered = await context.Database.GetCollection<BsonDocument>("customers")
            .Find(context.Session, Builders<BsonDocument>.Filter.Exists("spend", false))
            .Project(c => c["_id"]).ToListAsync(cancellationToken);
        context.Results.Set("untiered", untiered);
        context.Results.Set("cutoff", DateTime.UtcNow);
        if (untiered.Count > 0)
        {
            context.Results.Note($"{untiered.Count} customers have no spend and were left untiered.", NoteLevel.Warning);
        }
    }

    // ...
}
```

```
✔ 20261022120000 tier-customers   (0.84s, transaction)
    gold      matched 1520 · modified 1518 · upserted 0
    untiered  [2 items]
    cutoff    2026-10-22T12:31:03Z
    1 operation(s) recorded – migrate-mongo show 20261022120000
```

## Named values

`context.Results` holds named values:

| Method | Does |
|---|---|
| `Set(key, value)` | Stores a value: strings, numbers, booleans, dates, `Guid`s, `ObjectId`s, `BsonValue`s, lists and any object the driver can serialize (records, anonymous types, ...). |
| `Increment(key)` | Keeps a count. |
| `Remove(key)` | Takes a value away. |
| `Track(result, label)` | Adds an `UpdateResult`, `ReplaceOneResult`, `DeleteResult` or `BulkWriteResult`'s counts to a value. |

Keys follow MongoDB field-name rules: no `.`, no leading `$`, up to 64 characters.

`Guid`s are stored as standard UUIDs (subtype 4), on their own or in lists and arrays. Inside your own objects and
dictionaries they go through the driver's serializer, which needs a representation configured, e.g.
`BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard))`.

## Notes

`context.Results.Note(message, level)` records a timestamped `Info` or `Warning`, which is also logged. The first 100
are kept, each up to 1,024 characters.

## Tracking operations

`context.Track(collection, label?)` wraps a collection. Its update, replace, delete, insert, bulk write and index
methods pass `context.Session` for you and record each operation's kind, statement (filter, update, index keys and
options), counts and duration. With a label, the counts are also added to that named value.

- Dropping an index records its full definition first, so you can see what to recreate.
- Inserted documents are never recorded, only how many.
- `context.Track(database)` does the same for `RunCommandAsync`.
- `Collection` and `Database` on the wrappers give you the unwrapped objects for anything else.

An operation that throws is recorded too, with its `error`, before the exception is rethrown. A bulk write or
`insertMany` that fails part-way records the counts of what it applied before the error (with `IsOrdered = false`,
everything that didn't fail). Other failed operations record no counts.

## Keeping secrets and personal data out

> [!WARNING]
> **Statements are stored as they were sent.** Tracked filters, updates and commands are written to the migrations
> collections and printed by `show` and `--json`, so anyone who can read those collections or your CI logs can see
> them, including any secrets or personal data in them. A tracked `createUser` or `updateUser` command records its
> password (`pwd`), and a filter on an email address records the address.

For operations like these, either don't track them (run them on `context.Database`, or a tracked collection's
`Collection`), or pass `captureStatement: false` so only the kind, counts and timings are recorded:

```csharp
await context.Track(context.Database, captureStatement: false).RunCommandAsync(
    new BsonDocument { { "createUser", "reporting" }, { "pwd", password }, { "roles", new BsonArray { "read" } } },
    cancellationToken: cancellationToken);
```

To leave statements out everywhere, set [`results.captureStatements: false`](../reference/Configuration.md#results), or
pass `--no-capture-statements` for one run. `show --no-statements` hides them when displaying.

## Limits

Limits keep the state documents small:

- The named values can take up to [`results.maxBytes`](../reference/Configuration.md#results) (64 KB). A migration that
  records more fails with `MM-RESULTS-TOO-LARGE`, listing the largest keys. Put large data in your own collection.
- The first 200 operations are kept, and a statement over 4 KB is left out (`statementTruncated: true`).

## When a migration fails

Results are kept when a migration fails:

- If its transaction was aborted, they're marked as not committed (`resultsCommitted: false`). They show what the
  migration got up to, but none of it was saved.
- If the commit kept failing with `UnknownTransactionCommitResult` until `transactionRetryTimeout`, nobody can tell
  whether it was applied, so they're marked `resultsCommitted: null` and the run says "commit outcome unknown". Check
  the data (and the migration's state) before running it again.
- On a transaction retry, only the last attempt's results are kept.

## Rollbacks

`DownAsync` can record results too. They're stored on the `rolledBack` history entry.

## Reading them back

- [`show <id>`](../reference/commands/Command-show.md) lists everything.
- [`status`](../reference/commands/Command-status.md) and [`history`](../reference/commands/Command-history.md) summarise
  them in a `Results` column.
- They're native BSON in the state collections, so you can query them. See
  [What Gets Stored](../reference/What-Gets-Stored.md#results).
