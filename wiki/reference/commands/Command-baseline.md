Records every migration up to and including `--to` as applied, without running it.

```
migrate-mongo baseline --to <id>
```

```
$ migrate-mongo baseline --to 20261014164029
Baselined 3 migration(s) in batch 1.
```

## Options

| Option | Meaning |
|---|---|
| `--to <id>` | Required. The last migration to record as applied. |

Use it to adopt a database that already has those changes, e.g. one that was migrated by hand or by another tool.
Each baselined migration gets a `baselined` history entry. See
[Adopting an Existing Database](../../guides/Adopting-an-Existing-Database.md).
