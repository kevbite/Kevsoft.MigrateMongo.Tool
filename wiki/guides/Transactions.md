Where the deployment supports them, each migration runs in its own transaction, with its state document written in the
same transaction. A failure then leaves nothing half-applied.

## When migrations run in a transaction

The [`transaction`](../reference/Configuration.md#migrations) setting decides, and a migration can override it:

| Setting | Behaviour |
|---|---|
| `auto` (default) | Use a transaction when the deployment supports them (replica sets and sharded clusters). Standalone servers run without one. |
| `always` | Fail the migration if transactions aren't available. |
| `never` | Never use a transaction. |

```csharp
[Migration("20261014164028", "add-email-index-to-users", Transaction = TransactionMode.Never)]
```

`TransactionMode.Never` is for operations that can't run in a transaction, such as building indexes on existing
collections or dropping indexes. A tracked `DropIndexAsync` or `DropAllIndexesAsync` in a transaction fails straight
away and says so. `TransactionMode.Always` fails the migration if transactions aren't available.

The output shows which was used: `✔ 20261014164029 split-full-name (0.42s, transaction)`.

## Writing a transactional migration

Pass `context.Session` to every operation, so it's part of the transaction:

```csharp
[Migration("20261014164029", "split-full-name", Transaction = TransactionMode.Always)]
public sealed class SplitFullName : IMigration
{
    public Task UpAsync(MigrationContext context, CancellationToken cancellationToken) =>
        context.Database.GetCollection<BsonDocument>("users").UpdateManyAsync(
            context.Session,
            Builders<BsonDocument>.Filter.Exists("fullName"),
            new EmptyPipelineDefinition<BsonDocument>()
                .AppendStage<BsonDocument, BsonDocument, BsonDocument>(BsonDocument.Parse("""
                    { $set: {
                        firstName: { $arrayElemAt: [ { $split: ["$fullName", " "] }, 0 ] },
                        lastName:  { $arrayElemAt: [ { $split: ["$fullName", " "] }, 1 ] } } }
                    """)),
            cancellationToken: cancellationToken);

    public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) =>
        context.Database.GetCollection<BsonDocument>("users").UpdateManyAsync(
            context.Session,
            Builders<BsonDocument>.Filter.Exists("firstName"),
            Builders<BsonDocument>.Update.Unset("firstName").Unset("lastName"),
            cancellationToken: cancellationToken);
}
```

`context.Session` is `null` when the migration runs without a transaction (with `never`, or with `auto` on a standalone
server), and the driver's session overloads throw `ArgumentNullException` when given `null`. This example sets
`TransactionMode.Always` so there's always a session. For a migration that may run either way:

- use a [tracked collection](./Recording-Results.md), which picks the right overload for you; or
- pick the overload yourself:

```csharp
var users = context.Database.GetCollection<BsonDocument>("users");
var filter = Builders<BsonDocument>.Filter.Exists("firstName", false);
var unsplit = context.Session is { } session
    ? users.Find(session, filter)
    : users.Find(filter);
```

The transaction reads from the primary and commits with majority write concern.

## Retries

- On a `TransientTransactionError` (e.g. a write conflict or a primary election), the transaction is aborted and the
  migration **runs again from the start**.
- On an `UnknownTransactionCommitResult`, only the commit is retried.
- Retries back off from 100ms to 5s and stop after
  [`transactionRetryTimeout`](../reference/Configuration.md#migrations) (default 120 seconds).

> [!IMPORTANT]
> **A transactional migration can run more than once**, so keep any side effects outside the database (HTTP calls,
> files, messages) idempotent.

If the commit keeps failing with `UnknownTransactionCommitResult` until the timeout, nobody can tell whether it was
applied: the run says "commit outcome unknown". Check the data and the migration's state before running it again.

## Without a transaction

A migration that isn't in a transaction has its state set to `applying` before it runs. If the process dies part-way
through, the state stays `applying` and later runs stop until you resolve it with
[`mark`](../reference/commands/Command-mark.md). See [Troubleshooting](./Troubleshooting.md).

## Rollbacks

`DownAsync` runs in a transaction under the same rules as `UpAsync`, and the migration's state is removed in the same
transaction.

## See also

- [Safety](../concepts/Safety.md)
- [Writing Migrations](./Writing-Migrations.md)
