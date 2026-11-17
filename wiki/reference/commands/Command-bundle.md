Publishes the migrations project as a self-contained, single-file executable that runs without .NET. Tool only.

```
migrate-mongo bundle [--runtime <rid>] [--output <dir>]
```

```
$ migrate-mongo bundle -r linux-x64 -o out
Created out/MyApp.Migrations (linux-x64, 78.3 MB) and out/migrate-mongo.yaml
```

## Options

| Option | Default | Meaning |
|---|---|---|
| `-r`, `--runtime` | This machine's | Runtime identifier, e.g. `linux-x64`, `linux-musl-x64`, `win-x64`, `osx-arm64`. Any platform can be built from any OS. |
| `-o`, `--output` | `bundle` | Output directory. |

The bundle is named after the project, and `migrate-mongo.yaml` is copied next to it. When the bundle is for the
current platform, `bundle` checks it starts.

See [Bundles](../../guides/Bundles.md) for running them and using them in pipelines.

A config with only `scripts` (no `project`) has nothing to bundle: the tool runs
[JavaScript migrations](../../guides/JavaScript-Migrations.md) itself, including in the `-bundle` Docker image. A
mixed project's bundle runs only its C# migrations.
