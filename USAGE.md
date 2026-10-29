# Kevsoft.MigrateMongo usage

Everything you can do with Kevsoft.MigrateMongo. For a quick introduction, see the [README](README.md).

- [Installing](#installing)
- [Setting up a project](#setting-up-a-project)
- [Configuration](#configuration)
- [Writing migrations](#writing-migrations)
- [Commands](#commands)
- [Exit codes](#exit-codes)
- [What gets stored](#what-gets-stored)
- [When something goes wrong](#when-something-goes-wrong)
- [Running in CI](#running-in-ci)
- [Bundles](#bundles)
- [Docker](#docker)
- [Tunnelling through a bastion](#tunnelling-through-a-bastion)
- [AWS IAM authentication (Atlas)](#aws-iam-authentication-atlas)
- [Running from your application](#running-from-your-application)

## Installing

### As a .NET tool

Pin the tool in a local tool manifest so CI uses the same version as you:

```bash
dotnet new tool-manifest
dotnet tool install Kevsoft.MigrateMongo.Tool
dotnet tool run migrate-mongo version
```

Or install it globally with `dotnet tool install -g Kevsoft.MigrateMongo.Tool`.

With the .NET 10 SDK or later you get a Native AOT build for your platform (Windows, Linux glibc/musl and macOS on
x64 and arm64), which starts instantly and doesn't need a .NET runtime. Older SDKs, and other platforms, get a
framework-dependent build that runs on any .NET runtime from 8 upwards. `migrate-mongo version` shows which you have:

```
migrate-mongo/0.2.0 (linux-x64; aot)
migrate-mongo/0.2.0 (win-x64; .NET 8.0.31)
```

### Without .NET

The install scripts download the standalone Native AOT binary for your platform from GitHub Releases, check it
against `SHA256SUMS`, and install it to `~/.local/bin` (`/usr/local/bin` as root) or
`%LOCALAPPDATA%\Programs\migrate-mongo`:

```bash
curl -fsSL https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/releases/latest/download/install.sh | sh
curl -fsSL https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/releases/latest/download/install.sh | sh -s -- --version 0.2.0 --install-dir ./bin
```

```powershell
irm https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/releases/latest/download/install.ps1 | iex
```

Both scripts also read `MIGRATE_MONGO_VERSION` and `MIGRATE_MONGO_INSTALL_DIR`. Set `MIGRATE_MONGO_NO_PATH=1` to
stop `install.ps1` changing your user `PATH`.

There's also a [Docker image](#docker).

## Setting up a project

Migrations live in a small console project that you own. The tool builds it with your SDK and runs it on your runtime,
using the library and `MongoDB.Driver` versions you choose (3.5 or later). The tool itself never loads the driver, so
upgrading either side doesn't break the other.

### `init`

From the root of your repository:

```
$ migrate-mongo init --project src/MyApp.Migrations
Created migrate-mongo.yaml
Created src/MyApp.Migrations/MyApp.Migrations.csproj
Created src/MyApp.Migrations/Program.cs
Next: set MONGODB_URL, then run `migrate-mongo create <Name>` and `migrate-mongo status`.
```

| Option | Default | Meaning |
|---|---|---|
| `-p`, `--project` | `Migrations` | Where to create the project. If it already has a `.csproj`, that project is used instead and only the config is written. |
| `--auth aws` | | Also reference `Kevsoft.MigrateMongo.Aws` for [AWS IAM authentication](#aws-iam-authentication-atlas). |
| `-f`, `--file` | `migrate-mongo.yaml` | Where to write the config file. |

The project targets your SDK's .NET version and references `Kevsoft.MigrateMongo.Runner` and `MongoDB.Driver`.
`Program.cs` is one line:

```csharp
using Kevsoft.MigrateMongo;

return await MigrateMongoRunner.RunAsync(args);
```

To use an existing project, add `Kevsoft.MigrateMongo.Runner` and `MongoDB.Driver` to it, call
`MigrateMongoRunner.RunAsync(args)` from `Program.cs`, and point `project:` in `migrate-mongo.yaml` at it.

### `dotnet new` templates

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
| `--framework` | `net10.0` | `net10.0`, `net9.0` or `net8.0`. |
| `--database-name` | First part of the project name, lower-cased | The database written to `migrate-mongo.yaml`. |
| `--skip-config` | `false` | Don't create `migrate-mongo.yaml`, e.g. to keep it at the repository root with `migrate-mongo init --project src/MyApp.Migrations`. |
| `--auth` | `none` | `aws` references `Kevsoft.MigrateMongo.Aws`, like `init --auth aws`. |
| `--no-restore` | `false` | Don't restore the project after creating it. |

`migratemongo-migration` and `migratemongo-migration-minimal` use `<project root namespace>.Migrations` as the
namespace (read from the restored project, falling back to `Migrations`). Pass `--namespace` to choose your own. They
always use the built-in templates, not `migrationTemplate`.

### Packages

| Package | Purpose |
|---|---|
| `Kevsoft.MigrateMongo.Tool` | The `migrate-mongo` CLI. |
| `Kevsoft.MigrateMongo.Runner` | Entry point for your migrations project (`MigrateMongoRunner.RunAsync`). Brings in everything below except the opt-in packages. |
| `Kevsoft.MigrateMongo.Abstractions` | `IMigration`, `[Migration]`, `MigrationContext` and the source generator. Reference it directly to keep migrations in a class library. |
| `Kevsoft.MigrateMongo` | The engine: planning, locking, state and checksums. |
| `Kevsoft.MigrateMongo.MongoDriver` | The engine's MongoDB driver adapter. |
| `Kevsoft.MigrateMongo.Hosting` | [Run migrations from your application](#running-from-your-application). |
| `Kevsoft.MigrateMongo.Tunnel` | Opt-in: lets the runner, bundles and Hosting open [SSH tunnels](#tunnelling-through-a-bastion) themselves. |
| `Kevsoft.MigrateMongo.Aws` | Opt-in: [AWS IAM authentication](#aws-iam-authentication-atlas). |
| `Kevsoft.MigrateMongo.Templates` | The `dotnet new` templates. |

## Configuration

`migrate-mongo.yaml` (or `migrate-mongo.yml`) is found in the current directory, or passed with `-f <path>`. Any value
can reference environment variables with `${NAME}`. Unknown settings are errors, so typos don't go unnoticed.

```yaml
project: src/MyApp.Migrations        # the migrations project, relative to this file
mongodb:
  url: ${MONGODB_URL}
  databaseName: myapp
stateCollection: _migrations         # also uses _migrations.history and _migrations.lock
lockTtl: 300
lockWaitTimeout: 60
onChecksumMismatch: fail
transaction: auto
transactionRetryTimeout: 120
allowOutOfOrder: false
migrationTemplate: templates/migration.cs.template
results:
  captureStatements: true
  maxBytes: 65536
environments:
  staging:
    mongodb:
      url: ${STAGING_MONGODB_URL}
  prod:
    mongodb:
      url: ${PROD_MONGODB_URL}
    tunnel:
      host: bastion.example.com
      user: deploy
      privateKey: ${BASTION_SSH_KEY}
      hostKeyFingerprint: SHA256:x9Yb...
```

| Setting | Default | Meaning |
|---|---|---|
| `project` | | The migrations project directory or `.csproj`, relative to the config file. |
| `mongodb.url` | | Connection string. Required. Any driver option works here (`tls`, `authMechanism`, `proxyHost`, ...). |
| `mongodb.databaseName` | | Database to migrate. Required. |
| `stateCollection` | `_migrations` | State collection. History and lock use `<name>.history` and `<name>.lock`. |
| `lockTtl` | `300` | Seconds the lock lasts without renewal. It's renewed while a run is in progress. |
| `lockWaitTimeout` | `60` | Seconds to wait for another run's lock before failing with exit code `3`. |
| `onChecksumMismatch` | `fail` | `fail`, `warn` or `ignore` when an applied migration has been edited. |
| `transaction` | `auto` | `auto` (use transactions when the deployment supports them), `always` or `never`. A migration's own `Transaction` setting overrides this. |
| `transactionRetryTimeout` | `120` | Seconds to keep retrying transient transaction errors. `0` disables retries. |
| `allowOutOfOrder` | `false` | Allow pending migrations older than the newest applied one (e.g. from a merged branch). |
| `migrationTemplate` | | A file `create` uses instead of the built-in template. See [custom templates](#custom-templates). |
| `results.captureStatements` | `true` | Record each [tracked operation](#recording-results)'s filter, update or index keys. `false` keeps only the kind, counts and timings. |
| `results.maxBytes` | `65536` | The most a migration's named results can take up. A migration that records more fails. |
| `tunnel` | | See [Tunnelling through a bastion](#tunnelling-through-a-bastion). |
| `environments` | | Named overrides, selected with `--env <name>`. Each is merged over the top-level settings. |

## Writing migrations

### `create`

```
$ migrate-mongo create AddEmailIndexToUsers
Created src/MyApp.Migrations/Migrations/M20261014164028_AddEmailIndexToUsers.cs
```

The id is the current UTC time (`yyyyMMddHHmmss`), and migrations run in id order. The new file contains commented
examples of [tracking operations and recording results](#recording-results); pass `--minimal` for bare `UpAsync` and
`DownAsync` methods instead. Fill them in with the normal driver API:

```csharp
using Kevsoft.MigrateMongo;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyApp.Migrations.Migrations;

[Migration("20261014164028", "add-email-index-to-users", Transaction = TransactionMode.Never)]
public sealed class AddEmailIndexToUsers : IMigration
{
    public async Task UpAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        await context.Database.GetCollection<BsonDocument>("users").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("email"),
                new CreateIndexOptions { Unique = true, Name = "email_1" }),
            cancellationToken: cancellationToken);

        context.Logger.LogInformation("Created unique index on users.email");
    }

    public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) =>
        context.Database.GetCollection<BsonDocument>("users").Indexes.DropOneAsync("email_1", cancellationToken);
}
```

`MigrationContext` gives you:

| Member | Meaning |
|---|---|
| `Database` | The configured `IMongoDatabase`. |
| `Session` | The `IClientSessionHandle` when the migration runs in a transaction, otherwise `null`. |
| `Logger` | An `ILogger`. Output appears in the tool's output (`info: ...`) or your app's logs. |
| `Services` | The `IServiceProvider` for the run. |
| `Run` | The run id, batch, direction and environment. |
| `Results` | Named values and notes to store with this run. See [Recording results](#recording-results). |
| `Track(collection)`, `Track(database)` | Wrappers that run operations and record what they did. |

A migration's checksum is taken from its source code (ignoring whitespace and comments), so editing an applied
migration is detected. Write a new migration instead.

### Transactions

With `transaction: auto` (the default), each migration runs in a transaction when the deployment supports them
(replica sets and sharded clusters). Its state document is written in the same transaction, so a failure leaves
nothing half-applied. Pass `context.Session` to every operation:

```csharp
[Migration("20261014164029", "split-full-name")]
public sealed class SplitFullName : IMigration
{
    public Task UpAsync(MigrationContext context, CancellationToken cancellationToken) =>
        context.Database.GetCollection<BsonDocument>("users").UpdateManyAsync(
            context.Session,
            Builders<BsonDocument>.Filter.Exists("fullName"),
            new EmptyPipelineDefinition<BsonDocument>()
                .AppendStage<BsonDocument, BsonDocument, BsonDocument>(BsonDocument.Parse("""
                    { $set: {
                        firstName: { $arrayElemAt: [ { $split: ["$fullName", " "] }, 0 ] },
                        lastName:  { $arrayElemAt: [ { $split: ["$fullName", " "] }, 1 ] } } }
                    """)),
            cancellationToken: cancellationToken);

    public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) =>
        context.Database.GetCollection<BsonDocument>("users").UpdateManyAsync(
            context.Session,
            Builders<BsonDocument>.Filter.Exists("firstName"),
            Builders<BsonDocument>.Update.Unset("firstName").Unset("lastName"),
            cancellationToken: cancellationToken);
}
```

Set `Transaction` on `[Migration]` to override the config:

- `TransactionMode.Never` is for operations that can't run in a transaction, such as building indexes on existing
  collections.
- `TransactionMode.Always` fails the migration if transactions aren't available.

On a `TransientTransactionError` (e.g. a write conflict or a primary election), the transaction is aborted and the
migration runs again from the start. On an `UnknownTransactionCommitResult`, only the commit is retried. Retries back
off from 100ms to 5s and stop after `transactionRetryTimeout`. **A transactional migration can run more than once**,
so keep any side effects outside the database (HTTP calls, files) idempotent.

### Recording results

A migration can record what it did. Results are stored on the migration's state document and its history entry, shown
after the `✔` line, and listed in full by [`show`](#show).

```csharp
[Migration("20261022120000", "tier-customers")]
public sealed class TierCustomers : IMigration
{
    public async Task UpAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        var customers = context.Track(context.Database.GetCollection<BsonDocument>("customers"), "gold");
        await customers.UpdateManyAsync(
            Builders<BsonDocument>.Filter.Gte("spend", 10_000),
            Builders<BsonDocument>.Update.Set("tier", "gold"),
            cancellationToken: cancellationToken);

        var untiered = await context.Database.GetCollection<BsonDocument>("customers")
            .Find(context.Session, Builders<BsonDocument>.Filter.Exists("spend", false))
            .Project(c => c["_id"]).ToListAsync(cancellationToken);
        context.Results.Set("untiered", untiered);
        context.Results.Set("cutoff", DateTime.UtcNow);
        if (untiered.Count > 0)
        {
            context.Results.Note($"{untiered.Count} customers have no spend and were left untiered.", NoteLevel.Warning);
        }
    }

    // ...
}
```

```
✔ 20261022120000 tier-customers   (0.84s, transaction)
    gold      matched 1520 · modified 1518 · upserted 0
    untiered  [2 items]
    cutoff    2026-10-22T12:31:03Z
    1 operation(s) recorded – migrate-mongo show 20261022120000
```

`context.Results` holds:

- **Named values.** `Set(key, value)` takes strings, numbers, booleans, dates, `Guid`s, `ObjectId`s, `BsonValue`s,
  lists and any object the driver can serialize (records, anonymous types, ...). `Increment(key)` keeps a count, and
  `Remove(key)` takes a value away. Keys follow MongoDB field-name rules (no `.`, no leading `$`, up to 64
  characters). `Track(result, label)` adds an `UpdateResult`, `ReplaceOneResult`, `DeleteResult` or
  `BulkWriteResult`'s counts to a value. `Guid`s are stored as standard UUIDs (subtype 4), on their own or in lists
  and arrays. Inside your own objects and dictionaries they go through the driver's serializer, which needs a
  representation configured, e.g. `BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard))`.
- **Notes.** `Note(message, level)` records a timestamped `Info` or `Warning`, which is also logged. The first 100 are
  kept, each up to 1,024 characters.

`context.Track(collection, label?)` wraps a collection: its update, replace, delete, insert, bulk write and index
methods pass `context.Session` for you and record each operation's kind, statement (filter, update, index keys and
options), counts and duration. With a label, the counts are also added to that named value. Dropping an index records
its full definition first, so you can see what to recreate. Inserted documents are never recorded, only how many.
`context.Track(database)` does the same for `RunCommandAsync`. `Collection` and `Database` give you the unwrapped
objects for anything else.

An operation that throws is recorded too, with its `error`, before the exception is rethrown. A bulk write or
`insertMany` that fails part-way records the counts of what it applied before the error (with `IsOrdered = false`,
everything that didn't fail); other failed operations record no counts.

Statements can hold data from your filters. Turn them off with `results.captureStatements: false`,
`--no-capture-statements` for one run, or `context.Track(collection, captureStatement: false)` for one collection.

Limits keep the state documents small: the named values can take up to `results.maxBytes` (64 KB), and a migration
that records more fails with `MM-RESULTS-TOO-LARGE`, listing the largest keys. Put large data in your own collection.
The first 200 operations are kept, and a statement over 4 KB is left out (`statementTruncated: true`).

Results are kept when a migration fails. If its transaction was aborted, they're marked as not committed
(`resultsCommitted: false`): they show what the migration got up to, but none of it was saved. On a transaction
retry, only the last attempt's results are kept. `DownAsync` can record results too; they're stored on the
`rolledBack` history entry.

### Dependency injection

Migrations can take constructor dependencies. The source generator writes the factory, so no reflection is used.
Register services in `Program.cs`:

```csharp
return await MigrateMongoRunner.RunAsync(args, services =>
{
    services.AddSingleton<IClock, SystemClock>();
});
```

`MigrateMongoRunnerOptions` also takes `ConfigureServices`, extra `Assemblies` to scan (e.g. a class library that
references `Kevsoft.MigrateMongo.Abstractions`), and a `TunnelProvider`.

### Custom templates

To change what `create` writes (a base class, team usings, a header comment, ...), set `migrationTemplate` to your own
file. These placeholders are replaced and everything else is copied as-is:

| Placeholder | Example |
|---|---|
| `{{namespace}}` | `MyApp.Migrations.Migrations` |
| `{{id}}` | `20261014164028` |
| `{{className}}` | `AddEmailIndexToUsers` |
| `{{name}}` | `add-email-index-to-users` |

```csharp
using Kevsoft.MigrateMongo;
using MongoDB.Driver;

namespace {{namespace}};

[Migration("{{id}}", "{{name}}")]
public sealed class {{className}} : IMigration
{
    public Task UpAsync(MigrationContext context, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) => throw new NotImplementedException();
}
```

If the template lives inside the migrations project, don't give it a `.cs` extension, or it'll be compiled.
`create --minimal` ignores `migrationTemplate` and writes the minimal built-in template.

### Build errors

The source generator finds migrations at compile time and reports mistakes as build errors:

| Code | Meaning |
|---|---|
| `MM0001` | Two migrations share the same id. |
| `MM0002` | The id isn't a 14-digit `yyyyMMddHHmmss` timestamp. |
| `MM0003` | A `[Migration]` class doesn't implement `IMigration`. |
| `MM0004` | The migration class can't be constructed (abstract, private, or no accessible constructor). |
| `MM0005` | The migration name is empty. |

## Commands

These options work with every command:

| Option | Meaning |
|---|---|
| `-f`, `--file <path>` | Config file (default: `migrate-mongo.yaml` in the current directory). |
| `-e`, `--env <name>` | Use an environment from the config file. |
| `--json` | Write JSON lines instead of text. The last line is `{"type":"result","result":{...}}`. |
| `--no-build` | Don't build the migrations project first; reuse the last build. Tool only. |
| `--runner <path>` | Run a pre-built runner (a `.dll` or a [bundle](#bundles)) instead of building. Tool only. |
| `--insecure-skip-host-key-check` | Don't verify the SSH tunnel's host key. Avoid outside throwaway environments. |

The tool builds the project, then prints the runner's versions:

```
Built src/MyApp.Migrations/MyApp.Migrations.csproj (3.6s)
Runner: Kevsoft.MigrateMongo 0.2.0 · MongoDB.Driver 3.12.0 · .NET 10.0.12 · protocol 1
```

Running the project directly (`dotnet run --project src/MyApp.Migrations -- up`) or a bundle accepts the same commands
and options, except the tool-only ones and `init`, `create`, `bundle` and `version`.

### `status`

```
$ migrate-mongo status
 ID              Name                      State    Batch  Applied
 20261014164027  initial                   applied  1      2026-10-14 16:40:50
 20261014164028  add-email-index-to-users  pending
 20261014164029  split-full-name           pending
2 pending migration(s).
```

States are `pending`, `applied`, `applying`, `rollingBack` and `failed`. Applied migrations that are no longer in the
code, or whose checksum has changed, are flagged. `--check` exits with code `2` when there are pending migrations.

### `plan`

```
$ migrate-mongo plan
  ▶ 20261014164028  add-email-index-to-users   (no transaction)
  ▶ 20261014164029  split-full-name   (transaction)
Would apply 2 migration(s) as batch 2.
```

Shows what `up` would do. Exits with code `2` when there is pending work, so it can gate a deployment.

### `up`

```
$ migrate-mongo up
Acquired lock (run ba6b5b60f25e4861a4ba25597d63fd8d).
▶ 20261014164028 add-email-index-to-users
  info: Created unique index on users.email
✔ 20261014164028 add-email-index-to-users   (0.13s)
▶ 20261014164029 split-full-name
✔ 20261014164029 split-full-name   (0.02s, transaction)
Applied 2 migration(s) in batch 2.
```

Applies every pending migration as one batch. `--to <id>` stops after that migration. `--no-capture-statements`
records [tracked operations](#recording-results) without their statements (also on `down`).

### `down`

```
$ migrate-mongo down
Acquired lock (run ff6217413f1343948fb456e1ff2e435a).
◀ 20261014164029 split-full-name
✔ 20261014164029 split-full-name   (0.03s, transaction)
Rolled back 1 migration(s).
```

Rolls back the newest applied migration. Use one of:

| Option | Rolls back |
|---|---|
| `-n`, `--count <n>` | The newest `n` migrations. |
| `--batch` | Everything from the last `up`. |
| `--to <id>` | Everything after `<id>` (not `<id>` itself). |

### Dry runs

`up --dry-run` and `down --dry-run` (with any of their other options) show what would happen:

```
$ migrate-mongo down --batch --dry-run
  ◀ 20261014164029  split-full-name   (transaction)
  ◀ 20261014164028  add-email-index-to-users   (no transaction)
Dry run: would roll back 2 migration(s); nothing was changed.
```

A dry run makes the same checks as a real run (validation, blocked migrations, unknown `--to`, transaction support)
and exits with the code a real run would. It never runs migration code, takes the lock or writes anything, so it
works with a read-only user. If another run holds the lock, it warns instead of waiting. Unlike `plan`, it exits `0`
when there's pending work.

### `validate`

```
$ migrate-mongo validate
Validation passed.
```

Exits with code `4` if an applied migration was edited, an applied migration is missing from the code, or a pending
migration is older than the newest applied one (unless `allowOutOfOrder` is set). Run it in pull request builds.

### `baseline`

```
$ migrate-mongo baseline --to 20261014164029
Baselined 3 migration(s) in batch 1.
```

Records every migration up to and including `--to` as applied without running it. Use it to adopt a database that
already has those changes.

### `mark`

```bash
migrate-mongo mark 20261014164029 --applied     # it finished, or accept an edited migration's new checksum
migrate-mongo mark 20261014164029 --unapplied   # it didn't; run it again next time
```

### `unlock`

```bash
migrate-mongo unlock            # remove an expired lock
migrate-mongo unlock --force    # remove a lock even if it hasn't expired; only if you're sure its run is dead
```

### `history`

```
$ migrate-mongo history
 At                   ID              Name                      Event       Batch  Run       Host
 2026-10-14 16:40:50  20261014164027  initial                   applied     1      a621daa9  build-agent-12
 2026-10-14 16:40:51  20261014164028  add-email-index-to-users  applied     2      ba6b5b60  build-agent-12
 2026-10-14 16:40:51  20261014164029  split-full-name           applied     2      ba6b5b60  build-agent-12
 2026-10-14 16:40:54  20261014164029  split-full-name           rolledBack  2      ff621741  kev-laptop
```

Pass an id to see one migration, and `--limit <n>` to change the number of entries (default 50). When any entry has
[results](#recording-results), a `Results` column summarises them (`status` has one too).

### `show`

```
$ migrate-mongo show 20261022
20261022120000 tier-customers
  State     applied (batch 4, up, transaction)
  Applied   2026-10-22 12:31:03 by runner@build-agent-12 (0.84s)
  Checksum  sha256:253b18c7...
  Source    csharp MyApp.Migrations.Migrations.TierCustomers
  Versions  tool 0.2.0, library 0.2.0, driver 3.12.0
  Run       ba6b5b60f25e4861a4ba25597d63fd8d

Results
  gold      { matched: 1520, modified: 1518, upserted: 0 }
  untiered  [ObjectId("64b7f0c2a1b2c3d4e5f60718"), ObjectId("64b7f0c2a1b2c3d4e5f60719")]
  cutoff    2026-10-22T12:31:03Z

Notes
  12:31:03  warning  2 customers have no spend and were left untiered.

Operations
  1. updateMany customers [gold]  matched 1520 · modified 1518 · upserted 0  (0.61s)
       filter: { spend: { $gte: 10000 } }
       update: { $set: { tier: "gold" } }

History
 At                   ID              Name            Event    Batch  Run       Host            Error  Results
 2026-10-22 12:31:03  20261022120000  tier-customers  applied  4      ba6b5b60  build-agent-12         gold: matched 1520 · modified 1518 · upserted 0, untiered: …
```

Shows everything about one migration: its state, who applied it, its results, notes and operations, and its history.
The id can be a unique prefix. `--history <n>` changes how many history entries are shown (default 10), and
`--no-statements` leaves the operations' statements out. A rolled-back migration is still found through its history.

### `bundle` and `version`

`bundle` is described in [Bundles](#bundles). `version` prints the tool's version, platform and runtime.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Success. |
| `1` | A migration failed, or another error. |
| `2` | Pending migrations exist (`plan`, `status --check`). |
| `3` | Timed out waiting for the lock. |
| `4` | Validation failed (checksum mismatch, missing or out-of-order migrations). |
| `5` | The tool and runner have no protocol version in common. Update the tool. |
| `6` | Building the migrations project failed. |

## What gets stored

| Collection | Contents |
|---|---|
| `_migrations` | One document per migration, keyed by id, with its state, batch, checksum, timings and who applied it. |
| `_migrations.history` | An append-only log of every apply, rollback, failure and `mark`. |
| `_migrations.lock` | The lock document while a run is in progress, with its owner, expiry and heartbeat. |

```js
{
  _id: "20261014164028", v: 1, name: "add-email-index-to-users", source: "csharp",
  origin: "MyApp.Migrations.Migrations.AddEmailIndexToUsers",
  checksum: "sha256:253b18c7...", state: "applied", direction: "up", batch: 2, transactional: false,
  appliedAt: ISODate("2026-10-14T16:40:51.567Z"), durationMs: 119, runId: "ba6b5b60...",
  appliedBy: { host: "build-agent-12", user: "runner", tool: "migrate-mongo/0.2.0 (linux-x64; aot)",
               library: "0.2.0", driver: "3.12.0", runtime: "10.0.12" }
}
```

A migration that [records results](#recording-results) also has these fields, on its state document and its history
entry. Values are stored as native BSON, so you can query them:

```js
{
  results: { gold: { matched: Long(1520), modified: Long(1518), upserted: Long(0) },
             untiered: [ObjectId("64b7f0c2..."), ObjectId("64b7f0c2...")], cutoff: ISODate("2026-10-22T12:31:03Z") },
  notes: [ { at: ISODate("2026-10-22T12:31:03Z"), level: "warning", message: "2 customers have no spend and..." } ],
  resultsCommitted: true,
  operations: [
    { kind: "updateMany", collection: "customers", label: "gold",
      filter: { spend: { $gte: 10000 } }, update: { $set: { tier: "gold" } },
      counts: { matched: Long(1520), modified: Long(1518), upserted: Long(0) },
      durationMs: Long(611), at: ISODate("2026-10-22T12:31:03Z") }
  ]
}
```

`notesTruncated` and `operationsTruncated` count what went over the limits, and `statementTruncated: true` marks an
operation whose statement was too large to keep. A failed operation has an `error` with the exception's type and
message.

Before running a migration, its state is set to `applying` (or `rollingBack`). If the process dies part-way through a
migration that isn't in a transaction, the state stays there and later runs stop until you resolve it.

## When something goes wrong

**An applied migration was edited** (exit code `4`):

```
✖ 20261014164027 initial was modified after being applied (applied sha256:8eaa3c91..., current sha256:c6700cfc...).
  Create a new migration instead, or run `mark 20261014164027 --applied` to accept the change.
```

Revert the edit and write a new migration, or accept it with `mark --applied`. `onChecksumMismatch: warn` turns this
into a warning.

**A migration crashed part-way without a transaction** (exit code `1`):

```
✖ 20261014164029 split-full-name is in state 'applying' from run 6704b0c1 on build-agent-12. Inspect the data,
  then run `mark 20261014164029 --applied` or `mark 20261014164029 --unapplied`.
```

**Another run holds the lock** (exit code `3`):

```
… Waiting for lock held by run 6704b0c1 (build-agent-07), which expires at 2026-10-14 16:51:12Z.
✖ Timed out after 60s waiting for the migration lock held by run 6704b0c1 (build-agent-07), which expires at 2026-10-14 16:51:12Z.
```

Locks are renewed while a run is in progress and expire `lockTtl` seconds after a run dies. Remove one sooner with
`unlock --force`, but only if you're sure its run has stopped. Ctrl+C and `SIGTERM` cancel the running migration
(through its `CancellationToken`) and release the lock.

**The tool is older than the runner** (exit code `5`): update the tool (`dotnet tool update Kevsoft.MigrateMongo.Tool`).

## Running in CI

### GitHub Actions

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
          BASTION_SSH_KEY: ${{ secrets.BASTION_SSH_KEY }}   # if prod has a tunnel:
```

As a pull request gate, `migrate-mongo validate --env staging` fails with exit code `4` if someone edited an applied
migration. `migrate-mongo plan --env prod` exits with `2` if a deployment has migrations to apply.

### Build once, deploy many

Build a [bundle](#bundles) in the build job and run it on deploy agents that don't have .NET:

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - run: dotnet tool restore
      - run: dotnet tool run migrate-mongo bundle -r linux-x64 -o out
      - uses: actions/upload-artifact@v4
        with: { name: migrations, path: out/ }

  deploy:
    needs: build
    runs-on: [self-hosted, production]
    steps:
      - uses: actions/download-artifact@v4
        with: { name: migrations }
      - run: chmod +x MyApp.Migrations && ./MyApp.Migrations up --env prod
        env: { PROD_MONGODB_URL: ${{ secrets.PROD_MONGODB_URL }} }
```

## Bundles

A bundle is your migrations project published as a self-contained, single-file executable, so it runs where there's
no .NET installed:

```
$ migrate-mongo bundle -r linux-x64 -o out
Created out/MyApp.Migrations (linux-x64, 78.3 MB) and out/migrate-mongo.yaml
$ ./out/MyApp.Migrations up --env staging
```

| Option | Meaning |
|---|---|
| `-r`, `--runtime` | Runtime identifier, e.g. `linux-x64`, `linux-musl-x64`, `win-x64`, `osx-arm64`. Defaults to the current machine's. Any platform can be built from any OS. |
| `-o`, `--output` | Output directory (default `bundle`). |

The bundle is named after the project, and `migrate-mongo.yaml` is copied next to it. It looks for its config in the
current directory and then next to itself. It takes the same commands, options and exit codes as the tool. When the
bundle is for the current platform, `bundle` checks it starts.

For [tunnels](#tunnelling-through-a-bastion), either add `Kevsoft.MigrateMongo.Tunnel` to the project so the bundle
opens them itself, or drive the bundle with the standalone tool, which also gives you the tool's output:

```bash
migrate-mongo up --env prod --runner ./out/MyApp.Migrations
```

## Docker

`ghcr.io/kevbite/migrate-mongo` (`linux/amd64` and `linux/arm64`) is tagged with the version (`0.2.0`, `0.2`) and
`latest`. Pin a version in real pipelines.

- `ghcr.io/kevbite/migrate-mongo:<version>` is the tool on the .NET 10 SDK, with the .NET 8 and 9 runtimes, so it can
  build and run your migrations project. A `global.json` that pins an older SDK won't work in it.
- `ghcr.io/kevbite/migrate-mongo:<version>-bundle` is the tool without .NET, for running [bundles](#bundles) with
  `--runner`.

The working directory is `/src`. Run the SDK image as your user so build output in the mount isn't owned by root:

```bash
docker run --rm -v "$PWD:/src" --user "$(id -u):$(id -g)" -e HOME=/tmp \
  -e PROD_MONGODB_URL ghcr.io/kevbite/migrate-mongo:0.2.0 up --env prod

docker run --rm -v "$PWD:/src" -e PROD_MONGODB_URL \
  ghcr.io/kevbite/migrate-mongo:0.2.0-bundle up --env prod --runner ./out/MyApp.Migrations
```

## Tunnelling through a bastion

If the database is only reachable through an SSH jump host, add a `tunnel:` (usually to an environment). The tool
opens the SSH connection itself (no `ssh` binary needed), verifies the host key, and gives the driver a local SOCKS5
proxy, so every replica set member is reachable by the host names in the connection string:

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
| `host` / `port` | The SSH server. `host` is required; `port` defaults to `22`. |
| `user` | The SSH user. Required. |
| `password` | Password authentication (also answers keyboard-interactive prompts). |
| `privateKey` | The private key's contents, e.g. from a CI secret. Literal `\n` sequences become new lines. |
| `privateKeyPath` | A private key file. `~/` and paths relative to the config file work. |
| `passphrase` | The passphrase for an encrypted private key. |
| `hostKeyFingerprint` | The server's `SHA256:...` host key fingerprint, as printed by `ssh-keygen -lf`. |
| `knownHostsPath` | A `known_hosts` file to check the host key against. Defaults to `~/.ssh/known_hosts`. |
| `mode` | `socks` (default) or `local`. |

You need `password`, `privateKey` or `privateKeyPath` (not both key settings). The host key is always verified, against
`hostKeyFingerprint` if set or the `known_hosts` file otherwise. If verification fails, the error shows the server's
fingerprint so you can pin it.

`socks` mode needs `MongoDB.Driver` 3.12 or later, the first version with SOCKS5 support. `local` mode works with any
driver version: it forwards a local port to the first host in the connection string and connects with
`directConnection=true`. It can't use `mongodb+srv://` URLs, and TLS host name validation fails unless the
certificate also covers `127.0.0.1`.

When the tool drives the runner, the tool opens the tunnel. Running the project directly or as a bundle rejects a
`tunnel:` unless the project references `Kevsoft.MigrateMongo.Tunnel` and sets the provider:

```bash
dotnet add package Kevsoft.MigrateMongo.Tunnel
```

```csharp
using Kevsoft.MigrateMongo;
using Kevsoft.MigrateMongo.Tunnel;

return await MigrateMongoRunner.RunAsync(args, new MigrateMongoRunnerOptions
{
    TunnelProvider = new SshTunnelProvider(),
});
```

The package is opt-in, so SSH.NET is only in your dependencies if you need it.

## AWS IAM authentication (Atlas)

To connect without a password using an AWS IAM role (MongoDB Atlas "AWS IAM" database users), reference
`Kevsoft.MigrateMongo.Aws` from the migrations project and use `authMechanism=MONGODB-AWS`:

```bash
dotnet add package Kevsoft.MigrateMongo.Aws     # or: migrate-mongo init --auth aws
```

```yaml
environments:
  prod:
    mongodb:
      url: mongodb+srv://cluster0.xxxxx.mongodb.net/?authSource=%24external&authMechanism=MONGODB-AWS
      databaseName: myapp
```

The runner, bundles and Hosting register the mechanism with the driver when the URL asks for it, so no code is
needed. Without the package, a `MONGODB-AWS` URL fails before connecting with a message saying which package to add.
If you build your own `MongoClientSettings`, you can call `AwsAuthentication.Register()` yourself (it's idempotent).

Credentials come from the standard AWS sources, so nothing secret goes in the URL:

- the `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY` and `AWS_SESSION_TOKEN` environment variables;
- web identity (`AWS_WEB_IDENTITY_TOKEN_FILE` and `AWS_ROLE_ARN`);
- the ECS task or EC2 instance role.

The tool passes its environment to the runner, so set these wherever you run `migrate-mongo`. In Atlas, create a
database user with AWS IAM authentication for the role's ARN (`arn:aws:iam::123456789012:role/deploy`), not the
assumed-role session ARN. Atlas still checks its IP access list against where the connection comes from: the CI
runner, or the bastion if you use a `tunnel:` (`mode: socks` for `mongodb+srv://` URLs).

GitHub Actions, using OIDC to assume a role:

```yaml
    permissions: { id-token: write, contents: read }
    steps:
      - uses: aws-actions/configure-aws-credentials@v4
        with: { role-to-assume: arn:aws:iam::123456789012:role/deploy, aws-region: eu-west-2 }
      - run: dotnet tool run migrate-mongo up --env prod
```

Bitbucket Pipelines, using OIDC web identity (or any script that exports the `AWS_*` variables):

```yaml
- step:
    oidc: true
    script:
      - export AWS_ROLE_ARN=arn:aws:iam::123456789012:role/deploy
      - export AWS_WEB_IDENTITY_TOKEN_FILE=$(pwd)/web-identity-token
      - echo $BITBUCKET_STEP_OIDC_TOKEN > $(pwd)/web-identity-token
      - ./MyApp.Migrations up --env prod
```

## Running from your application

Reference your migrations (the migrations project, or a class library with them) from your app, add the hosting
package, and apply migrations at startup:

```bash
dotnet add package Kevsoft.MigrateMongo.Hosting
```

```csharp
builder.Services
    .AddMongoMigrations(o =>
    {
        o.ConnectionString = builder.Configuration.GetConnectionString("Mongo"); // omit to use your registered IMongoClient
        o.DatabaseName = "myapp";
    })
    .FromAssemblyContaining<AddEmailIndexToUsers>()
    .RunOnStartup();
```

`RunOnStartup()` applies pending migrations before any hosted service starts, so ASP.NET Core doesn't serve requests
against an out-of-date database. If a migration fails, or the lock isn't acquired within `LockWaitTimeout`, the host
fails to start with a `MongoMigrationException`. Progress goes to your app's `ILogger`, and migrations can take your
app's services (including scoped ones) as constructor dependencies.

App instances and CLI runs share the same state and lock, so it's safe when many instances start at once: one applies
the migrations and the others wait, then find nothing to do. Keep the options consistent with `migrate-mongo.yaml`.
The defaults match the config file's.

| Option | Config equivalent |
|---|---|
| `ConnectionString`, `DatabaseName` | `mongodb.url`, `mongodb.databaseName` |
| `StateCollection` | `stateCollection` |
| `LockTtl`, `LockWaitTimeout` | `lockTtl`, `lockWaitTimeout` (as `TimeSpan`s) |
| `OnChecksumMismatch` | `onChecksumMismatch` |
| `Transaction`, `TransactionRetryTimeout` | `transaction`, `transactionRetryTimeout` |
| `AllowOutOfOrder` | `allowOutOfOrder` |
| `CaptureStatements`, `ResultsMaxBytes` | `results.captureStatements`, `results.maxBytes` |
| `Environment` | Recorded with each run. |
| `Tunnel`, `TunnelProvider` | `tunnel` (needs `Kevsoft.MigrateMongo.Tunnel`) |

To run migrations yourself instead (e.g. from an admin endpoint or a `--migrate` switch), leave out `RunOnStartup()`
and use `IMongoMigrationRunner`:

```csharp
var runner = app.Services.GetRequiredService<IMongoMigrationRunner>();
var result = await runner.UpAsync();   // also StatusAsync, PlanAsync, DryRunUpAsync, ValidateAsync, ShowAsync(id) and ExecuteAsync("down", ...)
if (result.ExitCode != 0) { /* result.Message says why */ }
```

Through an SSH bastion:

```csharp
o.Tunnel = new TunnelConfig { Host = "bastion.example.com", User = "deploy", PrivateKey = key, HostKeyFingerprint = "SHA256:..." };
o.TunnelProvider = new SshTunnelProvider();
```
