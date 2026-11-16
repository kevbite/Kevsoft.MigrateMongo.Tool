Applies every pending migration as one batch, then brings each collection in line with its definition.

```
migrate-mongo up [--to <id>] [--dry-run] [--no-capture-statements] [--only migrations|collections] [--allow-* ...]
```

```
$ migrate-mongo up
Acquired lock (run ba6b5b60f25e4861a4ba25597d63fd8d).
▶ 20261014164028 add-email-index-to-users
  info: Created unique index on users.email
✔ 20261014164028 add-email-index-to-users   (0.13s)
▶ 20261014164029 split-full-name
✔ 20261014164029 split-full-name   (0.02s, transaction)
⟳ orders
  + create   companyId_1_createdAt_-1   { companyId: 1, createdAt: -1 }
  ~ modify   closedAt_1                 expireAfterSeconds 2592000 → 604800
✔ orders   (38.40s)
✔ customers   (in sync)
Applied 2 migration(s) in batch 2 and changed 1 collection(s).
```

## Options

| Option | Meaning |
|---|---|
| `--to <id>` | Stop after this migration. Collection definitions are skipped while later migrations are still pending. |
| `--dry-run` | Show what would happen without changing anything. See [below](#dry-runs). |
| `--no-capture-statements` | Record [tracked operations](../../guides/Recording-Results.md) without their filters, updates and commands, for this run. |
| `--only migrations` | Apply migrations only. |
| `--only collections` | Apply collection definitions only (warns if migrations are pending). |
| `--allow-rebuild <collection.index>` | Let this index be [rebuilt](../../guides/Indexes.md#rebuilds) even with `onRebuild: fail`. |
| `--allow-unmatched <collection>` | Change this collection although none of the declared indexes use a field its existing indexes use. See [typed definitions](../../guides/Managing-Collections.md#typed-definitions). |
| `--allow-superseded` | Apply definitions a newer run replaced less than `supersededWindow` ago. See [rolling deploys](../../guides/Managing-Collections.md#rolling-deploys). |
| `--allow-convert-to-capped <collection>` | [Convert this existing collection to capped](../../guides/Collection-Options.md#converting-to-capped) even with `onConvertToCapped: fail`. |
| `--skip-unchangeable <collection>` | Leave the [options MongoDB can't change](../../guides/Collection-Options.md#changes-mongodb-cant-make) on this collection as they are, with a warning, and apply the rest. |
| `--allow-invalid-documents <collection>` | Apply this collection's [validator](../../guides/Validators.md) even though existing documents fail it, with `onInvalidDocuments: fail`. |

Each `--allow-*` and `--skip-*` option approves one item for one run. Repeat it for more, e.g.
`--allow-rebuild orders.reference_1 --allow-rebuild orders.status_1`.

## Order of work

1. Take the [lock](../../concepts/Safety.md#locking), waiting up to `lockWaitTimeout`.
2. Check every collection definition. One that's invalid, or needs a change that isn't allowed, stops `up` before any
   migration runs, unless migrations run first and could change it; then it's checked again after them.
3. Apply pending migrations in id order, each in a [transaction](../../guides/Transactions.md) where possible.
4. Bring each collection in line with its definition: create the collection with its declared options if it's
   missing, then make the other changes, new indexes first and removals last. Views are applied after every
   collection, and a view on another view after the one it reads from.
5. Release the lock.

## Dry runs

`up --dry-run` (with any of its other options) shows what would happen:

```
$ migrate-mongo up --dry-run
  ▶ 20261014164028  add-email-index-to-users   (no transaction)
  ⟳ orders
      + create   companyId_1_createdAt_-1   { companyId: 1, createdAt: -1 }
Dry run: would apply 1 migration(s) as batch 2 and change 1 collection(s); nothing was changed.
```

A dry run makes the same checks as a real run (validation, blocked migrations, unknown `--to`, transaction support)
and exits with the code a real run would. It never runs migration code, takes the lock or writes anything, so it works
with a read-only user. If another run holds the lock, it warns instead of waiting. Unlike [`plan`](./Command-plan.md),
it exits `0` when there's pending work.

## Exit codes

`0` on success, `1` if a migration fails, `3` if the lock isn't acquired in time, `4` if validation fails or a
collection change isn't allowed, `6` if the build fails. See [Exit Codes](../Exit-Codes.md).
