A bundle is your migrations project published as a self-contained, single-file executable, so it runs where there's no
.NET installed: deploy agents, minimal containers, jump boxes.

## Creating one

```
$ migrate-mongo bundle -r linux-x64 -o out
Created out/MyApp.Migrations (linux-x64, 78.3 MB) and out/migrate-mongo.yaml
```

| Option | Meaning |
|---|---|
| `-r`, `--runtime` | Runtime identifier, e.g. `linux-x64`, `linux-musl-x64` (Alpine), `win-x64`, `osx-arm64`. Defaults to the current machine's. Any platform can be built from any OS. |
| `-o`, `--output` | Output directory (default `bundle`). |

The bundle is named after the project, and `migrate-mongo.yaml` is copied next to it. When the bundle is for the current
platform, `bundle` checks it starts.

## Running one

```
$ ./out/MyApp.Migrations up --env staging
```

- It takes the same commands, options and [exit codes](../reference/Exit-Codes.md) as the tool, apart from the
  tool-only ones (`init`, `create`, `bundle`, `version`, `--no-build` and `--runner`).
- It looks for its config in the current directory and then next to itself, or use `-f <path>`.
- Environment variables in the config are read from the environment it runs in.

## Tunnels

A bundle can't open an [SSH tunnel](./SSH-Tunnels.md) on its own. Either:

- add `Kevsoft.MigrateMongo.Tunnel` to the project so the bundle opens them itself; or
- drive the bundle with the standalone tool, which opens the tunnel and also gives you the tool's output:

  ```bash
  migrate-mongo up --env prod --runner ./out/MyApp.Migrations
  ```

The `-bundle` [Docker image](./Docker.md) is the tool without .NET, for exactly this.

## In pipelines

See [Running in CI: build once, deploy many](./Running-in-CI.md#build-once-deploy-many).

## See also

- [`bundle`](../reference/commands/Command-bundle.md)
- [How It Works](../concepts/How-It-Works.md)
