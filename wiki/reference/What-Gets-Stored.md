The state of a database's migrations and collection definitions is kept in the database itself, in collections named
after [`stateCollection`](./Configuration.md#migrations) (`_migrations` by default).

| Collection | Contents |
|---|---|
| `_migrations` | One document per migration, keyed by id, with its state, batch, checksum, timings and who applied it. |
| `_migrations.history` | An append-only log of every apply, rollback, failure and `mark`, and every collection change (`kind: "collection"`). |
| `_migrations.lock` | The lock document while a run is in progress, with its owner, expiry and heartbeat. |
| `_migrations.collections` | One document per [collection definition](../guides/Managing-Collections.md), keyed by collection name: the declaration last applied, the one it replaced and when, the indexes it hid, and the last run's operations. |

## A migration's state

```js
{
  _id: "20261014164028", v: 1, name: "add-email-index-to-users", source: "csharp",
  origin: "MyApp.Migrations.Migrations.AddEmailIndexToUsers",
  checksum: "sha256:253b18c7...", state: "applied", direction: "up", batch: 2, transactional: false,
  appliedAt: ISODate("2026-10-14T16:40:51.567Z"), durationMs: 119, runId: "ba6b5b60...",
  appliedBy: { host: "build-agent-12", user: "runner", tool: "migrate-mongo/0.2.0 (linux-x64; aot)",
               library: "0.2.0", driver: "3.12.0", runtime: "10.0.12" }
}
```

Before a migration runs, its state is set to `applying` (or `rollingBack`). If the process dies part-way through a
migration that isn't in a transaction, the state stays there and later runs stop until you resolve it with
[`mark`](./commands/Command-mark.md).

## Results

A migration that [records results](../guides/Recording-Results.md) also has these fields, on its state document and its
history entry. Values are stored as native BSON, so you can query them:

```js
{
  results: { gold: { matched: Long(1520), modified: Long(1518), upserted: Long(0) },
             untiered: [ObjectId("64b7f0c2..."), ObjectId("64b7f0c2...")], cutoff: ISODate("2026-10-22T12:31:03Z") },
  notes: [ { at: ISODate("2026-10-22T12:31:03Z"), level: "warning", message: "2 customers have no spend and..." } ],
  resultsCommitted: true,
  operations: [
    { kind: "updateMany", collection: "customers", label: "gold",
      filter: { spend: { $gte: 10000 } }, update: { $set: { tier: "gold" } },
      counts: { matched: Long(1520), modified: Long(1518), upserted: Long(0) },
      durationMs: Long(611), at: ISODate("2026-10-22T12:31:03Z") }
  ]
}
```

`notesTruncated` and `operationsTruncated` count what went over the limits, and `statementTruncated: true` marks an
operation whose statement was too large to keep. A failed operation has an `error` with the exception's type and
message.

## Querying it

```js
// who applied what, most recent first
db.getCollection("_migrations.history").find({}, { _id: 0, at: 1, migrationId: 1, event: 1, "appliedBy.host": 1 }).sort({ at: -1 })

// migrations that left a warning
db._migrations.find({ "notes.level": "warning" }, { name: 1, notes: 1 })
```

Don't change these collections by hand; use [`mark`](./commands/Command-mark.md), [`baseline`](./commands/Command-baseline.md)
and [`unlock`](./commands/Command-unlock.md).

## Consistency

These collections are always read from and written to the primary with majority read and write concerns, whatever the
connection string (or your app's `IMongoClient`) asks for, so a lagging secondary can't hide an applied migration. The
lock expires by the database server's clock, so agents whose clocks disagree still agree on when it has expired.
