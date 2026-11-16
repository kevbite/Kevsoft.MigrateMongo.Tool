Shows everything about one migration or collection definition. Read-only.

```
migrate-mongo show <id|collection> [--history <n>] [--no-statements]
```

## Arguments and options

| | Meaning |
|---|---|
| `<id>` | A migration id, or a unique prefix of one (e.g. `20261022` for a migration created that day). Or a collection name. |
| `--history <n>` | The number of history entries to show (default 10, `0` for none). |
| `--no-statements` | Leave out the recorded filters, updates and commands, e.g. when sharing the output. |

## A migration

Its state, who applied it, its [results, notes and operations](../../guides/Recording-Results.md), and its history:

```
$ migrate-mongo show 20261022
20261022120000 tier-customers
  State     applied (batch 4, up, transaction)
  Applied   2026-10-22 12:31:03 by runner@build-agent-12 (0.84s)
  Checksum  sha256:253b18c7...
  Source    csharp MyApp.Migrations.Migrations.TierCustomers
  Versions  tool 0.2.0, library 0.2.0, driver 3.12.0
  Run       ba6b5b60f25e4861a4ba25597d63fd8d

Results
  gold      { matched: 1520, modified: 1518, upserted: 0 }
  untiered  [ObjectId("64b7f0c2a1b2c3d4e5f60718"), ObjectId("64b7f0c2a1b2c3d4e5f60719")]
  cutoff    2026-10-22T12:31:03Z

Notes
  12:31:03  warning  2 customers have no spend and were left untiered.

Operations
  1. updateMany customers [gold]  matched 1520 · modified 1518 · upserted 0  (0.61s)
       filter: { spend: { $gte: 10000 } }
       update: { $set: { tier: "gold" } }

History
 At                   ID              Name            Event    Batch  Run       Host            Error  Results
 2026-10-22 12:31:03  20261022120000  tier-customers  applied  4      ba6b5b60  build-agent-12         gold: matched 1520 · modified 1518 · upserted 0, untiered: …
```

A rolled-back migration is still found through its history, by its id or a unique prefix, even once its code has been
deleted.

## A collection

`show <collection>` does the same for a [collection definition](../../guides/Managing-Collections.md): each index on
the server and how it relates to the definition, the changes `up` would make, the operations of the last run that
changed something, and its history.

```
$ migrate-mongo show orders
orders (collection)
  State      in sync
  Reconciled 2026-10-29 10:12:41 by runner@build-agent-12 (38.40s)
  Declared   sha256:6d0c41...
  Source     csharp MyApp.Migrations.Collections.Orders

Indexes
  _id_                      { _id: 1 }
  companyId_1_createdAt_-1  { companyId: 1, createdAt: -1 }
  legacy_ref                { reference: 1 }  unique
  status_1                  { status: 1 }  hidden  undeclared, hidden 2026-10-29 10:12
  adhoc_x                   { x: 1 }  ignored
```

> [!WARNING]
> Statements are shown as they were sent, including any personal data or secrets in them. See
> [Recording Results](../../guides/Recording-Results.md#keeping-secrets-and-personal-data-out).
