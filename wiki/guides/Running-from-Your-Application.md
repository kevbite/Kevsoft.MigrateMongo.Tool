Instead of (or as well as) running `migrate-mongo`, your app can apply migrations itself when it starts, with
`Kevsoft.MigrateMongo.Hosting`.

## Setting it up

Reference your migrations from your app (the migrations project, or a
[class library](../getting-started/Project-Setup.md#keeping-migrations-in-a-class-library) with them), and add the
hosting package:

```bash
dotnet add package Kevsoft.MigrateMongo.Hosting
```

```csharp
using Kevsoft.MigrateMongo.Hosting;

builder.Services
    .AddMongoMigrations(o =>
    {
        o.ConnectionString = builder.Configuration.GetConnectionString("Mongo"); // omit to use your registered IMongoClient
        o.DatabaseName = "myapp";
    })
    .FromAssemblyContaining<AddEmailIndexToUsers>()
    .RunOnStartup();
```

- **`AddMongoMigrations`** sets the [options](#options). There's also an overload that takes `(IServiceProvider, options)`,
  to read them from your own services.
- **`FromAssemblyContaining<T>()`** (or `FromAssembly(assembly)`) says where your migrations and collection definitions
  are. Call it once per assembly.
- **`RunOnStartup()`** applies them when the app starts. Leave it out to [run them yourself](#running-them-yourself).

Without a `ConnectionString`, the `IMongoClient` registered in your app is used.

Options use the standard `IOptionsFactory<MongoMigrationOptions>` pipeline, so `services.Configure<MongoMigrationOptions>(...)`
and configuration binding can be used alongside `AddMongoMigrations`. Options are validated when the host starts and whenever
the runner is invoked.

To migrate more than one database from the same service provider, give each registration a name and resolve its keyed runner:

```csharp
builder.Services
    .AddMongoMigrations("catalog", o => o.DatabaseName = "catalog")
    .FromAssemblyContaining<CatalogMigrations>();
builder.Services
    .AddMongoMigrations("identity", o => o.DatabaseName = "identity")
    .FromAssemblyContaining<IdentityMigrations>();

var catalog = app.Services.GetRequiredKeyedService<IMongoMigrationRunner>("catalog");
await catalog.UpAsync();
```

Named registrations can also call `RunOnStartup()` to apply each database during host startup.

## What happens at startup

`RunOnStartup()` applies pending migrations before any hosted service starts, so ASP.NET Core doesn't serve requests
against an out-of-date database.

- If a migration fails, or the lock isn't acquired within `LockWaitTimeout`, the host fails to start with a
  `MongoMigrationException`.
- Progress, warnings (such as checksum warnings with `OnChecksumMismatch = Warn`) and the outcome go to your app's
  `ILogger`.
- Migrations can take your app's services, including scoped ones, as constructor dependencies. A new scope is created
  for each run. See [Dependency Injection](./Dependency-Injection.md).

App instances and CLI runs share the same state and lock, so it's safe when many instances start at once: one applies
the migrations and the others wait, then find nothing to do.

## Options

Keep the options consistent with `migrate-mongo.yaml`, if you also use the tool. The defaults match the config file's.

| Option | Config equivalent |
|---|---|
| `ConnectionString`, `DatabaseName` | `mongodb.url`, `mongodb.databaseName` |
| `StateCollection` | `stateCollection` |
| `LockTtl`, `LockWaitTimeout` | `lockTtl`, `lockWaitTimeout` (as `TimeSpan`s) |
| `OnChecksumMismatch` | `onChecksumMismatch` |
| `Transaction`, `TransactionRetryTimeout` | `transaction`, `transactionRetryTimeout` |
| `AllowOutOfOrder` | `allowOutOfOrder` |
| `CaptureStatements`, `ResultsMaxBytes` | `results.captureStatements`, `results.maxBytes` |
| `IndexPrune`, `IndexRebuild`, `DropHiddenIndexesAfter`, `SupersededDefinitionWindow` | `indexes.prune`, `indexes.onRebuild`, `indexes.dropHiddenAfter`, `indexes.supersededWindow` |
| `SearchIndexPrune`, `WaitForSearchIndexes`, `SearchIndexWaitTimeout` | `searchIndexes.prune`, `searchIndexes.waitForReady`, `searchIndexes.waitTimeout` |
| `ConvertToCapped`, `OnUnchangeable` | `collections.onConvertToCapped`, `collections.onUnchangeable` |
| `CollectionPrune`, `CollectionIgnore` | `collections.prune`, `collections.ignore` |
| `ValidatorPrune`, `OnInvalidDocuments`, `CountInvalidDocuments` | `validators.prune`, `validators.onInvalidDocuments`, `validators.countInvalidDocuments` |
| `Environment` | Recorded with each run, like `--env`. |
| `Tunnel`, `TunnelProvider` | `tunnel` (needs `Kevsoft.MigrateMongo.Tunnel`). See [below](#through-an-ssh-bastion). |

See [Configuration](../reference/Configuration.md) for what each does.

### Startup options

`RunOnStartup(o => ...)` takes options for the startup run only:

| Option | Default | Meaning |
|---|---|---|
| `Collections` | `true` | Also bring collections in line with their definitions. `false` applies migrations only, like `up --only migrations`. |
| `AllowRebuild` | | Like `--allow-rebuild`, e.g. `["orders.reference_1"]`. |
| `AllowUnmatched` | | Like `--allow-unmatched`. |
| `AllowSuperseded` | `false` | Like `--allow-superseded`. |
| `AllowConvertToCapped` | | Like `--allow-convert-to-capped`. |
| `SkipUnchangeable` | | Like `--skip-unchangeable`. |
| `AllowInvalidDocuments` | | Like `--allow-invalid-documents`. |

## Collection definitions at startup

`RunOnStartup()` also brings collections in line with their [definitions](./Managing-Collections.md), and the app
doesn't start until that's finished too. Watch out for:

- **Index builds delay startup.** On a large collection a build can take longer than your platform's health-check grace
  period, and the instance is restarted part-way through.
- **A change that isn't allowed stops startup.** For example, `IndexRebuild.Fail` with a definition that needs a
  rebuild: `RunOnStartup()` throws a `MongoMigrationException`, and every instance fails to start until the definition
  or the option changes. To get one deploy past it, set the same allowances as the CLI's `--allow-*` options, then
  remove them once it has run:

  ```csharp
  .RunOnStartup(o =>
  {
      o.AllowRebuild = ["orders.reference_1"];   // --allow-rebuild
      o.AllowUnmatched = ["customers"];          // --allow-unmatched
      o.AllowSuperseded = true;                  // --allow-superseded
      o.AllowConvertToCapped = ["events"];       // --allow-convert-to-capped
      o.SkipUnchangeable = ["metrics"];          // --skip-unchangeable
      o.AllowInvalidDocuments = ["customers"];   // --allow-invalid-documents
  });
  ```

- **Older builds don't undo newer definitions.** During a rolling deploy, an instance still on the previous build skips
  any definition a newer build replaced within `SupersededDefinitionWindow`, and logs a warning. See
  [rolling deploys](./Managing-Collections.md#rolling-deploys).

To avoid the first two, apply migrations at startup and collection definitions from your deploy step:

```csharp
.RunOnStartup(o => o.Collections = false);
```

```bash
migrate-mongo up --only collections --env prod
```

## Running them yourself

To run migrations on demand instead (e.g. from an admin endpoint or a `--migrate` switch), leave out `RunOnStartup()`
and use `IMongoMigrationRunner`:

```csharp
var runner = app.Services.GetRequiredService<IMongoMigrationRunner>();
var result = await runner.UpAsync();
if (result.ExitCode != 0) { /* result.Message says why */ }
```

| Method | Like |
|---|---|
| `UpAsync(to?)` | `up [--to]` |
| `DryRunUpAsync(to?)` | `up --dry-run` |
| `StatusAsync()` | `status` |
| `PlanAsync()` | `plan` |
| `ValidateAsync()` | `validate` |
| `ShowAsync(id)` | `show <id>` |
| `ExecuteAsync(command, arguments)` | Any other command, e.g. `"down"`. |

Results carry the same [exit codes](../reference/Exit-Codes.md) as the CLI.

## Through an SSH bastion

Reference `Kevsoft.MigrateMongo.Tunnel` and set both tunnel options:

```csharp
using Kevsoft.MigrateMongo.Config;
using Kevsoft.MigrateMongo.Tunnel;

o.Tunnel = new TunnelConfig { Host = "bastion.example.com", User = "deploy", PrivateKey = key, HostKeyFingerprint = "SHA256:..." };
o.TunnelProvider = new SshTunnelProvider();
```

See [SSH Tunnels](./SSH-Tunnels.md) for the settings.

## See also

- [Packages](../reference/Packages.md)
- [Safety](../concepts/Safety.md)
