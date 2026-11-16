Removes the migration lock.

```
migrate-mongo unlock [--force]
```

```bash
migrate-mongo unlock            # remove an expired lock
migrate-mongo unlock --force    # remove a lock even if it hasn't expired; only if you're sure its run is dead
```

## Options

| Option | Meaning |
|---|---|
| `--force` | Remove the lock even if it hasn't expired. |

You rarely need this. A run renews its lock while it's working, and the lock expires `lockTtl` seconds (5 minutes by
default) after a run dies, so the next run takes it then. Use `--force` only when you know the run holding it has
stopped and you can't wait. Removing a live run's lock lets a second run start alongside it. See
[Safety](../../concepts/Safety.md#locking).
