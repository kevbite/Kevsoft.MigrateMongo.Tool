You can start using `migrate-mongo` on a database that already exists, whether it was changed by hand, by scripts or by
another migration tool. Existing data is never touched until you write a migration that changes it.

## 1. Set up the project

Follow [Project Setup](../getting-started/Project-Setup.md) and point `migrate-mongo.yaml` at the database. Check you can
connect:

```
migrate-mongo status
```

The first run that writes creates the [state collections](../reference/What-Gets-Stored.md) (`_migrations`,
`_migrations.history`, ...) next to your data.

## 2. Capture what's already there (optional)

If you're moving from another tool or have scripts that were already run, you can add them as migrations so a fresh
database (e.g. a developer's or a test one) gets the same changes. Create one migration per change, in the order they
were made:

```
migrate-mongo create AddCustomerIndexes
migrate-mongo create BackfillOrderTotals
```

Then record them as applied on the existing database, **without running them**, with
[`baseline`](../reference/commands/Command-baseline.md):

```
$ migrate-mongo baseline --to 20261014164029
Baselined 2 migration(s) in batch 1.
```

Every migration up to and including `--to` is recorded as applied, with a `baselined` history entry. New migrations
created after it run normally on the next `up`. On a fresh database, `up` runs them all.

To record a single migration as applied, use [`mark <id> --applied`](../reference/commands/Command-mark.md) instead.

## 3. Adopt indexes and collections (optional)

[Collection definitions](./Managing-Collections.md) adopt existing collections and indexes as they are: a declared index
matches an existing one with the same keys, collation and partial filter, whatever either is called.

1. Declare the indexes that exist today. `migrate-mongo show <collection>` lists what's on the server.
2. Run [`plan`](../reference/commands/Command-plan.md). Each declared index that matches shows no change; anything
   listed as `create` or `modify` differs from the server.
3. Fix the definition until the plan shows only the changes you intend.
4. Keep the defaults while adopting: [`indexes.prune: report`](./Indexes.md#undeclared-indexes) and
   `collections.prune: off` never remove anything, they only list `keep`. Tighten them once the plan is clean.

With [typed definitions](./Managing-Collections.md#typed-definitions), make sure your class maps are registered, or every
index looks new. A collection where no declared index uses any field the existing ones use is blocked until you run with
`--allow-unmatched <collection>`, as a guard.

## 4. Gate deployments

Add `validate` and `plan` to your pipeline. See [Running in CI](./Running-in-CI.md).

## See also

- [`baseline`](../reference/commands/Command-baseline.md), [`mark`](../reference/commands/Command-mark.md)
- [Indexes](./Indexes.md)
