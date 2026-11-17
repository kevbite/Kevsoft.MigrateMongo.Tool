`migrate-mongo` has two ways to change a database. They work together, and `up` runs both.

| | [Migration](../guides/Writing-Migrations.md) | [Collection definition](../guides/Managing-Collections.md) |
|---|---|---|
| Describes | A change: *do this once*. | A desired state: *this is what it should look like*. |
| Runs | Once, in id order, then recorded as applied. | On every `up`, making only the changes needed. |
| Good for | Data: backfills, renames, splitting fields, seeding, one-off commands. | Indexes, search indexes, collection options, validators, views. |
| Editing it later | Not allowed: [write a new one](./Safety.md#checksums). | Expected: edit it and `up` works out the difference. |
| Undo | `DownAsync`, via `down`. | Change the definition back. |
| Fresh database | Every migration runs, in order. | Created straight in its final shape. |

## Why indexes belong in definitions

Indexes written as migrations pile up: create, then drop and recreate with an option, then another... A fresh database
replays all of them, and nothing tells you what the indexes *should* be today. A definition is one place that says
what they are, and `up`:

- creates, modifies and removes indexes to match, in a safe order (new first, removals last);
- adopts existing indexes that already match, whatever they're called;
- shows exactly what will change with `plan`;
- detects drift with `status --check`, e.g. an index added by hand.

## When you need both

Migrations run first in the same `up`, under the same lock, so a migration can prepare the data a definition needs:

- **Making an index unique:** a migration removes duplicates, then the definition adds `Unique = true`.
- **Adding a validator:** a migration fixes documents that would fail it.
- **Renaming a field:** a migration renames it in the documents, and the definition's index moves to the new field.
- **Changing an option MongoDB can't change** (e.g. converting to time series): a migration copies the data into a new
  collection. See [Collection Options](../guides/Collection-Options.md#changes-mongodb-cant-make).

## Running them separately

`up --only migrations` and `up --only collections` split them, e.g. migrations at app startup and long index builds
from a deploy step. See [Running from Your Application](../guides/Running-from-Your-Application.md#collection-definitions-at-startup).
