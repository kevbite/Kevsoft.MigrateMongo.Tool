Every command, with a page each for the details.

| Command | What it does | Writes to the database? |
|---|---|---|
| [`init`](./commands/Command-init.md) | Create `migrate-mongo.yaml` and a migrations project. | No |
| [`create <name>`](./commands/Command-create.md) | Create a new migration file. | No |
| [`status`](./commands/Command-status.md) | Show the state of every migration and collection definition. | No |
| [`plan`](./commands/Command-plan.md) | Show what `up` would do; exit `2` if there's pending work. | No |
| [`up`](./commands/Command-up.md) | Apply pending migrations, then bring collections in line with their definitions. | Yes |
| [`down`](./commands/Command-down.md) | Roll back applied migrations. | Yes |
| [`validate`](./commands/Command-validate.md) | Check applied migrations against the code. | No |
| [`baseline --to <id>`](./commands/Command-baseline.md) | Record migrations as applied without running them. | Yes |
| [`mark <id>`](./commands/Command-mark.md) | Set a migration's state by hand. | Yes |
| [`unlock`](./commands/Command-unlock.md) | Remove an expired (or, with `--force`, any) lock. | Yes |
| [`history [id]`](./commands/Command-history.md) | Show the audit log. | No |
| [`show <id>`](./commands/Command-show.md) | Show one migration or collection in detail. | No |
| [`bundle`](./commands/Command-bundle.md) | Publish the migrations project as a self-contained executable. | No |
| [`version`](./commands/Command-version.md) | Show the tool's version. | No |

The commands that only read work with a read-only database user. The commands that write (`up`, `down`, `mark` and
`baseline`) also create the state collections' indexes the first time.

## Global options

These work with every command:

| Option | Meaning |
|---|---|
| `-f`, `--file <path>` | Config file (default: `migrate-mongo.yaml` in the current directory). |
| `-e`, `--env <name>` | Use an [environment](./Configuration.md#environments) from the config file. |
| `--json` | Write JSON lines instead of text. The last line is `{"type":"result","result":{...}}`. |
| `--no-build` | Don't build the migrations project first; reuse the last build. Tool only. |
| `--runner <path>` | Run a pre-built runner (a `.dll` or a [bundle](../guides/Bundles.md)) instead of building. Tool only. |
| `--insecure-skip-host-key-check` | Don't verify the [SSH tunnel](../guides/SSH-Tunnels.md)'s host key. Avoid outside throwaway environments. |

`migrate-mongo --help` and `migrate-mongo <command> --help` list them too.

## The tool, the project and bundles

The tool builds the migrations project, then prints the runner's versions before the command's output:

```
Built src/MyApp.Migrations/MyApp.Migrations.csproj (3.6s)
Runner: Kevsoft.MigrateMongo 0.2.0 · MongoDB.Driver 3.12.0 · .NET 10.0.12 · protocol 1
```

The build output is only shown when the build fails (exit code `6`).

Running the project directly (`dotnet run --project src/MyApp.Migrations -- up`) or a [bundle](../guides/Bundles.md)
(`./MyApp.Migrations up`) accepts the same commands and options, except the tool-only ones: `init`, `create`,
`bundle`, `version`, `--no-build` and `--runner`.

## JSON output

With `--json`, each line is a JSON object with a `type`, e.g. `migrationStarted`, `migrationCompleted`,
`migrationFailed` (with the exception in `details`) and `lockWaiting`. The last line is always the `result`. Use it to
drive dashboards or deployment tooling; the [exit code](./Exit-Codes.md) is the same either way.

## See also

- [Exit Codes](./Exit-Codes.md)
- [Configuration](./Configuration.md)
