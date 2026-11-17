The tool, the migrations project and [bundles](../guides/Bundles.md) all use the same exit codes, so a pipeline can
act on them. With `--json`, the last line's `result` carries the same code.

| Code | Meaning | Typical cause |
|---|---|---|
| `0` | Success. | |
| `1` | A migration failed, or another error. | A migration threw; bad config; can't connect. |
| `2` | Pending migrations or collection changes exist. | [`plan`](./commands/Command-plan.md), or [`status --check`](./commands/Command-status.md). |
| `3` | Timed out waiting for the lock. | Another run is in progress. See [Troubleshooting](../guides/Troubleshooting.md#another-run-holds-the-lock). |
| `4` | Validation failed. | An edited, missing or out-of-order migration; an invalid collection definition; a collection change that isn't allowed. |
| `5` | The tool and runner have no protocol version in common. | The tool is older than the `Kevsoft.MigrateMongo.Runner` in your project. Update the tool. |
| `6` | Building the migrations project failed. | A compile error, including [build errors](./Build-Errors.md) from the source generator. |

## In scripts

```bash
status=0
migrate-mongo plan --env prod || status=$?
case $status in
  0) echo "Nothing to deploy" ;;
  2) migrate-mongo up --env prod ;;
  *) exit 1 ;;
esac
```

The `||` keeps `set -e` (the default in GitHub Actions) from stopping the script when `plan` exits with `2`.

When you [run from your application](../guides/Running-from-Your-Application.md), `IMongoMigrationRunner` results have
the same `ExitCode`.
