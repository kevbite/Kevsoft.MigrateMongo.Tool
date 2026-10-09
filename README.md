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
  are per-environment configs and `${ENV_VAR}` secrets, and SSH tunnelling 🚧 reaches databases behind a bastion.
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

### Applying Migrations

```bash
migrate-mongo up                       # apply everything pending, as one batch
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
| `5` | The tool and runner have no protocol version in common. Update the tool. |
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

### Tunnelling Through a Bastion 🚧

If the database is only reachable through a jump host, add a `tunnel:` to the environment. The tool opens an SSH
connection, verifies the host key, and gives the driver a local SOCKS5 proxy, so every replica set member is
reachable:

```yaml
environments:
  prod:
    mongodb:
      url: ${PROD_MONGODB_URL}
    tunnel:
      type: ssh
      host: bastion.example.com
      user: deploy
      privateKey: ${BASTION_SSH_KEY}
      hostKeyFingerprint: SHA256:x9Yb...
```

### Bundles 🚧

Build once and deploy many times to agents with no .NET installed:

```bash
migrate-mongo bundle -r linux-x64 -o out/migrations
./out/migrations up --env staging
```

### Running on Application Startup 🚧

Reference your migrations project from your app and run migrations as a hosted service. App instances and CLI
runs share the same state and lock, so it's safe when many pods start together:

```csharp
builder.Services
    .AddMongoMigrations(o =>
    {
        o.ConnectionString = builder.Configuration.GetConnectionString("Mongo")!;
        o.DatabaseName = "myapp";
    })
    .FromAssemblyContaining<AddEmailIndexToUsers>()
    .RunOnStartup();
```

## Samples

The [tests/fixtures](tests/fixtures/) folder contains small migrations projects used by the integration tests. They
also show the setup working end to end:

- **BasicMigrations** (`net8.0`) creates a collection with a validator, adds a unique index and seeds data using a
  service injected from `Program.cs`.
- **FailingMigrations** (`net10.0`) shows that a failed transactional migration is rolled back and recorded.

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
