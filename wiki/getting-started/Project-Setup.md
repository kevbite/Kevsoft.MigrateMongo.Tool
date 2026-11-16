Migrations live in a small console project that you own. The tool builds it with your SDK and runs it on your
runtime, with the `Kevsoft.MigrateMongo` and `MongoDB.Driver` (3.9 or later) versions you choose. The tool itself never
loads the driver, so upgrading either side doesn't break the other (see [How It Works](../concepts/How-It-Works.md)).
The database must be MongoDB 5.0 or later.

There are three ways to set one up.

## With `init`

From the root of your repository:

```
$ migrate-mongo init --project src/MyApp.Migrations
Created migrate-mongo.yaml
Created src/MyApp.Migrations/MyApp.Migrations.csproj
Created src/MyApp.Migrations/Program.cs
Created src/MyApp.Migrations/.editorconfig
Next: set MONGODB_URL, then run `migrate-mongo create <Name>` and `migrate-mongo status`.
```

The project targets your SDK's .NET version and references `Kevsoft.MigrateMongo.Runner` and `MongoDB.Driver`.
`Program.cs` is one line:

```csharp
using Kevsoft.MigrateMongo;

return await MigrateMongoRunner.RunAsync(args);
```

`.editorconfig` makes [CA2016](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2016) a
warning, so your editor and `dotnet build` point out calls that take a `CancellationToken` but aren't given the
migration's. Passing it on matters: it's how Ctrl+C and `SIGTERM` stop a running migration cleanly.

`migrate-mongo.yaml` is written next to where you ran it. The database name is the first part of the project name,
lower-cased:

```yaml
# Kevsoft.MigrateMongo configuration. See https://github.com/kevbite/Kevsoft.MigrateMongo.Tool
project: src/MyApp.Migrations
mongodb:
  url: ${MONGODB_URL}
  databaseName: myapp
stateCollection: _migrations
lockTtl: 300
lockWaitTimeout: 60
onChecksumMismatch: fail   # fail | warn | ignore
transaction: auto          # auto | always | never
# environments:
#   prod:
#     mongodb:
#       url: ${PROD_MONGODB_URL}
```

See [`init`](../reference/commands/Command-init.md) for its options, including `--auth aws`, and
[Configuration](../reference/Configuration.md) for everything the file can hold.

## With `dotnet new`

The same project and migration files are available as `dotnet new` templates:

```bash
dotnet new install Kevsoft.MigrateMongo.Templates
dotnet new migratemongo -n MyApp.Migrations -o src/MyApp.Migrations
dotnet new migratemongo-migration -n AddEmailIndexToUsers -o src/MyApp.Migrations/Migrations
```

| Template | Short name | Creates |
|---|---|---|
| MongoDB Migrations Project | `migratemongo` | The same project as `init`, with `migrate-mongo.yaml` next to it (`project: .`). |
| MongoDB Migration | `migratemongo-migration` | The same `M<timestamp>_<Name>.cs` file as `create`. |
| MongoDB Migration, minimal | `migratemongo-migration-minimal` | The same file as `create --minimal`. |

`migratemongo` options:

| Option | Default | Meaning |
|---|---|---|
| `--framework` | `net10.0` | The target framework (`net10.0` or `net11.0`). |
| `--database-name` | First part of the project name, lower-cased | The database written to `migrate-mongo.yaml`. |
| `--skip-config` | `false` | Don't create `migrate-mongo.yaml`, e.g. to keep it at the repository root with `migrate-mongo init --project src/MyApp.Migrations`. |
| `--auth` | `none` | `aws` references `Kevsoft.MigrateMongo.Aws`, like `init --auth aws`. |
| `--no-restore` | `false` | Don't restore the project after creating it. |

`migratemongo-migration` and `migratemongo-migration-minimal` use `<project root namespace>.Migrations` as the
namespace (read from the restored project, falling back to `Migrations`). Pass `--namespace` to choose your own. They
always use the built-in templates, not a [custom template](../guides/Custom-Templates.md).

## With an existing project

Any console project works:

1. Add the packages:

   ```bash
   dotnet add package Kevsoft.MigrateMongo.Runner
   dotnet add package MongoDB.Driver
   ```

2. Make `Program.cs` call the runner:

   ```csharp
   return await Kevsoft.MigrateMongo.MigrateMongoRunner.RunAsync(args);
   ```

3. Point `project:` in `migrate-mongo.yaml` at it. `migrate-mongo init --project <path>` does this for you: if the
   folder already has a `.csproj`, it's used as it is and only the config is written.

## Keeping migrations in a class library

To share migrations between the migrations project and your app (for
[running them at startup](../guides/Running-from-Your-Application.md)), put them in a class library that references
`Kevsoft.MigrateMongo.Abstractions`, and tell the runner to scan it:

```csharp
return await MigrateMongoRunner.RunAsync(args, new MigrateMongoRunnerOptions
{
    Assemblies = { typeof(AddEmailIndexToUsers).Assembly },
});
```

See [Packages](../reference/Packages.md) for which package to reference where.

## Layout

A typical repository:

```
.config/dotnet-tools.json        # pins the tool version
migrate-mongo.yaml               # project, connection, environments
src/
  MyApp/                         # your app (optionally runs migrations at startup)
  MyApp.Migrations/
    MyApp.Migrations.csproj
    Program.cs
    Migrations/
      M20261014164028_AddEmailIndexToUsers.cs
    Collections/
      Orders.cs                  # [ManagedCollection] definitions
```

Folders inside the project are up to you: migrations and definitions are found by their attributes, at compile time.
