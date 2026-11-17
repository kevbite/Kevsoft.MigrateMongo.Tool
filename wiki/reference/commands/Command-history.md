Shows the audit log: every apply, rollback, failure, mark and baseline, newest last. Read-only.

```
migrate-mongo history [<id>] [--limit <n>]
```

```
$ migrate-mongo history
 At                   ID              Name                      Event       Batch  Run       Host
 2026-10-14 16:40:50  20261014164027  initial                   applied     1      a621daa9  build-agent-12
 2026-10-14 16:40:51  20261014164028  add-email-index-to-users  applied     2      ba6b5b60  build-agent-12
 2026-10-14 16:40:51  20261014164029  split-full-name           applied     2      ba6b5b60  build-agent-12
 2026-10-14 16:40:54  20261014164029  split-full-name           rolledBack  2      ff621741  kev-laptop
```

## Arguments and options

| | Meaning |
|---|---|
| `<id>` | Only show this migration. |
| `--limit <n>` | The number of entries to show (default 50, at least 1). |

Events are `applied`, `rolledBack`, `failed`, `markedApplied`, `markedUnapplied` and `baselined`. When any entry has
[results](../../guides/Recording-Results.md), a `Results` column summarises them.

The history lives in the `_migrations.history` collection, so you can also query it directly. See
[What Gets Stored](../What-Gets-Stored.md). For everything about one migration, use [`show`](./Command-show.md).
