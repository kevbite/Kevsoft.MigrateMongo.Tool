Shows what [`up`](./Command-up.md) would do, without changing anything. Read-only.

```
migrate-mongo plan [--only migrations|collections] [--allow-* ...]
```

```
$ migrate-mongo plan
  ▶ 20261014164028  add-email-index-to-users   (no transaction)
  ▶ 20261014164029  split-full-name   (transaction)
  ⟳ orders
      + create   companyId_1_createdAt_-1   { companyId: 1, createdAt: -1 }
      ~ modify   closedAt_1                 expireAfterSeconds 2592000 → 604800
      - hide     legacy_ref                 not declared
      ! keep     status_1                   not declared (prune: report)
  ⟳ customers   in sync
Would apply 2 migration(s) as batch 2 and change 1 collection(s).
```

It takes `up`'s [`--only` and `--allow-*` options](./Command-up.md#options), so you can check exactly what a run with
them would do.

While migrations are pending, the collection changes are worked out against the database as it is now, so they may
change once the migrations have run.

## Exit codes

| Code | When |
|---|---|
| `0` | Nothing to do. |
| `2` | There are migrations to apply or collection changes to make. Use it to gate a deployment. |
| `4` | A collection change isn't allowed (e.g. a rebuild with `indexes.onRebuild: fail`). |

## `plan` or `up --dry-run`?

Both show the same thing. `plan` exits `2` when there's pending work; [`up --dry-run`](./Command-up.md#dry-runs)
exits with the code a real `up` would (`0` when it would succeed).
