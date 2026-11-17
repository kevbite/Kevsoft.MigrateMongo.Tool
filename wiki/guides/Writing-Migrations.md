A migration is a C# class that changes your data once, in order, and records that it ran. Use one for data changes
(backfills, renames, splitting fields) and one-off operations. For indexes, options, validators and views, consider a
[collection definition](./Managing-Collections.md) instead; see
[Migrations vs Collection Definitions](../concepts/Migrations-vs-Collection-Definitions.md).

## Creating one

```
$ migrate-mongo create AddEmailIndexToUsers
Created src/MyApp.Migrations/Migrations/M20261014164028_AddEmailIndexToUsers.cs
```

The id is the current UTC time (`yyyyMMddHHmmss`), and migrations run in id order. The new file contains commented
examples of [tracking operations and recording results](./Recording-Results.md). Pass `--minimal` for bare `UpAsync`
and `DownAsync` methods instead, or [use your own template](./Custom-Templates.md). See
[`create`](../reference/commands/Command-create.md) for all options.

## Anatomy of a migration

```csharp
using Kevsoft.MigrateMongo;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyApp.Migrations.Migrations;

[Migration("20261014164028", "add-email-index-to-users", Transaction = TransactionMode.Never)]
public sealed class AddEmailIndexToUsers : IMigration
{
    public async Task UpAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        await Users(context).CreateIndexAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("email"),
                new CreateIndexOptions { Unique = true, Name = "email_1" }),
            cancellationToken: cancellationToken);

        context.Logger.LogInformation("Created unique index on users.email");
    }

    public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) =>
        Users(context).DropIndexAsync("email_1", cancellationToken: cancellationToken);

    // Records each operation, so `migrate-mongo show 20261014164028` lists what ran.
    private static TrackedCollection<BsonDocument> Users(MigrationContext context) =>
        context.Track(context.Database.GetCollection<BsonDocument>("users"));
}
```

Taking each part in turn:

- **`[Migration(id, name)]`** marks the class. The id must be a unique 14-digit timestamp and the name must not be
  empty; the source generator enforces both at compile time (see [Build Errors](../reference/Build-Errors.md)).
- **`Transaction`** (optional) overrides the [`transaction`](../reference/Configuration.md#migrations) setting for this
  migration. Creating an index on an existing collection can't run in a transaction, hence `Never` here. See
  [Transactions](./Transactions.md).
- **`IMigration`** has two methods. `UpAsync` applies the change, and `DownAsync` undoes it for
  [`down`](../reference/commands/Command-down.md). If a migration can't be undone, throw from `DownAsync`.
- **`MigrationContext`** gives you the database and everything else you need (below).
- **`context.Track(...)`** wraps a collection so each operation is recorded and visible in
  [`show`](../reference/commands/Command-show.md). It's optional. See [Recording Results](./Recording-Results.md).

Migrations can also be records, and can take constructor dependencies; see
[Dependency Injection](./Dependency-Injection.md).

## `MigrationContext`

| Member | Meaning |
|---|---|
| `Database` | The configured `IMongoDatabase`. |
| `Session` | The `IClientSessionHandle` when the migration runs in a transaction, otherwise `null`. Pass it to every operation. |
| `Logger` | An `ILogger`. Output appears in the tool's output (`info: ...`) or your app's logs. |
| `Services` | The `IServiceProvider` for the run. |
| `Run` | The run id, batch, direction and environment. |
| `Results` | Named values and notes to store with this run. See [Recording Results](./Recording-Results.md). |
| `Track(collection)`, `Track(database)` | Wrappers that run operations and record what they did. |

## Running it

```
migrate-mongo status   # see what's pending
migrate-mongo plan     # see what up would do, without changing anything
migrate-mongo up       # apply it
migrate-mongo down     # roll back the newest migration
```

Each `up` applies every pending migration as one **batch**. `down` rolls back only the newest applied migration by
default; use `down --batch` to roll back the whole of the last batch.
See [`up`](../reference/commands/Command-up.md) and [`down`](../reference/commands/Command-down.md).

## Don't edit applied migrations

A migration's checksum is taken from its source code, ignoring whitespace and comments. If an applied migration is
edited, [`validate`](../reference/commands/Command-validate.md) and `up` report it (exit code `4`) and stop, depending on
[`onChecksumMismatch`](../reference/Configuration.md#migrations). Write a new migration instead. See
[Safety](../concepts/Safety.md#checksums).

Similarly, don't delete an applied migration's class, and don't add a migration with an id older than one that's
already applied; both are reported by `validate`.

## Tips

- **Make migrations safe to re-run where you can.** A [transactional migration](./Transactions.md) can run more than
  once on a transient error, and a non-transactional one that fails part-way leaves whatever it already did.
- **Filter on what you're changing**, e.g. `Filter.Exists("fullName")`, so a second run is a no-op.
- **Keep them independent of your app's current model.** A migration runs against the data as it was at that point,
  so use `BsonDocument` or types private to the migration rather than your app's entity classes, which will change.
- **Pass the `CancellationToken` to every driver call.** Ctrl+C, `SIGTERM` and losing the lock cancel it. Projects
  from `init` and `dotnet new migratemongo` make
  [CA2016](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2016) a warning, so missing
  ones are pointed out. See [Safety](../concepts/Safety.md#cancellation).

## See also

- [Transactions](./Transactions.md)
- [Recording Results](./Recording-Results.md)
- [Getting Started](../getting-started/Getting-Started.md)
