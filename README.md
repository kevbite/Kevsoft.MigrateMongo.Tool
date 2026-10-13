# Kevsoft.MigrateMongo [![Continuous Integration Workflow](https://github.com/kevbite/Kevsoft.MigrateMongo/actions/workflows/ci.yml/badge.svg)](https://github.com/kevbite/Kevsoft.MigrateMongo/actions/workflows/ci.yml) [![install from nuget](http://img.shields.io/nuget/v/Kevsoft.MigrateMongo.Tool.svg?style=flat-square)](https://www.nuget.org/packages/Kevsoft.MigrateMongo.Tool) [![downloads](http://img.shields.io/nuget/dt/Kevsoft.MigrateMongo.Tool.svg?style=flat-square)](https://www.nuget.org/packages/Kevsoft.MigrateMongo.Tool)

.NET database migration tool for [MongoDB](https://www.mongodb.com/). You write migrations in C# against the
official [MongoDB C# driver](https://github.com/mongodb/mongo-csharp-driver), then apply and roll them back from
the command line, your build server or your application.

Inspired by [migrate-mongo](https://www.npmjs.com/package/migrate-mongo) from [seppevs](https://github.com/seppevs),
with ideas borrowed from [Mongock](https://mongock.io/), [Flyway](https://flywaydb.org/) and
[EF Core migrations](https://learn.microsoft.com/ef/core/managing-schemas/migrations/).

> **Status:** under active development. Features marked 🚧 are planned but not available yet.
> [PLAN.md](PLAN.md) describes the full design and [USAGE.md](USAGE.md) walks through it end to end.

## Why another migration tool?

- **It doesn't break when the .NET runtime or SDK changes.** The `migrate-mongo` CLI is a Native AOT binary,
  so it needs no .NET runtime to start. Your migrations live in a small console project that **you** own. The
  CLI builds that project with your SDK and runs it on your runtime, so you never need a particular SDK to
  match the tool.
- **It doesn't break when the MongoDB driver changes.** You choose the `MongoDB.Driver` version (3.5 or later).
  Your migrations compile against that version and the library never calls driver APIs on your behalf.
  Driver upgrades therefore can't cause `MissingMethodException`s in code you didn't write.
- **It's safe to run anywhere.** A distributed lock stops concurrent runs, `applying`/`rollingBack` states are
  written before each migration runs, migrations can run in transactions, and checksums detect migrations
  edited after they were applied.
- **It's built for build servers.** Exit codes are meaningful, `--json` gives machine-readable output, there
  are per-environment configs and `${ENV_VAR}` secrets, and built-in SSH tunnelling reaches databases behind a bastion.
- **Mistakes are caught at compile time.** A source generator discovers migrations without reflection and
  reports duplicate or malformed ids as build errors.

## Getting Started

### Prerequisites

- A MongoDB deployment to migrate, version 4.2 or later. Transactions require a replica set or sharded
  cluster. Locally, [MongoDB Atlas Local](https://hub.docker.com/r/mongodb/mongodb-atlas-local) works well:

  ```bash
  docker run -d -p 27017:27017 mongodb/mongodb-atlas-local:8.0
  ```

- A [.NET SDK](https://dotnet.microsoft.com/download) to build your migrations project (.NET 8 or later). The
  `migrate-mongo` CLI itself needs no runtime.

### Installing the Tool

**Kevsoft.MigrateMongo.Tool** can be installed as a [.NET tool](https://learn.microsoft.com/dotnet/core/tools/global-tools).
Pinning it in a local tool manifest is recommended, so CI uses the same version as you:

```bash
dotnet new tool-manifest
dotnet tool install Kevsoft.MigrateMongo.Tool
```

alternatively you can install it globally.

```bash
dotnet tool install -g Kevsoft.MigrateMongo.Tool
```

Check it's working:

```bash
migrate-mongo version
```

### Installing the Packages

The migrations project references the runner, which brings in everything else it needs:

```bash
dotnet add package Kevsoft.MigrateMongo.Runner
dotnet add package MongoDB.Driver   # any version >= 3.5, your choice
```

| Package | Purpose |
|---|---|
| `Kevsoft.MigrateMongo.Tool` | The `migrate-mongo` CLI (Native AOT). |
| `Kevsoft.MigrateMongo.Runner` | Entry point for your migrations console project (`MigrateMongoRunner.RunAsync`). |
| `Kevsoft.MigrateMongo.Abstractions` | `IMigration`, `[Migration]`, `MigrationContext` and the source generator. Reference it directly to keep migrations in a class library. |
| `Kevsoft.MigrateMongo` | The migration engine: planning, locking, state and checksums. |
| `Kevsoft.MigrateMongo.MongoDriver` | Engine adapter for the MongoDB C# driver. |
| `Kevsoft.MigrateMongo.Hosting` | Run migrations from your own app on startup, or on demand with `IMongoMigrationRunner`. |

### Creating Your First Project

From the root of your repository:

```bash
migrate-mongo init --project src/MyApp.Migrations
```

This creates `migrate-mongo.yaml` and a console project with a `Program.cs` that's a single line:

```csharp
return await MigrateMongoRunner.RunAsync(args);
```

Set your connection string and check the status:

```bash
export MONGODB_URL="mongodb://localhost:27017/?directConnection=true"
migrate-mongo status
```

To use an existing project instead, add the `Kevsoft.MigrateMongo.Runner` package to it and point `project:` in
`migrate-mongo.yaml` at it.

## Usage

### Configuration

`migrate-mongo.yaml` lives at the root of your repository. Values can reference environment variables with
`${NAME}`. Each entry under `environments:` is merged over the top-level settings when you pass `--env <name>`.

```yaml
project: src/MyApp.Migrations        # relative to this file
mongodb:
  url: ${MONGODB_URL}
  databaseName: myapp
stateCollection: _migrations         # also uses _migrations.history and _migrations.lock
lockTtl: 300                         # seconds; the lock is renewed while running
lockWaitTimeout: 60                  # seconds to wait for another run to finish
onChecksumMismatch: fail             # fail | warn | ignore
transaction: auto                    # auto | always | never
transactionRetryTimeout: 120         # seconds to keep retrying transient transaction errors (0 = off)
environments:
  prod:
    mongodb:
      url: ${PROD_MONGODB_URL}
```

Use `-f <path>` to point at a different config file.

### Creating a Migration

```bash
migrate-mongo create AddEmailIndexToUsers
```

This creates `Migrations/M<timestamp>_AddEmailIndexToUsers.cs`. Fill in `UpAsync` and `DownAsync` using the
normal driver API:

```csharp
using Kevsoft.MigrateMongo;
using MongoDB.Bson;
using MongoDB.Driver;

[Migration("20261009145210", "add-email-index-to-users", Transaction = TransactionMode.Never)]
public sealed class AddEmailIndexToUsers : IMigration
{
    public Task UpAsync(MigrationContext ctx, CancellationToken ct) =>
        ctx.Database.GetCollection<BsonDocument>("users").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("email"),
                new CreateIndexOptions { Unique = true, Name = "email_1" }),
            cancellationToken: ct);

    public Task DownAsync(MigrationContext ctx, CancellationToken ct) =>
        ctx.Database.GetCollection<BsonDocument>("users").Indexes.DropOneAsync("email_1", ct);
}
```

`MigrationContext` gives you:

```csharp
public sealed class MigrationContext
{
    IMongoDatabase Database { get; }        // the configured database
    IClientSessionHandle? Session { get; }  // set when running in a transaction
    ILogger Logger { get; }                 // output is streamed to the CLI
    IServiceProvider Services { get; }      // for constructor-injected dependencies
    MigrationRunInfo Run { get; }           // run id, batch, direction, environment
}
```

The source generator reports these build errors:

| Code | Meaning |
|---|---|
| `MM0001` | Two migrations share the same id. |
| `MM0002` | The id isn't a 14-digit `yyyyMMddHHmmss` timestamp. |
| `MM0003` | A `[Migration]` class doesn't implement `IMigration`. |
| `MM0004` | The migration class can't be constructed (abstract, private, or no accessible constructor). |
| `MM0005` | The migration name is empty. |

### Transactions

By default (`transaction: auto`) each migration runs in a transaction when the deployment supports one. The
migration's state document is written in the same transaction, so a failure leaves nothing half-applied.
Pass `ctx.Session` to every operation:

```csharp
[Migration("20261009150030", "split-full-name")]
public sealed class SplitFullName : IMigration
{
    public Task UpAsync(MigrationContext ctx, CancellationToken ct) =>
        ctx.Database.GetCollection<BsonDocument>("users").UpdateManyAsync(
            ctx.Session,
            Builders<BsonDocument>.Filter.Exists("fullName"),
            new EmptyPipelineDefinition<BsonDocument>()
                .AppendStage<BsonDocument, BsonDocument, BsonDocument>(BsonDocument.Parse("""
                    { $set: {
                        firstName: { $arrayElemAt: [ { $split: ["$fullName", " "] }, 0 ] },
                        lastName:  { $arrayElemAt: [ { $split: ["$fullName", " "] }, 1 ] } } }
                    """)),
            cancellationToken: ct);

    public Task DownAsync(MigrationContext ctx, CancellationToken ct) =>
        ctx.Database.GetCollection<BsonDocument>("users").UpdateManyAsync(
            ctx.Session,
            Builders<BsonDocument>.Filter.Exists("firstName"),
            Builders<BsonDocument>.Update.Unset("firstName").Unset("lastName"),
            cancellationToken: ct);
}
```

Some operations, such as building indexes on existing collections, can't run in a transaction. Use
`TransactionMode.Never` for those. `TransactionMode.Always` fails the migration if transactions aren't available.

If the server reports a `TransientTransactionError` (e.g. a write conflict or a primary election), the whole
transaction is aborted and the migration runs again from the start. An `UnknownTransactionCommitResult` retries
just the commit. Retries back off from 100ms up to 5s and stop after `transactionRetryTimeout` seconds
(default `120`, `0` disables retries). This means **a transactional migration's `UpAsync`/`DownAsync` can run
more than once**, so keep any side effects outside the database (HTTP calls, files) idempotent.

### Dependency Injection

Migrations can take constructor dependencies. The generated factory resolves them from `ctx.Services`, without
reflection. Register services in `Program.cs`:

```csharp
return await MigrateMongoRunner.RunAsync(args, services =>
{
    services.AddSingleton<IClock, SystemClock>();
});
```

### Checking Status and Planning

```bash
migrate-mongo status     # every migration and its state (pending, applied, failed, applying, ...)
migrate-mongo plan       # what `up` would do, without changing anything
```

`plan` exits with code `2` when there is pending work, so you can use it as a CI gate.

### Dry Runs

```bash
migrate-mongo up --dry-run               # what `up` would apply, and in which batch
migrate-mongo up --to 20261009145210 --dry-run
migrate-mongo down --batch --dry-run     # what `down` would roll back
```

A dry run uses the same checks as a real run (validation, blocked migrations, unknown `--to`, transaction
support) and exits with the code a real run would. It never runs migration code, takes the lock or writes
anything, not even the `_migrations` collections, so it works with a read-only database user. If another run holds
the lock, it prints a warning instead of waiting. Unlike `plan`, it exits `0` when there is pending work.

`--dry-run` needs `Kevsoft.MigrateMongo.Runner` 0.2.0 or later in the migrations project. Older runners would
ignore the flag, so the tool refuses with exit code `5` and runs nothing.

### Applying Migrations

```bash
migrate-mongo up                       # apply everything pending, as one batch
migrate-mongo up --dry-run             # show what would be applied, change nothing
migrate-mongo up --to 20261009145210   # stop after this migration
migrate-mongo up --env prod            # use the `prod` environment
migrate-mongo up --no-build            # skip `dotnet build` and reuse the last build
migrate-mongo up --json                # machine-readable output
```

### Rolling Back

```bash
migrate-mongo down                     # revert the last applied migration
migrate-mongo down --count 3           # revert the last 3
migrate-mongo down --batch             # revert everything from the last `up`
migrate-mongo down --to 20261009144500 # revert everything after this migration
migrate-mongo down --batch --dry-run   # show what would be reverted, change nothing
```

### Validating

```bash
migrate-mongo validate --env staging
```

This fails with exit code `4` if an applied migration was edited (checksum mismatch), an applied migration is
missing from the code, or a pending migration is older than one already applied. Run it in pull-request builds.

### Fixing State

When a migration crashed half-way without a transaction, or you need to adopt an existing database:

```bash
migrate-mongo mark 20261009160000 --applied     # it actually finished, or accept an edited checksum
migrate-mongo mark 20261009160000 --unapplied   # it didn't; run it again next time
migrate-mongo baseline --to 20261009144500      # record everything up to here as applied without running it
migrate-mongo unlock                            # remove an expired lock
migrate-mongo unlock --force                    # remove a lock even if it hasn't expired
migrate-mongo history 20261009150030            # audit trail for a migration
```

### What Gets Stored

| Collection | Contents |
|---|---|
| `_migrations` | One document per migration, keyed by id: `state`, `batch`, `checksum`, `transactional`, `appliedAt`, `durationMs` and `appliedBy` (host, user, tool, library, driver and runtime versions). |
| `_migrations.history` | Append-only audit log of every apply, rollback, failure and `mark`. |
| `_migrations.lock` | The single lock document, with owner, expiry and heartbeat. |

### Exit Codes

| Code | Meaning |
|---|---|
| `0` | Success. |
| `1` | A migration failed, or a general error. |
| `2` | Pending migrations exist (`plan` / `status --check`). |
| `3` | Timed out waiting for the lock. |
| `4` | Validation failed (checksum mismatch, missing or out-of-order migrations). |
| `5` | The tool and runner have no protocol version in common (update the tool), or the runner is too old for a requested feature such as `--dry-run` (update `Kevsoft.MigrateMongo.Runner`). |
| `6` | Building the migrations project failed. |

### Running in CI

```yaml
jobs:
  migrate:
    runs-on: ubuntu-latest
    environment: production
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - run: dotnet tool restore
      - run: dotnet tool run migrate-mongo validate --env prod
      - run: dotnet tool run migrate-mongo up --env prod
        env:
          PROD_MONGODB_URL: ${{ secrets.PROD_MONGODB_URL }}
```

### Tunnelling Through a Bastion

If the database is only reachable through a jump host, add a `tunnel:` to the environment. The tool opens an SSH
connection (in-process, no `ssh` binary needed), verifies the host key, and gives the driver a local SOCKS5 proxy,
so every replica set member is reachable by the hostnames in your connection string:

```yaml
environments:
  prod:
    mongodb:
      url: ${PROD_MONGODB_URL}       # e.g. mongodb://db1.internal,db2.internal/?replicaSet=rs0
    tunnel:
      host: bastion.example.com
      user: deploy
      privateKey: ${BASTION_SSH_KEY}
      hostKeyFingerprint: SHA256:x9Yb...
```

| Setting | Meaning |
|---|---|
| `host` / `port` | The SSH server (`port` defaults to `22`). Required: `host`. |
| `user` | The SSH user. Required. |
| `password` | Password authentication (also answers keyboard-interactive prompts). |
| `privateKey` | The private key's contents, e.g. from a CI secret. Literal `\n` sequences are turned into new lines. |
| `privateKeyPath` | Path to a private key file. `~/` and paths relative to the config file are supported. |
| `passphrase` | Passphrase for an encrypted private key. |
| `hostKeyFingerprint` | The server's `SHA256:...` host key fingerprint, as printed by `ssh-keygen -lf`. |
| `knownHostsPath` | A `known_hosts` file to verify the host key against. Defaults to `~/.ssh/known_hosts`. |
| `mode` | `socks` (default) or `local`. |

You need `password`, `privateKey` or `privateKeyPath` (not both key settings). The host key is always verified,
using `hostKeyFingerprint` if set or the `known_hosts` file otherwise. If verification fails, the error shows the
server's fingerprint so you can pin it. `--insecure-skip-host-key-check` turns verification off; avoid it outside
throwaway environments.

`socks` mode needs `MongoDB.Driver` 3.12 or later in your migrations project, because that's the first version
with SOCKS5 proxy support. `local` mode works with any driver version: it forwards a local port to the first host
in the connection string and connects with `directConnection=true`. It can't use `mongodb+srv://` URLs, and TLS
host name validation will fail unless the certificate also covers `127.0.0.1`.

Tunnels are a feature of the `migrate-mongo` tool. The standalone runner (`dotnet run` on the migrations project)
rejects configs with a `tunnel:`.

### Bundles 🚧

Build once and deploy many times to agents with no .NET installed:

```bash
migrate-mongo bundle -r linux-x64 -o out/migrations
./out/migrations up --env staging
```

### Running on Application Startup

Reference your migrations project (or a class library with your migrations) from your app, add the hosting package,
and apply migrations when the app starts:

```bash
dotnet add package Kevsoft.MigrateMongo.Hosting
```

```csharp
builder.Services
    .AddMongoMigrations(o =>
    {
        o.ConnectionString = builder.Configuration.GetConnectionString("Mongo"); // or omit to use your registered IMongoClient
        o.DatabaseName = "myapp";
    })
    .FromAssemblyContaining<AddEmailIndexToUsers>()
    .RunOnStartup();
```

`RunOnStartup()` applies pending migrations before any hosted service starts, so ASP.NET Core doesn't serve requests
against an out-of-date database. If a migration fails, or the lock can't be acquired within `LockWaitTimeout`, the
host fails to start with a `MongoMigrationException`. Migration progress goes to your app's `ILogger`, and migrations
can take your app's services (including scoped ones) as constructor dependencies.

App instances and CLI runs share the same state collections and lock, so it's safe when many pods start together:
one applies the migrations, the others wait for the lock and then find nothing to do. Keep the options the same as
your `migrate-mongo.yaml` (`DatabaseName`, `StateCollection`, `Transaction`, `OnChecksumMismatch`, ...). The
defaults match the config file's defaults. The state records each app run with `appliedBy.tool` set to
`app/<application name>`.

To run migrations yourself instead (e.g. from an admin endpoint or a `--migrate` switch), skip `RunOnStartup()` and
use `IMongoMigrationRunner`:

```csharp
var runner = app.Services.GetRequiredService<IMongoMigrationRunner>();
var result = await runner.UpAsync();          // also StatusAsync, PlanAsync, DryRunUpAsync, ValidateAsync and ExecuteAsync("down", ...)
if (result.ExitCode != 0) { /* result.Message says why */ }
```

SSH tunnels aren't available in-app; use a direct connection or a SOCKS5 proxy in the connection string.

## Samples

The [tests/fixtures](tests/fixtures/) folder contains small migrations projects used by the integration tests. They
also show the setup working end to end:

- **BasicMigrations** (`net8.0`) creates a collection with a validator, adds a unique index and seeds data using a
  service injected from `Program.cs`.
- **FailingMigrations** (`net10.0`) shows that a failed transactional migration is rolled back and recorded.
- **HostingApp** (`net8.0`) references BasicMigrations and applies them on startup with `Kevsoft.MigrateMongo.Hosting`.

## Contributing

1. Issue
1. Fork
1. Hack!
1. Pull Request

You'll need the .NET 8 and 10 SDKs ([global.json](global.json) pins the one used to build) and
[Docker](https://www.docker.com/). The integration tests pack the repo into a local NuGet feed, install the tool
from it, and use [Testcontainers](https://dotnet.testcontainers.org/) to start MongoDB Atlas Local automatically:

```bash
dotnet build
dotnet test --project tests/Kevsoft.MigrateMongo.Tests               # unit tests
dotnet test --project tests/Kevsoft.MigrateMongo.IntegrationTests    # full tool tests against MongoDB in Docker
```

To use your own MongoDB instead of a container, set `MIGRATE_MONGO_TEST_MONGODB_URL` (it must be a replica set
so transactions work). To test packages you've already packed, set `MIGRATE_MONGO_PACKAGE_VERSION` and
`MIGRATE_MONGO_PACKAGE_FEED`.
