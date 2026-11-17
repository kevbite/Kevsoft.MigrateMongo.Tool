Shows the state of every migration and collection definition. Read-only.

```
migrate-mongo status [--check]
```

```
$ migrate-mongo status
 ID              Name                      State    Batch  Applied
 20261014164027  initial                   applied  1      2026-10-14 16:40:50
 20261014164028  add-email-index-to-users  pending
 20261014164029  split-full-name           pending
2 pending migration(s).
```

## Options

| Option | Meaning |
|---|---|
| `--check` | Exit with code `2` when there are pending migrations, or a collection that's out of line with its definition. A scheduled run can use it to spot indexes changed by hand. |

## Migration states

| State | Meaning |
|---|---|
| `pending` | In the code, not applied yet. |
| `applied` | Applied. |
| `applying` / `rollingBack` | A run started applying or rolling it back and didn't finish (e.g. the process died outside a transaction). Later runs stop until it's resolved with [`mark`](./Command-mark.md). |
| `failed` | The last attempt threw. |

Applied migrations that are no longer in the code, or whose checksum has changed, are flagged. When a migration has
[results](../../guides/Recording-Results.md), a `Results` column summarises them.

## Collections

With [collection definitions](../../guides/Managing-Collections.md), a second table follows:

```
 Collection  State    Reconciled           Changes
 orders      changes  2026-10-21 09:00:00  1 create · 1 hide
 customers   in sync  2026-10-21 09:00:00  1 kept
```

| State | Meaning |
|---|---|
| `in sync` | The collection matches its definition. |
| `changes` | `up` would change something; [`plan`](./Command-plan.md) shows what. |
| `blocked` | The definition is invalid, or a change it needs isn't allowed. |
| `superseded` | A newer build replaced this definition recently, so this build won't apply it. See [rolling deploys](../../guides/Managing-Collections.md#rolling-deploys). |

## Exit codes

`0`, or `2` with `--check` when there's pending work. See [Exit Codes](../Exit-Codes.md).
