Once your collections are declared with [collection definitions](./Managing-Collections.md), the tool can report or
remove collections and views that no definition declares, e.g. leftovers from features that were removed.

## Settings

[`collections.prune`](../reference/Configuration.md#collections) decides what happens to a collection or view in the
database that no definition declares:

| `collections.prune` | |
|---|---|
| `off` (default) | Nothing; they aren't listed. |
| `report` | Listed as `keep` with a warning. |
| `drop` | Views are dropped. Collections are dropped only if they're empty; one with documents is kept and reported. The `listCollections` entry and indexes are recorded in history first. |

```yaml
collections:
  prune: report
  ignore: [ "audit_*", "tmp_*" ]
```

## What's never touched

- Names matching `collections.ignore`.
- The state collections (`<stateCollection>` and `<stateCollection>.*`). See
  [What Gets Stored](../reference/What-Gets-Stored.md).
- `system.*` collections.
- Anything a declared or kept view depends on, directly or indirectly: a view's source as well as the collections and
  views reached through `$lookup`, `$graphLookup`, `$unionWith` and `$facet` in its pipeline, following the chain
  through intermediate views.

These are shown as `(undeclared)` in `plan`, `status` and `up`.

## A safe way to turn it on

1. Set `prune: report` and run [`plan`](../reference/commands/Command-plan.md) to see what's undeclared.
2. Declare the collections you need, and add the rest to `ignore` or delete them.
3. Switch to `prune: drop` once the report is clean, if you want empty leftovers removed automatically.

`up` counts a collection's documents again just before dropping it and keeps it if it isn't empty. The count and the
drop are separate commands, though, and the migration lock doesn't stop your application writing, so a document
inserted between them is dropped with the collection. Before using `prune: drop`, stop anything writing to the
collections it would drop, or add collections that are in use to `ignore`.

## See also

- [Managing Collections](./Managing-Collections.md)
- [Views](./Views.md)
