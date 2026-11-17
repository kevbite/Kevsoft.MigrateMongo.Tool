Creates `migrate-mongo.yaml` and a migrations project, or with `--js` a folder for
[JavaScript migrations](../../guides/JavaScript-Migrations.md). Tool only.

```
migrate-mongo init [--project <path>] [--js] [--auth aws] [--file <path>]
```

```
$ migrate-mongo init --project src/MyApp.Migrations
Created migrate-mongo.yaml
Created src/MyApp.Migrations/MyApp.Migrations.csproj
Created src/MyApp.Migrations/Program.cs
Created src/MyApp.Migrations/.editorconfig
Next: set MONGODB_URL, then run `migrate-mongo create <Name>` and `migrate-mongo status`.
```

## Options

| Option | Default | Meaning |
|---|---|---|
| `-p`, `--project` | `Migrations` | Where to create the project. If it already has a `.csproj`, that project is used instead and only the config is written. With `--js`, the scripts folder (default: the config file's directory). |
| `--js` | | Set up JavaScript migrations instead of a C# project. No .NET SDK needed. |
| `--auth aws` | | Also reference `Kevsoft.MigrateMongo.Aws` for [AWS IAM authentication](../../guides/AWS-IAM-Authentication.md). Not with `--js`. |
| `-f`, `--file` | `migrate-mongo.yaml` | Where to write the config file. |

## What it creates

- **`<project>.csproj`**: targets your SDK's .NET version and references `Kevsoft.MigrateMongo.Runner` and
  `MongoDB.Driver` (and, with `--auth aws`, `Kevsoft.MigrateMongo.Aws` and `MongoDB.Driver.Authentication.AWS` at the
  same version as the driver).
- **`Program.cs`**: `return await MigrateMongoRunner.RunAsync(args);`
- **`.editorconfig`**: makes CA2016 a warning, so calls that don't pass the migration's `CancellationToken` are flagged.
- **`migrate-mongo.yaml`**: `project:` pointing at the project, `mongodb.url: ${MONGODB_URL}`, `databaseName` (the
  first part of the project name, lower-cased) and the main settings with their defaults. With `--auth aws`, a comment
  shows an AWS IAM connection string.

With `--js`, it creates `migrations/`, `collections/`, `types/migrate-mongo.d.ts`, `jsconfig.json` and
`package.json` (the last two only if they don't exist), and a `migrate-mongo.yaml` with `scripts:` instead of
`project:`. See [JavaScript Migrations](../../guides/JavaScript-Migrations.md#getting-started).

## See also

- [Project Setup](../../getting-started/Project-Setup.md), including the `dotnet new` templates
- [Configuration](../Configuration.md)
