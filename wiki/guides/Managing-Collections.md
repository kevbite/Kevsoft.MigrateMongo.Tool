A [migration](./Writing-Migrations.md) runs once. A **collection definition** describes what a collection should look
like *now*, and every `up` makes only the changes needed to get there: the collection itself and its
[options](./Collection-Options.md), [validator](./Validators.md), [indexes](./Indexes.md) and
[search indexes](./Search-Indexes.md). [Views](./Views.md) are declared the same way. Collection definitions need
MongoDB 5.0 or later.

Not sure which to use? See [Migrations vs Collection Definitions](../concepts/Migrations-vs-Collection-Definitions.md).

## A first definition

```csharp
using Kevsoft.MigrateMongo;
using MongoDB.Bson;
using MongoDB.Driver;

[ManagedCollection("orders")]
public sealed class Orders : ICollectionDefinition<BsonDocument>
{
    public void Define(CollectionBuilder<BsonDocument> collection) =>
        collection.Indexes
            .Add(k => k.Ascending("companyId").Descending("createdAt"))
            .Add(k => k.Ascending("reference"), new() { Unique = true })
            .Add(k => k.Ascending("closedAt"), new() { ExpireAfter = TimeSpan.FromDays(30) })
            .Add(k => k.Ascending("customer"), new() { Name = "customer_ci", Collation = new Collation("en", strength: CollationStrength.Secondary) })
            .Ignore("adhoc_*");
}
```

- **`[ManagedCollection("orders")]`** names the collection. One class per collection: two classes for the same
  collection is a [build error](../reference/Build-Errors.md).
- **`ICollectionDefinition<TDocument>`** has one method, `Define`, that describes the collection on the builder.
  `TDocument` can be `BsonDocument` or your own class (see [typed definitions](#typed-definitions)).
- **`collection.Indexes`**, **`collection.SearchIndexes`**, **`collection.Options`** and **`collection.Validator`**
  declare each part. Index options are the driver's own `CreateIndexOptions<TDocument>`.

Definitions live in the migrations project next to the migrations, and can take constructor dependencies the same way
(see [Dependency Injection](./Dependency-Injection.md)).

## Seeing and applying changes

```
$ migrate-mongo plan
  ⟳ orders
      + create   companyId_1_createdAt_-1   { companyId: 1, createdAt: -1 }
      ~ modify   closedAt_1                 expireAfterSeconds 2592000 → 604800
      ! keep     status_1                   not declared (prune: report)
  ⟳ customers   in sync
Would change 1 collection(s).
```

[`plan`](../reference/commands/Command-plan.md) shows what [`up`](../reference/commands/Command-up.md) would change
without changing anything, and exits with `2` when there are changes. [`status`](../reference/commands/Command-status.md)
lists each collection's state, and [`show orders`](../reference/commands/Command-show.md) shows its indexes and history.

Existing databases are adopted as they are: an existing collection or index that matches the definition is left alone,
whatever it's called. See [how indexes are matched](./Indexes.md#how-indexes-are-matched).

## When they run

`up` applies pending migrations first, then each collection definition, under the same lock. A migration can prepare
the data a definition needs, e.g. removing duplicates before an index becomes unique. `up --to` skips the definitions
while later migrations are still pending.

`--only migrations` and `--only collections` split the two, e.g. migrations at startup and collection definitions from
a deploy step where long index builds don't matter:

```
migrate-mongo up --only migrations
migrate-mongo up --only collections
```

A definition that's invalid, or a change that isn't allowed, stops `up` before any migration runs, unless migrations
run first in the same `up` and could change the indexes. Then every definition is checked again after the migrations,
and `up` stops before changing any collection. With `--only collections` no migration runs first, so `up` stops before
changing anything.

A definition covers a collection in the configured database only.

## Typed definitions

With a document class, keys are rendered through the class maps and conventions registered in the process:

```csharp
[ManagedCollection("customers")]
public sealed class Customers : ICollectionDefinition<Customer>
{
    public void Define(CollectionBuilder<Customer> collection) =>
        collection.Indexes.Add(k => k.Ascending(c => c.Email), new() { Unique = true });
}
```

Register them before the runner starts, in the migrations project's `Program.cs`:

```csharp
MyAppMongoConfiguration.Register();
return await MigrateMongoRunner.RunAsync(args);
```

If they aren't registered, the field names render differently and every index looks new. As a guard, a collection where
none of the declared indexes use a field that any existing index uses is blocked until you check the plan and run with
`--allow-unmatched <collection>`. (Changing the keys of an index on the same fields doesn't trip it.)

A typed definition follows the model: renaming a property that changes the stored field name changes the index too.
That's usually a data change as well (existing documents still have the old field), so pair it with a migration that
renames the field. Migrations themselves are best written against `BsonDocument`, so a later model change doesn't alter
what an old one does on a fresh database.

## Rolling deploys

During a rolling deploy, an instance of the previous build can start after the new one has run. Each collection
remembers the definition it replaced, and for [`indexes.supersededWindow`](../reference/Configuration.md#indexes) (an
hour by default) after the replacement, a run that would bring that one back is skipped with a warning instead of undoing
the newer one. `status` shows it as `superseded`.

After that window, going back to it is treated as a deliberate change. `--allow-superseded` applies it straight away,
e.g. when rolling back on purpose. Set `supersededWindow: 0` to turn this off.

## Configuration

Each part has its own section in `migrate-mongo.yaml`, and most can be set per environment:
[`indexes`](../reference/Configuration.md#indexes), [`searchIndexes`](../reference/Configuration.md#searchindexes),
[`collections`](../reference/Configuration.md#collections) and [`validators`](../reference/Configuration.md#validators).
A definition can usually make its own collection stricter than the config, but not looser.

## See also

- [Indexes](./Indexes.md), [Search Indexes](./Search-Indexes.md), [Collection Options](./Collection-Options.md),
  [Validators](./Validators.md), [Views](./Views.md), [Undeclared Collections](./Undeclared-Collections.md)
- [What Gets Stored](../reference/What-Gets-Stored.md)
