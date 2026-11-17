A [collection definition](./Managing-Collections.md) declares a collection's indexes, and `up` creates, changes and
removes indexes to match.

```csharp
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

`Add(keys, options?)` takes the driver's index keys builder and `CreateIndexOptions<TDocument>`. Names are optional: a
new index without one gets MongoDB's default name (`companyId_1_createdAt_-1`).

## How indexes are matched

An index is identified by its **keys, collation and partial filter**: MongoDB only allows two indexes on the same keys if
those differ. A declared index matches an existing one with the same identity whatever either is called, so existing
databases are adopted as they are.

MongoDB fills in every collation field an index isn't given, with defaults that depend on the locale (`da` puts upper
case first, for example). Each declared collation is compared with the server's after the server has filled it in the
same way (`explain` shows it without changing anything), so going back from `strength: 2` to the default is a change.
On a collection created with a default collation, an index declared without a collation gets the collection's, and is
compared with it. Declare `Collation.Simple` for an index without one.

## What `up` does

Once matched, the other options are compared:

| Change | What `up` does |
|---|---|
| Declared, not on the server | Creates it. |
| TTL or `hidden` changed | `collMod`, in place. |
| TTL added | `collMod`, in place, on a single-field index (MongoDB 5.1+). A compound or wildcard index is rebuilt. |
| Made unique | `collMod` with `prepareUnique`, then `unique` (MongoDB 6.0+). Fails, leaving the index as it was, if there are duplicates. `prepareUnique` is turned off again, also when the run is cancelled part-way. |
| Any other option changed, or the TTL removed | [Rebuilds](#rebuilds) it: drops the index and creates it again. |
| Same name, different keys | Drops it and creates the declared one. (Creating it would fail with `IndexKeySpecsConflict`.) |
| Different keys, no name | The new index is created alongside, and the old one is pruned. |
| On the server, not declared | Depends on `prune`. See [undeclared indexes](#undeclared-indexes). |

Changes run in that order: new indexes first, removals last. Every operation is recorded like a migration's
[tracked operations](./Recording-Results.md#tracking-operations), including the full definition of anything dropped. If
creating the replacement in a rebuild fails or is cancelled (Ctrl+C, or the lock is lost), the dropped index is
restored.

> [!TIP]
> Making an index unique fails if there are duplicates. Write a migration that removes them: migrations run before
> collection definitions in the same `up`.

## Rebuilds

A rebuild drops the index and creates it again. An index without a declared name keeps the existing name when it's
rebuilt, so queries that hint it by name keep working. One with a declared name is rebuilt under that name, and the plan
shows the rename, e.g. `name reference_1 → reference_unique`.

Either way **the collection is without the index** (and, for a unique index, without the constraint) while it builds.
To refuse that, set [`indexes.onRebuild: fail`](../reference/Configuration.md#indexes) (per environment if you like).
`plan` and `up` then stop with exit code `4` before changing anything:

```
  ⟳ orders
      ↻ rebuild  reference_1   sparse: false → true (drop, then create)   ✖ blocked: indexes.onRebuild is fail
error: orders: rebuilding reference_1 drops it before the new one builds, and indexes.onRebuild is fail. A new name
alone won't build it alongside the old one: MongoDB doesn't allow two indexes with the same keys, collation and partial
filter. Give the declared index a new name and a partial filter or collation, or run with --allow-rebuild
orders.reference_1.
```

Your options:

- **Approve it once**: `--allow-rebuild orders.reference_1`.
- **Build the new index alongside the old one.** It needs a name and an identity of its own. When the keys, collation
  or partial filter change, a new name is enough. When only other options change, as with `sparse` here, add a partial
  filter or collation too, e.g. a `$exists` partial filter in place of `sparse`. The old index is then undeclared, so
  [`prune`](#undeclared-indexes) decides what happens to it.

A collection has only one text index, so a text index can only be rebuilt.

A definition can make this stricter, not looser: `collection.Indexes.OnRebuild = IndexRebuild.Fail`, or
`Add(..., onRebuild: IndexRebuild.Fail)` for one index.

## Undeclared indexes

[`indexes.prune`](../reference/Configuration.md#indexes) decides what happens to an index on the server that isn't
declared:

| `prune` | An index on the server that isn't declared |
|---|---|
| `report` (default) | Is left alone and listed as `keep`. |
| `hide` | Is hidden from the query planner (still maintained, so unhiding is instant), then dropped once it has been hidden for `dropHiddenAfter`. Declaring it again unhides it. |
| `drop` | Is dropped, its definition recorded first. |

`hide` is the safe way to remove indexes: if something turns out to need one, declaring it again brings it back
instantly.

- `collection.Indexes.Prune` overrides the config for one collection.
- `Ignore("pattern")` leaves matching names alone (`*` matches anything), e.g. indexes created by hand or by Atlas's
  Performance Advisor.
- The `_id_` index is never touched.

A declared index never changes an ignored one: if it has the same name, the same keys with other options, or both are
text indexes, the collection is blocked until one of them changes. One that's exactly the same is fine.

## Adopting existing indexes

Declare the indexes that already exist, run `plan`, and check it shows nothing to change for them. Keep `prune: report`
until the plan only lists the indexes you expect. See
[Adopting an Existing Database](./Adopting-an-Existing-Database.md).

## See also

- [Managing Collections](./Managing-Collections.md)
- [Search Indexes](./Search-Indexes.md)
- [Configuration: indexes](../reference/Configuration.md#indexes)
