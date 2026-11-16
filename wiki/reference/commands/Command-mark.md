Sets one migration's state by hand.

```
migrate-mongo mark <id> --applied | --unapplied
```

```bash
migrate-mongo mark 20261014164029 --applied     # it finished, or accept an edited migration's new checksum
migrate-mongo mark 20261014164029 --unapplied   # it didn't; run it again next time
```

## Options

Use exactly one:

| Option | Meaning |
|---|---|
| `--applied` | Record the migration as applied, with its current checksum. |
| `--unapplied` | Remove the migration's state, so the next `up` runs it. |

## When to use it

- **A migration failed or crashed part-way outside a transaction** and is stuck in `failed`, `applying` (or
  `rollingBack`), which stops later runs. Look at the data, then mark it `--applied` if it finished, or `--unapplied` if
  it should run again (after undoing what it did, if it isn't safe to repeat).
- **An applied migration was edited** on purpose and you want to accept the new code: `--applied` stores its new
  checksum.

Each mark is recorded in the history (`markedApplied` or `markedUnapplied`). See
[Troubleshooting](../../guides/Troubleshooting.md).
