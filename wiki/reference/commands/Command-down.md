Rolls back applied migrations by running their `DownAsync`, newest first. With no options, it rolls back the newest
applied migration.

```
migrate-mongo down [--count <n> | --batch | --to <id>] [--dry-run] [--no-capture-statements]
```

```
$ migrate-mongo down
Acquired lock (run ff6217413f1343948fb456e1ff2e435a).
◀ 20261014164029 split-full-name
✔ 20261014164029 split-full-name   (0.03s, transaction)
Rolled back 1 migration(s).
```

## Options

Use at most one of these:

| Option | Rolls back |
|---|---|
| `-n`, `--count <n>` | The newest `n` migrations (at least 1). |
| `--batch` | Everything from the last `up`. |
| `--to <id>` | Everything after `<id>` (not `<id>` itself). |

And any of:

| Option | Meaning |
|---|---|
| `--dry-run` | Show what would be rolled back without changing anything. See [dry runs](./Command-up.md#dry-runs). |
| `--no-capture-statements` | Record [tracked operations](../../guides/Recording-Results.md) without their statements, for this run. |

```
$ migrate-mongo down --batch --dry-run
  ◀ 20261014164029  split-full-name   (transaction)
  ◀ 20261014164028  add-email-index-to-users   (no transaction)
Dry run: would roll back 2 migration(s); nothing was changed.
```

## Notes

- `down` only rolls back migrations. Collection definitions describe the current state, so to undo a definition
  change, change the definition back and run `up`.
- A rolled-back migration's state is removed, so the next `up` applies it again. Its history keeps an entry for the
  rollback, with any [results](../../guides/Recording-Results.md) `DownAsync` recorded.
- `DownAsync` runs in a transaction under the same rules as `UpAsync`. See [Transactions](../../guides/Transactions.md).

## Exit codes

`0` on success, `1` if a rollback fails, `3` if the lock isn't acquired in time, `4` if validation fails. See
[Exit Codes](../Exit-Codes.md).
