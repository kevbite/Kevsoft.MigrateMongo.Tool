A [collection definition](./Managing-Collections.md) can declare the collection's own options: capped, time series,
clustered, default collation and change stream pre- and post-images.

Every managed collection is created by `up` with an explicit `create` command, with every declared option, before its
indexes. MongoDB would otherwise create it on the first insert or index with default options, and options that can only
be set at creation (time series, clustered) would be missed. An existing collection is adopted as it is.

## Example

```csharp
[ManagedCollection("readings")]
public sealed class Readings : ICollectionDefinition<Reading>
{
    public void Define(CollectionBuilder<Reading> collection)
    {
        collection.Options
            .TimeSeries(r => r.Timestamp, meta: r => r.Sensor, granularity: TimeSeriesGranularity.Minutes)
            .ExpireAfter(TimeSpan.FromDays(90));

        collection.Indexes.Add(k => k.Ascending(r => r.Sensor.Site).Descending(r => r.Timestamp));
    }
}
```

## Options

| Option | On an existing collection |
|---|---|
| `Capped(maxSize, maxDocuments)` | `size` and `max` change in place with `collMod` (6.0+). `size` is rounded up to a multiple of 256, as MongoDB does. A collection that isn't capped is [converted](#converting-to-capped) only when allowed. |
| `TimeSeries(time, meta:, granularity:)` or `TimeSeries(time, meta, bucketMaxSpan, bucketRounding)` | Coarser granularity or bucketing changes in place. Finer, a different `timeField` or `metaField`, or switching to or from time series [can't change](#changes-mongodb-cant-make). Without a granularity, MongoDB uses `seconds`. Custom bucketing needs 6.3+. |
| `ExpireAfter(TimeSpan)` | A time series collection's `expireAfterSeconds`. Changes in place, and removing it turns expiry off. |
| `Clustered(name, expireAfter)` | Clustered on `{ _id: 1 }`. `expireAfter` changes in place, and only expires documents whose `_id` is a date. Switching to or from clustered can't change. |
| `Collation(Collation)` | The default collation, compared once the server has filled in its defaults. Can't change. |
| `ChangeStreamPreAndPostImages(enabled)` | Changes in place (6.0+). |

- Only declared options are compared. An option you don't declare, e.g. pre- and post-images turned on by a platform
  team, is left alone.
- Capped can't be combined with time series or clustered; that makes the definition invalid.
- An option the server is too old for stops the run before anything changes.
- A time series collection's own `{meta}_1_{time}_1` index and a clustered index are never pruned.

## Converting to capped

Declaring `Capped(...)` on an existing collection that isn't capped stops the run with exit code `4`.
`convertToCapped` copies the data into a new collection while holding an exclusive lock on the whole database, keeps
only the newest documents that fit in `maxSize`, and drops every index except `_id_`.

To allow it:

- set [`collections.onConvertToCapped: allow`](../reference/Configuration.md#collections), or
- approve one collection for one run with `--allow-convert-to-capped audit_log`.

`up` then recreates the declared indexes straight after. `collection.Options.OnConvertToCapped` can make a collection
stricter than the config, not looser.

## Changes MongoDB can't make

Some options can't change on an existing collection: switching to or from time series or clustered, `timeField`,
`metaField`, finer granularity, the default collation, and resizing a capped collection before 6.0. `up` never works
around them by copying the data. By default `plan` and `up` stop with exit code `4` before anything changes:

```
  ⟳ readings
      ✖ blocked  timeseries.metaField   sensor → device   can't be changed on an existing collection
```

You can:

- **Write a migration** that copies the data into a new collection and renames it.
- **Skip the change.** Skipping leaves the option as it is, applies the rest of the definition and warns on every run
  until it's resolved. From narrowest to widest:
  - `--skip-unchangeable readings` for one run;
  - `collection.Options.OnUnchangeable = UnchangeableOption.Skip` in the definition, when the new options are for new
    databases only;
  - [`collections.onUnchangeable: skip`](../reference/Configuration.md#collections) in the config, usually per
    environment.

## See also

- [Managing Collections](./Managing-Collections.md)
- [Configuration: collections](../reference/Configuration.md#collections)
