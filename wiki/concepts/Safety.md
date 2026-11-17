How `migrate-mongo` keeps concurrent runs, edited code, crashes and cancellations from corrupting your database or its
record of what ran.

## Locking

Every command that changes something (`up`, `down`, `mark`, `baseline`) first takes a lock: a document in
`_migrations.lock`. Only one run, from the tool or any [app instance](../guides/Running-from-Your-Application.md), can
hold it.

- **Waiting:** a run that finds the lock held waits up to [`lockWaitTimeout`](../reference/Configuration.md#migrations)
  (60 seconds), showing who holds it, then fails with exit code `3`.
- **Renewal:** the lock lasts `lockTtl` seconds (300) and is renewed while the run is in progress, so long migrations
  keep it.
- **Crashes:** if a run dies, its lock expires `lockTtl` seconds later, so nothing is stuck for good. The expiry is
  checked by the database server's clock, so agents whose clocks disagree still agree on when it has expired.
- **Losing it:** if a run can't renew its lock (e.g. it lost its connection for too long), it cancels the running
  migration and stops, rather than carry on alongside another run.
- **Removing one:** [`unlock`](../reference/commands/Command-unlock.md) removes an expired lock, and `unlock --force`
  any lock, but only use that if you're sure its run has stopped.

Many app instances starting at once is safe: one applies the migrations, the others wait and then find nothing to do.

## Checksums

Each migration's checksum is taken from its source code, ignoring whitespace and comments, and stored when it's
applied. If an applied migration's code changes, [`validate`](../reference/commands/Command-validate.md), `plan` and
`up` report it, because the database no longer matches what the code says happened.

| [`onChecksumMismatch`](../reference/Configuration.md#migrations) | |
|---|---|
| `fail` (default) | Stop with exit code `4`. |
| `warn` | Warn and carry on. |
| `ignore` | Say nothing. |

To accept a deliberate edit, run [`mark <id> --applied`](../reference/commands/Command-mark.md), which stores the new
checksum. Otherwise revert it and write a new migration.

## Ordering

Migrations run in id order. A pending migration older than the newest applied one (e.g. from a branch merged late) is
reported by `validate` and stops `up`, because it would run after migrations that were written later. Set
[`allowOutOfOrder: true`](../reference/Configuration.md#migrations) to run it anyway. An applied migration whose code
has gone is reported too.

## Transactions

Where the deployment supports them, each migration runs in its own transaction, together with the write of its state.
A failure leaves nothing half-applied: the migration is still pending and can simply run again. Transient errors are
retried. See [Transactions](../guides/Transactions.md).

Without a transaction, a migration's state is set to `applying` before it runs. If it fails, or the process dies, the
state says so (`failed` or `applying`) and later runs stop until someone checks the data and resolves it with
[`mark`](../reference/commands/Command-mark.md). Nothing guesses. See [Troubleshooting](../guides/Troubleshooting.md).

## Cancellation

Ctrl+C and `SIGTERM` cancel the running migration through its `CancellationToken`, so pass it to every driver call.

- The lock is kept until the migration's code returns, even if it ignores the token, and is then released.
- A transactional migration is rolled back.
- One outside a transaction that ran to the end is recorded as applied, so a cancel doesn't leave a finished migration
  marked `failed`.

Collection changes are cancelled safely too: if creating the replacement in an index rebuild is cancelled, the dropped
index is restored.

## Consistent state

The state, history and lock collections are always read from and written to the primary with majority read and write
concerns, whatever the connection string (or your app's `IMongoClient`) asks for, so a lagging secondary can't hide an
applied migration. See [What Gets Stored](../reference/What-Gets-Stored.md).

## Seeing before doing

- [`plan`](../reference/commands/Command-plan.md) shows what `up` would do and exits `2` if there's anything.
- [`up --dry-run`](../reference/commands/Command-up.md#dry-runs) goes through `up` without changing anything.
- Collection changes that would lose something (rebuilding an index, converting to capped, a validator existing
  documents fail) can be made to stop and ask for an explicit `--allow-*` flag. See
  [Troubleshooting](../guides/Troubleshooting.md#a-collection-change-is-blocked).
