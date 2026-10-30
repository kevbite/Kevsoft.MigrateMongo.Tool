# Kevsoft.MigrateMongo — Project Plan

A MongoDB migration toolkit for .NET inspired by [migrate-mongo](https://www.npmjs.com/package/migrate-mongo):

* Migrations are written in **C#** (other formats such as JS/JSON are deferred but the architecture has a
  pluggable loader for them).
* The **CLI never breaks because of the installed .NET runtime/SDK version**, and never breaks because the
  user's project uses a different version of our library or of the MongoDB driver.
* Works well on **build servers** (CI/CD), including through **SSH tunnels / bastions**.

> **Implementation status (v1 build).** Implemented: Abstractions + source generator, engine, MongoDB
> adapter, Runner, the `migrate-mongo` tool (all commands in §5), unit tests, Atlas Local integration tests
> (`tests/Kevsoft.MigrateMongo.IntegrationTests`, fixtures in `tests/fixtures`) and GitHub Actions
> (`ci.yml`, `release.yml`, `nightly.yml`). Where the build differs from this plan:
> * **.NET 10 or later only.** .NET 8 and 9 reach end of support on 10 Nov 2026, so the libraries, tool, templates,
>   fixtures and Docker image target .NET 10. The tool package is a `net10.0` pointer to the RID-specific Native AOT
>   packages (+ an `any` framework-dependent fallback with `RollForward=LatestMajor`), which .NET 10+ SDKs install.
>   `linux-musl-*` must be listed explicitly, otherwise the RID graph would pick the glibc `linux-*` build on Alpine.
>   Standalone AOT binaries are also on GitHub Releases.
> * Spectre.Console isn't used; the tool has a small hand-written renderer, so AOT has no third-party risk.
> * `IMigrationSource.GetMigrations()` is synchronous; `init` doesn't scaffold an initial migration.
> * `create` only reads `project` from the config, so it works without connection variables set.
> * Also done: SSH tunnels (§6, SOCKS + local forward), automatic retry of transient
>   transaction errors (`transactionRetryTimeout`), and `Kevsoft.MigrateMongo.Hosting` (§4.5).
> * `up --dry-run` / `down --dry-run` are done. They don't run migration code (so there's no
>   `MigrationRunInfo.DryRun`), take the lock or write anything, and return the exit code a real run would.
>   `plan` stays separate (exit `2` with pending work).
> * **Pre-1.0: no compatibility with earlier releases.** Nobody depends on the 0.x packages yet, so breaking changes
>   are fine and there are no version checks, upgrade notes or shims for older tools/runners. The protocol rules
>   (§3), ApiCompat baselines and tests against previously published runners apply from 1.0.
> * `Kevsoft.MigrateMongo.Templates` is done: `migratemongo` (project, same output as `init` plus a
>   `migrate-mongo.yaml` with `project: .`) and `migratemongo-migration` (same file as `create`). The Runner
>   version is stamped in at pack time; unit tests keep the template files identical to `Scaffolding`.
>   `create` renders `migrationTemplate:` (a config path) when set: `{{namespace}}`, `{{id}}`, `{{className}}`, `{{name}}`.
> * `bundle` is done (scenario 15): `dotnet publish -c Release -r <rid> --self-contained -p:PublishSingleFile=true`
>   into `-o` (default `bundle/`), copies the config next to the executable and handshakes with it when the RID is
>   the current one. Not trimmed (the driver isn't trim-safe). Integration tests run a bundle directly, through
>   `--runner`, and a cross-built `linux-*` bundle in `runtime-deps:10.0` (no .NET).
> * `Kevsoft.MigrateMongo.Tunnel` is done: the SSH code moved out of the tool behind `IMongoTunnelProvider`
>   (Contracts). The standalone runner, bundles and hosting tunnel when `TunnelProvider = new SshTunnelProvider()`
>   is set; without it they reject `tunnel:` configs with a hint. In protocol mode the tool still opens the tunnel.
> * `install.sh` / `install.ps1` and Docker images are done. Release archives are versionless
>   (`migrate-mongo-<rid>.tar.gz|zip`) so `releases/latest/download/...` always works; the scripts verify
>   `SHA256SUMS`. Images are `ghcr.io/kevbite/migrate-mongo:{version, major.minor, latest}` (tool: AOT binary on
>   `sdk:10.0`) and `...-bundle` (`runtime-deps:10.0`), built from the prebuilt AOT binaries.
>   CI tests the scripts on every RID and both images in the integration tests.
> * `Kevsoft.MigrateMongo.Aws` is done: `MongoDriverBackend.Create` checks the URL's `authMechanism`; for
>   `MONGODB-AWS` it registers the driver's AWS mechanism through the package (found by name, so no code changes),
>   or fails up front saying which package to add. `init --auth aws` / `dotnet new migratemongo --auth aws` add it.
>   X.509 and OIDC need no package (built into the driver), so they're just a URL.
> * Deferred: scripting loaders.

---

## 1. Research summary

### 1.1 Prior art

| Tool | Lang | Migration format | Notable ideas worth copying |
|------|------|------------------|-----------------------------|
| **migrate-mongo** | Node | JS module exporting `up(db, client)` / `down(db, client)` | `init`/`create`/`up`/`down`/`status`; lock collection with `lockTtl`; file hash (re-run detection); `migrationBlock` (undo a whole batch); config file overridable with `-f`; customisable migration template |
| **Mongock** → **Flamingock** | Java | `@ChangeUnit` classes with `@Execution` / `@RollbackExecution` / `@BeforeExecution` | Distributed lock document; each change wrapped in a transaction when the server supports it, otherwise manual rollback; rich audit collection (author, duration, state, error); runs embedded in the app on startup *or* standalone |
| **doesntmattr/mongodb-migrations** | PHP | Classes modelled on Doctrine Migrations, plus optional raw JS script directory | Separate connection config from migration config (YAML/XML); `generate`, `migrate`, `execute` (single version up/down), `status`, `version` (manually mark applied/unapplied); Symfony bundle |
| **golang-migrate (mongodb driver)** | Go | JSON arrays of `db.runCommand` documents | Language-neutral, declarative migrations; `x-transaction-mode`; advisory lock with timeout + exponential back-off; single static binary |
| **EF Core migrations / dotnet-ef** | .NET | Compiled C# classes | Migration bundles (`efbundle`, self-contained exe for deploy agents); runs the user's compiled code out-of-process with the user's own runtime. **Pain points to avoid:** the `dotnet-ef` tool is itself framework-dependent (needs a matching runtime) and must be version-aligned with `Microsoft.EntityFrameworkCore.Design` in the project. |
| Flyway / Liquibase | JVM | SQL / changelogs (Liquibase has a MongoDB extension) | `validate` (checksum drift), `baseline`, `repair`, `info`, dry-run, per-environment config, JSON output for CI |

### 1.2 Runtime-proofing findings (.NET)

* Framework-dependent global tools roll forward on minor/patch only. A tool built for net8.0 won't run
  on a machine with only .NET 9/10 unless it sets `<RollForward>LatestMajor</RollForward>`.
* .NET 10 SDK supports **RID-specific, self-contained and Native AOT tool packages**
  (`ToolPackageRuntimeIdentifiers` + `PublishAot`). The pointer package tells `dotnet tool install`
  which RID package to fetch; adding `any` to the RID list provides a framework-dependent fallback.
  Docs: <https://learn.microsoft.com/dotnet/core/tools/rid-specific-tools>
* C# migrations are user code, so *something* must run on the user's runtime. If that is the **user's own
  process** (built by their SDK, referencing their chosen package versions), the runtime always matches.
  The global tool should therefore only *orchestrate* that process, never load user assemblies itself.

### 1.3 MongoDB driver version findings

* **MongoDB.Driver 3.0** moved `MongoDB.Driver.Core` into the main package and made most of its public
  API internal. It also removed LINQ2 (`MongoClientSettings.LinqProvider`) and APIs deprecated in 2.30.
  Libraries compiled against 2.x then failed at runtime with `MissingMethodException`/`TypeLoadException`
  when apps upgraded. Real examples: Aspire.MongoDB.Driver, jbogard's
  MongoDB.Driver.Core.Extensions.DiagnosticSources.
* Lesson: the risk is **our compiled library calling driver members** that a different driver version no
  longer has. User migration code is compiled against whichever driver they reference, so it's never
  mismatched with itself.
* SOCKS5 proxy support (`MongoClientSettings.Socks5ProxySettings`, `proxyHost`/`proxyPort` URI options)
  exists from driver **3.12** (verified while building the tunnel; the plan originally said 3.5).

### 1.4 Connectivity findings

* Migrations typically run from CI agents that can't reach a private cluster directly
  (Atlas private endpoints, DocumentDB in a VPC, self-hosted replica sets behind a bastion).
* A plain SSH local port-forward to a **replica set** breaks: the driver discovers members by their
  internal hostnames. Workarounds: `directConnection=true` to a single node (primary only), or a
  **SOCKS5 dynamic forward** so every discovered member is reached through the proxy.
* SSH.NET supports `ForwardedPortDynamic` (SOCKS) and `ForwardedPortLocal`, so the tunnel can be
  in-process with no `ssh` binary required on the agent.

---

## 2. Goals / non-goals

**Goals**
1. Feature parity with migrate-mongo's CLI (`init`, `create`, `up`, `down`, `status`) plus CI-friendly
   extras (`validate`, `plan`/dry-run, `unlock`, `baseline`, `mark`, `history`, JSON output, exit codes).
2. **C# migrations by default**, discovered at compile time and loaded through a pluggable
   `IMigrationSource` so other formats can be added later.
3. **No version coupling** between:
   * the CLI and the machine's .NET runtime (Native AOT, no runtime needed),
   * the CLI and the library version in the user's project (out-of-process + versioned protocol),
   * our library and the MongoDB driver minor/patch versions (isolated adapter + compat test matrix).
4. First-class build-server support: env-var config, non-interactive, SSH tunnel / SOCKS5 proxy, TLS,
   cloud auth (MONGODB-AWS, X.509, OIDC), prebuilt bundles for deploy agents without an SDK.
5. The same migrations can run on app startup (Mongock-style) or from the CLI, sharing state and lock.

**Non-goals (v1)**
* JS (Jint) and JSON-command migrations. Deferred, but anticipated by `IMigrationSource` (§4.4).
* Schema diffing/model snapshots (MongoDB is schemaless).
* Compatibility with migrate-mongo's `changelog` collection. We use our own, safer state model (§5.4).
  Teams adopting the tool on an existing database use `baseline`.
* MongoDB.Driver 2.x. v1 targets driver 3.5+. A 2.x adapter package is possible later if there is demand (§4.3).
* Wrapping the MongoDB driver API for users (see §4.3 for why).

---

## 3. Architecture

```
 ┌──────────────────────────────┐   build (user's SDK)    ┌─────────────────────────────────────────┐
 │ migrate-mongo (global tool)  │ ──────────────────────▶ │ User's migrations project (console app)  │
 │ Native AOT, no .NET needed   │                         │  ├─ Kevsoft.MigrateMongo.Runner          │
 │  • config / env / --env      │  spawn + handshake      │  ├─ Kevsoft.MigrateMongo (engine)        │
 │  • init / create scaffolding │ ──────────────────────▶ │  ├─ Kevsoft.MigrateMongo.MongoDriver     │
 │  • build orchestration       │  NDJSON events (stdout) │  ├─ Kevsoft.MigrateMongo.Abstractions    │
 │  • SSH tunnel → local SOCKS  │ ◀────────────────────── │  ├─ MongoDB.Driver (user-chosen ≥ 3.5)   │
 │  • rendering, exit codes, CI │                         │  └─ Migrations/*.cs                      │
 └──────────────────────────────┘                         └─────────────────────────────────────────┘
                                                                 ▲ same engine, state & lock
                                                          ┌──────┴──────────────────────────────────┐
                                                          │ User's app: Kevsoft.MigrateMongo.Hosting │
                                                          │ (IHostedService / startup migration)     │
                                                          └─────────────────────────────────────────┘
```

**Key idea:** the global tool is a runtime-free *launcher*. The code that touches user migrations and the
driver runs in a process the **user's SDK built**, with the **user's** package versions. The two talk over a
small, versioned JSON protocol, so no assembly is ever loaded across the boundary.

### 3.1 Tool ⇄ runner protocol
1. The tool resolves the runner: `project:` from config → `dotnet build` (unless `--no-build`) →
   `dotnet msbuild -getProperty:TargetPath` to find the output. Alternatively `--runner <path>` runs a
   prebuilt exe/dll or bundle (CI "build once, deploy many").
2. **Handshake:** `runner --mm-handshake` → `{"protocol":[1,2],"library":"1.4.0","driver":"3.5.2","runtime":"10.0.1"}`.
   The tool picks the highest common protocol version. If there isn't one, it fails with a clear message
   such as "update the tool to ≥ X or Kevsoft.MigrateMongo to ≥ Y", never a `MissingMethodException`.
3. **Invocation:** command and resolved settings are passed on stdin as JSON, with secrets in the payload
   rather than the argv/env of the child process. The runner streams NDJSON events on stdout
   (`migrationStarted`, `migrationCompleted`, `lockWaiting`, `result`, …) and diagnostics on stderr.
   The tool renders human/JSON output and maps results to exit codes.
4. **Compatibility rules:** additive changes only within a protocol version; unknown fields ignored; a
   protocol version is supported for at least one major version of both tool and library.

### 3.2 Distribution (the "don't break on runtime version" requirement)

| Channel | Build | Needs .NET on the machine? |
|---------|-------|----------------------------|
| `dotnet tool install -g Kevsoft.MigrateMongo.Tool` (.NET 10+ SDK) | `net10.0` → RID-specific **Native AOT** packages (`win-x64;win-arm64;linux-x64;linux-arm64;linux-musl-x64;linux-musl-arm64;osx-x64;osx-arm64`) **+ `any`** fallback for other RIDs | Only the SDK to *install* it; not to run |
| GitHub Releases | Standalone AOT binaries + checksums (+ install script) | No |
| Docker image (`ghcr.io/kevbite/migrate-mongo`) | AOT tool + .NET SDK base (needs to build user projects) and a `runtime-deps` variant for bundles | No (for bundles) |
| **Migration bundle** (`migrate-mongo bundle`) | `dotnet publish` of the user's migrations project: self-contained single-file runner | No. Runs on deploy agents directly or via the tool's `--runner` |

The SDK is needed only to *build* the user's migrations project, and that's the SDK their repo already
pins via `global.json`. Running the tool or a bundle needs no .NET at all.

**Hard rules**
* The tool never loads user assemblies or our runtime library. It only spawns processes.
* Tool dependencies must be trim/AOT-compatible (`IsAotCompatible`, warnings as errors; System.Text.Json
  source generation).

### 3.3 CLI libraries
* **Parsing: System.CommandLine 2.x** (stable since Nov 2025, AOT/trim-friendly, used by the `dotnet` CLI)
  in both the tool and the runner. In the runner it ships inside the user's dependency graph, where a
  Microsoft package minimises conflict risk.
* **Rendering: Spectre.Console** (tables, colours, progress) in the **tool only**, not its `.Cli` parser,
  which relies on reflection. If the spike shows AOT warnings, replace it with a small hand-rolled table/ANSI
  writer. The runner emits NDJSON and plain logs only.
* **Fallback:** ConsoleAppFramework v5 (source-generated, zero-reflection) if System.CommandLine proves too
  verbose.
* Rejected: Spectre.Console.Cli, CliFx, Cocona, McMaster.Extensions.CommandLineUtils. All rely on
  reflection, so Native AOT is unsupported or fragile; the last two are also largely unmaintained.

---

## 4. Migration authoring model

### 4.1 C# migrations (default)

```csharp
// Migrations/M20261009120000_AddEmailIndex.cs
[Migration("20261009120000", "add-email-index")]
public sealed class AddEmailIndex : IMigration
{
    public async Task UpAsync(MigrationContext ctx, CancellationToken ct)
    {
        var users = ctx.Database.GetCollection<BsonDocument>("users");
        await users.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(Builders<BsonDocument>.IndexKeys.Ascending("email"),
                                               new CreateIndexOptions { Unique = true }),
            cancellationToken: ct);
    }

    public Task DownAsync(MigrationContext ctx, CancellationToken ct) =>
        ctx.Database.GetCollection<BsonDocument>("users").Indexes.DropOneAsync("email_1", ct);
}
```

* `[Migration(id, name, Transaction = TransactionMode.Auto | Always | Never)]`. The id is a sortable
  `yyyyMMddHHmmss` timestamp generated by `migrate-mongo create`.
* Optional `IIrreversibleMigration` marker (no `DownAsync`) so `down` fails fast with a clear message.
* Constructor injection is supported (`IServiceProvider` from the runner/host), e.g. for options or
  `TimeProvider`.
* **Source generator** (shipped as an analyzer in `Kevsoft.MigrateMongo.Abstractions`):
  * generates a static migration registry (no runtime reflection scanning, trim-friendly, fast startup);
  * reports compile errors for duplicate/malformed ids and for missing `IMigration`;
  * embeds a **checksum** per migration: SHA-256 of the class's syntax tree with trivia (whitespace,
    comments) removed. Reformatting doesn't trip validation, but logic changes do. Limitation (documented):
    changes in helper code outside the class aren't detected.

### 4.2 `MigrationContext` — ours, deliberately small

```csharp
public sealed class MigrationContext
{
    public IMongoDatabase Database { get; }          // the real driver object
    public IClientSessionHandle? Session { get; }    // non-null when running in a transaction
    public ILogger Logger { get; }                   // Microsoft.Extensions.Logging.Abstractions
    public IServiceProvider Services { get; }
    public MigrationRunInfo Run { get; }             // RunId, Batch, Direction, Environment, DryRun
}
```
It's our type, so we can add members without breaking anyone. It only *exposes* driver objects. Our
library never calls methods on them on the user's behalf.

### 4.3 Driver-version resilience — should we wrap `IMongoDatabase`?

**Recommendation: no, don't wrap the driver API for users. Do isolate *our own* use of the driver.**

Why not a user-facing wrapper:
* **It doesn't remove the coupling, it moves it.** The wrapper is compiled against *some* driver version,
  so a breaking driver release breaks the wrapper in exactly the way it would break us today.
* **User code isn't where `MissingMethodException` comes from.** A migration is compiled against the
  driver the user references, in the same build. The failure mode is a *pre-built library* (ours) calling
  a member that a different driver version no longer has. That was the Aspire/DiagnosticSources issue
  with driver 3.0.
* The driver surface we'd have to mirror is huge (Builders, LINQ, aggregation, bulk writes, indexes,
  search indexes, change streams, GridFS). A wrapper would always lag, block users from driver docs and
  examples, and leave us maintaining an ORM-sized API.
* Migrations are write-once history. When a user upgrades the driver and an old migration no longer
  compiles, that's a *compile-time* error in their own code, which is fixable and visible. It isn't a
  runtime surprise.

What we do instead:
1. **Driver-free engine.** `Kevsoft.MigrateMongo` (planning, ordering, checksum validation, state
   machine, locking protocol, run orchestration) has **no reference to MongoDB.Driver**. It talks to ports:
   `IMigrationStateStore`, `IMigrationHistory`, `IDistributedLock`, `ITransactionScopeFactory`. This also
   makes it unit-testable with in-memory fakes.
2. **One adapter project** `Kevsoft.MigrateMongo.MongoDriver` implements those ports. It's the *only*
   place we call driver members, and it sticks to a deliberately conservative subset: `BsonDocument`-typed
   collections, `Builders<BsonDocument>`, `InsertOne/FindOneAndUpdate/UpdateOne/DeleteOne/Find`,
   `Indexes.CreateOne`, `StartSession/WithTransaction`, `MongoClientSettings`. It uses no LINQ, no POCO
   class maps, no `*.Core` or internal-ish APIs, and nothing marked `[Obsolete]` (CS0618 as an error, so
   deprecations surface the moment we bump our reference, before the driver removes them).
3. **Abstractions reference only stable driver *types*** (`IMongoDatabase`, `IClientSessionHandle`), never
   members. Type references survive across driver versions as long as the type exists. These two have
   been stable since driver 2.0 and survived 3.0.
4. **Dependency range:** `MongoDB.Driver >= 3.5.0` with no upper bound. NuGet upper bounds cause
   resolution errors for users. We compensate with the compat matrix in §8.3 (min, latest stable, and
   latest prerelease as a canary).
5. **Feature detection** for anything newer than our floor, e.g. a newer driver API, behind a check that
   falls back gracefully.
6. **When driver 4.x ships:** if the canary shows a break, we release a fixed
   `Kevsoft.MigrateMongo.MongoDriver` (or a parallel `MongoDriver4` adapter package, if the two versions
   can't be supported from one binary). The engine, abstractions, state model and tool are untouched.
   The same route provides a `MongoDriver2` adapter if 2.x support is ever wanted.
7. **Our own API stability:** `EnablePackageValidation` + ApiCompat baselines on `Abstractions` so *we*
   never ship a binary-breaking change in a minor release.

### 4.4 Pluggable loaders (`IMigrationSource`)

```csharp
public interface IMigrationSource
{
    IAsyncEnumerable<MigrationDescriptor> GetMigrationsAsync(CancellationToken ct);
}
// MigrationDescriptor: Id, Name, Source ("csharp"), Checksum, TransactionMode, Reversible,
//                      Func<MigrationContext, CancellationToken, Task> Up / Down
```
* **v1:** `GeneratedMigrationSource` (from the source generator) is the default and only registered
  source. A `ReflectionMigrationSource` exists as a fallback for projects that disable generators.
* **Later (backburner):** `Kevsoft.MigrateMongo.Scripting` adds `JsMigrationSource` (Jint) and
  `JsonCommandMigrationSource` (golang-migrate-style `*.up.json`). Because they plug in as sources, the
  engine, state model and tool need no changes. Their checksum is a hash of the normalised file content.
* Multiple sources can be combined. The engine merges them by id and rejects duplicates.

### 4.5 Running in-app

`Kevsoft.MigrateMongo.Hosting`:
`services.AddMongoMigrations(o => ...).FromAssemblyContaining<AddEmailIndex>()` plus either `RunOnStartup()`
(an `IHostedLifecycleService` that migrates in `StartingAsync`, before any hosted service such as the web server
starts, and fails host startup on error), or `IMongoMigrationRunner` for explicit control. It uses the same
engine, state collections and lock, so CLI and app runs coordinate safely. The connection comes from
`ConnectionString` or the app's registered `IMongoClient`; each run gets a DI scope for migration dependencies.

---

## 5. Feature set

### 5.1 Commands

| Command | Description |
|---------|-------------|
| `init` | Scaffold `migrate-mongo.yaml` and a migrations console project (`Program.cs` calling `MigrateMongoRunner.RunAsync(args)`, package refs, sample migration). The tool writes files itself; no `dotnet new` template install needed. A `dotnet new` template package is also published. |
| `create <name>` | New `Migrations/M<timestamp>_<Name>.cs` from the template (customisable via `migrationTemplate:`) |
| `up [--to <id>] [--dry-run]` | Apply pending migrations (all share one `batch` number) |
| `down [--to <id> \| --batch \| --count N]` | Roll back last migration / to a target / whole last batch |
| `status [--json] [--exit-code]` | Table of applied/pending/changed/missing/failed/in-flight migrations |
| `plan` | Like `up --dry-run`, but exits 0/2 for "nothing/something to do" |
| `validate` | Fail if applied migrations have changed checksums, are missing, or pending ones are out of order |
| `baseline --to <id>` | Mark migrations up to `<id>` as applied without running them (adopting an existing DB) |
| `mark <id> --applied\|--unapplied` | Manual fix-ups, incl. resolving `failed`/`applying` state |
| `history [<id>] [--json]` | Show the audit trail |
| `unlock [--force]` | Release a stale lock |
| `bundle [-r <rid>] [-o <dir>]` | Publish the migrations project as a self-contained single-file runner (with `migrate-mongo.yaml` copied alongside) |
| `version` | Tool version + RID; with a project, also the runner's library/driver/runtime versions from the handshake |

The runner exe accepts the same commands directly, so a bundle works stand-alone on a deploy agent:
`./migrations up --env prod`.

### 5.2 Engine behaviours
* **State, history and lock** collections — see §5.4.
* **Checksums**: `onChecksumMismatch: fail|warn` (default `fail`, Flyway semantics).
* **Transactions**: project default `transaction: auto|always|never`, overridable per migration via the
  attribute. `auto` uses a session transaction when the topology supports it (replica set/sharded) and the
  migration doesn't opt out. Some operations, such as some index builds and `collMod`, can't run in
  transactions; the docs list them.
* **Ordering & safety**: refuse to apply a pending migration older than the latest applied one unless
  `--allow-out-of-order`.
* **Cancellation**: Ctrl+C / SIGTERM (forwarded by the tool to the runner) stops after the current
  migration and releases the lock.
* **Exit codes**: 0 success, 1 failure, 2 pending changes (`plan`/`status --exit-code`), 3 lock timeout,
  4 validation failure, 5 tool/runner protocol mismatch, 6 build failure.
* **Output**: human table by default, `--json` for machines, `--no-color`; GitHub Actions / Azure DevOps
  log groups and annotations when detected.

### 5.3 Configuration
Precedence: CLI flags > env vars (`MIGRATE_MONGO_*`) > `--config` file > defaults. The tool reads config
and passes the resolved settings to the runner. Bundles/in-app hosting read the same file/env via the library.
```yaml
# migrate-mongo.yaml
project: src/MyApp.Migrations         # migrations project the tool builds & runs
mongodb:
  url: ${MONGODB_URL}                 # env var interpolation
  databaseName: app
  options: { tls: true, retryWrites: false }   # retryWrites:false for DocumentDB
stateCollection: _migrations          # history/lock derive as _migrations.history / _migrations.lock
lockTtl: 300                          # seconds; renewed by heartbeat while running
lockWaitTimeout: 60
onChecksumMismatch: fail
transaction: auto
environments:                         # optional, --env prod
  prod:
    mongodb: { url: ${PROD_MONGODB_URL} }
tunnel: { ... }                       # see §6
```
Secrets are never logged, and connection strings are redacted in output.

### 5.4 Database state model

Our own design, not compatible with migrate-mongo. Three collections, with names derived from `stateCollection`:

**`_migrations`** — current state, one document per applied (or in-flight/failed) migration:
```js
{
  _id: "20261009120000",                 // migration id
  v: 1,                                  // document schema version
  name: "add-email-index",
  source: "csharp",                      // csharp (later: js | json)
  origin: "MyApp.Migrations.AddEmailIndex, MyApp.Migrations",
  checksum: "sha256:9f86d081884c7d65...",
  state: "applied",                      // applying | applied | failed | rollingBack
  batch: 7,                              // all migrations from one `up` run share a batch
  runId: ObjectId("..."),
  transactional: true,
  appliedAt: ISODate("2026-10-09T11:02:17Z"),
  durationMs: 412,
  appliedBy: {
    host: "build-agent-12", user: "svc-ci",
    tool: "migrate-mongo/1.2.0 (linux-x64; aot)",
    library: "Kevsoft.MigrateMongo/1.4.0", driver: "3.5.2", runtime: "10.0.1",
    ci: { provider: "github-actions", runId: "123456", commit: "a1b2c3d" }
  },
  error: { message: "...", type: "...", at: ISODate("...") }   // only when state = failed
}
```

**`_migrations.history`** — append-only audit trail (the tool never updates or deletes it):
```js
{ _id: ObjectId, migrationId: "20261009120000", event: "applied",   // applied | rolledBack | failed |
  runId, batch, at: ISODate, durationMs, checksum, by: { ... },      //   baselined | marked
  error: { ... } }
```
Index `{ migrationId: 1, at: -1 }`. An optional `historyRetentionDays` setting adds a TTL index on `at`.

**`_migrations.lock`** — a single document `{ _id: "lock", runId, owner: { host, pid, user }, acquiredAt,
expiresAt, heartbeatAt }`.

**Design properties:**
* **Keyed by migration id (`_id`):** MongoDB enforces uniqueness, writes are idempotent upserts, and
  renaming a class/name doesn't look like a new migration (reported as a warning).
* **Write-ahead state.** We write `state: "applying"` first, then switch it to `applied`/`failed`. A crash
  mid-way through a non-transactional migration is therefore visible. The next run refuses to continue past
  an `applying`/`failed` migration until `mark` or `down` resolves it. Inside a transaction, the state write
  is part of the **same transaction** as the migration, so data and state change together.
* **Current state and history are separate.** `status` is a cheap read. `down` deletes the state
  document, while the history keeps every apply, rollback, failure and manual fix.
* **Integer batch numbers**, `max(batch) + 1` computed while holding the lock. They aren't timestamps, so
  agents with skewed clocks can't collide.
* **Self-describing checksums** (`sha256:` prefix) let us change the algorithm later.
* **Versioned documents (`v`).** A library that finds a `v` newer than it understands refuses to write, so
  an old pinned version on one pipeline can't corrupt state written by a newer one.
* **Lock correctness doesn't depend on TTL indexes**, which only run every ~60 s. We check expiry
  explicitly:
  * acquire with `insertOne`, or with a conditional `findOneAndUpdate` when `expiresAt < now`;
  * a heartbeat extends `expiresAt` during long migrations;
  * release deletes the lock only if `runId` still matches;
  * acquisition retries with exponential back-off up to `lockWaitTimeout`.
* **Provenance** records tool, library, driver and runtime versions plus CI run/commit, answering
  "who/what ran this?".

---

## 6. Connectivity for build servers

The SSH tunnel runs **in the AOT tool**. It opens an SSH.NET `ForwardedPortDynamic` on `127.0.0.1:<random>`
and passes it to the runner as SOCKS5 proxy URL options, which driver 3.12+ supports (older drivers fail fast
with a message to upgrade or use `mode: local`). This keeps SSH.NET out of the user's dependency graph.
The tunnel code lives in `Kevsoft.MigrateMongo.Tunnel` behind `IMongoTunnelProvider` (in Contracts). The
standalone runner, bundles and in-app hosting opt in by referencing the package and setting `TunnelProvider`;
otherwise they reject `tunnel:` configs.

| Mode | Config | Use case |
|------|--------|----------|
| Direct | `mongodb.url` | CI can reach the cluster (Atlas IP allow-list, self-hosted agent in VPC) |
| **SSH tunnel – SOCKS (default when `tunnel` set)** | `tunnel: { host, port, user, password \| privateKey \| privateKeyPath, passphrase, hostKeyFingerprint \| knownHostsPath }` | Replica sets / sharded clusters behind a bastion; topology discovery works through the proxy |
| SSH tunnel – local forward | `tunnel: { mode: local, ... }` | Rewrites to `directConnection=true` and warns that it reaches only one node |
| External SOCKS5 proxy | `mongodb.options.proxyHost/proxyPort/...` | Corporate proxies, `ssh -D` started by the pipeline, Teleport/Boundary |
| Cloud auth | `authMechanism=MONGODB-AWS` (IAM role on agent), X.509 cert path, OIDC (Azure/GCP workload identity) | Secretless CI |
| TLS extras | `tlsCAFile`, `tlsCertificateKeyFile`, `tlsAllowInvalidHostnames` | DocumentDB CA bundle, private PKI |

Tunnel security:
* Host key verification is **required** (fingerprint or known_hosts) unless
  `--insecure-skip-host-key-check` is passed explicitly.
* Private keys can be supplied as env var contents, as CI secret stores usually provide them.
* The SOCKS listener binds to loopback only, and the tunnel lives only as long as the command.

---

## 7. Solution layout

```
Kevsoft.MigrateMongo.sln
src/
  Kevsoft.MigrateMongo.Abstractions/     # IMigration, [Migration], MigrationContext, IMigrationSource (+ analyzer)
  Kevsoft.MigrateMongo.SourceGenerator/  # Registry + checksum generation, diagnostics (packed into Abstractions)
  Kevsoft.MigrateMongo/                  # Driver-free engine: planner, state machine, lock protocol, config
  Kevsoft.MigrateMongo.MongoDriver/      # ONLY project calling MongoDB.Driver members (state/history/lock/tx)
  Kevsoft.MigrateMongo.Runner/           # MigrateMongoRunner.RunAsync(args): commands, protocol, NDJSON events
  Kevsoft.MigrateMongo.Hosting/          # DI + IHostedService for in-app execution
  Kevsoft.MigrateMongo.Tunnel/           # SSH.NET tunnel (used by the tool; opt-in for runner/bundles/hosting)
  Kevsoft.MigrateMongo.Tool/             # AOT global tool: launcher, scaffolding, build, tunnel, rendering
  Kevsoft.MigrateMongo.Templates/        # dotnet new templates (project + migration)
  (later) Kevsoft.MigrateMongo.Scripting/  # JS (Jint) + JSON command IMigrationSource implementations
tests/
  Kevsoft.MigrateMongo.Tests/                 # Engine unit tests with in-memory ports
  Kevsoft.MigrateMongo.SourceGenerator.Tests/ # Generator snapshot + diagnostic tests
  Kevsoft.MigrateMongo.MongoDriver.Tests/     # Adapter tests (Testcontainers); run against the driver version matrix
  Kevsoft.MigrateMongo.Protocol.Tests/        # Tool ⇄ runner compat using previously published runner versions
  Kevsoft.MigrateMongo.Tool.IntegrationTests/ # End-to-end: real tool binary → real runner → Atlas Local container (§8.2)
  Kevsoft.MigrateMongo.Tool.SmokeTests/       # Install/run matrix across OSes and .NET versions (no DB needed)
  fixtures/
    BasicMigrations/                          # 3 migrations: collection + validator, unique index, transactional data change
    FailingMigrations/                        # 2nd migration throws (transactional and non-transactional variants)
    SlowMigrations/                           # Sleeps; used for lock contention / heartbeat / cancellation tests
samples/
  console-migrations/, in-app-hosting/, bundle-deploy/, github-actions/, azure-devops/
.github/
  workflows/ci.yml, nightly.yml, release.yml  # see §9
  actions/setup-dotnet-cached/action.yml      # composite: setup-dotnet from global.json + NuGet cache
  dependabot.yml
global.json, Directory.Build.props, Directory.Packages.props, nuget.config
```

---

## 8. Testing

### 8.1 Test layers

| Layer | Project | Needs Docker | What it proves |
|-------|---------|--------------|----------------|
| Unit | `Kevsoft.MigrateMongo.Tests` | No | Engine logic: ordering, planning, state transitions, checksum rules, config precedence (in-memory ports) |
| Generator | `…SourceGenerator.Tests` | No | Generated registry/checksums (snapshot tests), diagnostics MM0001+ |
| Adapter | `…MongoDriver.Tests` | Yes | State/history/lock documents, transactions, lock expiry/heartbeat, `v` guard, across the driver matrix |
| Protocol | `…Protocol.Tests` | No | Handshake/negotiation and NDJSON contracts between tool and runner versions |
| **Tool integration** | `…Tool.IntegrationTests` | Yes | The **real tool binary**, building and launching a **real migrations project**, against **Atlas Local** (§8.2) |
| Smoke | `…Tool.SmokeTests` | No | Install + `version`/`init`/`create`/`--help` on every OS/RID and .NET version |

Rules for every layer:
* Unit tests use hand-written expected values; never derive expectations from production code.
* xUnit v3 on Microsoft.Testing.Platform. Docker-dependent tests carry `[Trait("Category", "Integration")]`
  (and `"Tunnel"` for SSH tests), so `dotnet test --filter-not-trait "Category=Integration"` gives a fast loop.

### 8.2 Tool integration tests (Testcontainers + MongoDB Atlas Local)

**Database:** `mongodb/mongodb-atlas-local:<pinned tag>` started by **Testcontainers for .NET**. It's a
single-node replica set out of the box, so transactions work with no `rs.initiate()`, and it matches what
users run locally and in Atlas. Container setup:
* There's no dedicated .NET Atlas module, so use a generic `ContainerBuilder` with image + port 27017.
* Readiness: `Wait.ForUnixContainer().UntilContainerIsHealthy()`, because the image ships a healthcheck.
* Connect with `mongodb://localhost:<mapped>/?directConnection=true`. The replica set member advertises
  its container hostname, so the driver must not try to discover it.
* The spike confirms whether `MongoDbBuilder().WithImage(...)` works directly. Its root-credential
  env var names differ from Atlas Local's, so the generic builder is the safe default.

**Fixtures:**
* `AtlasLocalFixture`: one container per test assembly (assembly fixture). Each test uses its own
  database (`t_<guid>`) so tests run in parallel without interfering. An env var
  `MIGRATE_MONGO_TEST_MONGODB_URL` skips the container and uses an existing instance, e.g. a
  `docker run` you already have or the `atlas deployments` CLI.
* `ToolFixture`: resolves the binary under test:
  * `MIGRATE_MONGO_TOOL_PATH` (CI sets this to the **AOT binary** produced earlier in the pipeline);
  * otherwise it runs `dotnet publish src/Kevsoft.MigrateMongo.Tool` once per test run, framework-dependent
    for a fast local loop.
* `MigrationsProjectFixture`: copies a `tests/fixtures/*` project to a temp dir and writes
  `migrate-mongo.yaml` pointing at the test database. It also writes a `nuget.config` whose local feed is
  `artifacts/packages`, so tests consume the **packed** NuGet packages exactly as users would.
  `MIGRATE_MONGO_PACKAGE_VERSION` selects the version; locally it falls back to `ProjectReference`
  via a `UseProjectReferences=true` property.
* `ToolProcess.RunAsync(args, env)` → `(ExitCode, StdOut, StdErr)`. Tests usually pass `--json` and assert on
  the parsed result plus exit code.

**Assertions read MongoDB directly.** They use the raw driver (`BsonDocument`) with literal expected
values, not our library's types or mappers. That keeps the tests independent of the code under test.

**`BasicMigrations` fixture:**
| Id | Name | Up | Down | Tx |
|----|------|----|------|----|
| `20260101000001` | `create-users` | `createCollection users` with `$jsonSchema` validator | drop `users` | Never |
| `20260101000002` | `users-email-index` | unique index `email_1` | drop index | Never |
| `20260101000003` | `seed-defaults` | insert 2 `roles` docs + 1 `users` doc (`admin@example.com`, `role: "admin"`) | delete those docs | Auto |

**Scenarios:**

1. **`up` applies all migrations and records state.** On a fresh database, run `up`, then assert:
   * exit code `0`; JSON result `applied = ["20260101000001","20260101000002","20260101000003"]`, `batch = 1`;
   * `_migrations` has exactly 3 docs. For each: `_id`, `name`, `v: 1`, `source: "csharp"`,
     `state: "applied"`, `batch: 1`, `checksum` matches `^sha256:[0-9a-f]{64}$`; migration 3 has
     `transactional: true`, 1–2 have `false`; `appliedBy.tool` starts with `migrate-mongo/`; all docs share
     one `runId`; `durationMs >= 0`;
   * `_migrations.history` has 3 `applied` events in order, and its `{ migrationId: 1, at: -1 }` index exists;
   * `_migrations.lock` is **empty** (lock released);
   * effects: `users` collection exists with the expected validator; index `email_1` exists with
     `unique: true`; `roles` has the 2 expected docs; `users` has exactly the expected admin doc.
2. **Second `up` is a no-op.** Exit `0`, `applied = []`, still 3 state docs and 3 history events, no doc
   changes (`appliedAt` unchanged).
3. **`up` then `down` (last migration).** State has 2 docs (migration 3 gone), history = 3× `applied` +
   1× `rolledBack` for `20260101000003`, `roles` empty, admin user removed, index and validator still present.
4. **`up` then `down --batch`.** Everything reverted: `_migrations` empty; `users` dropped; history = 3×
   `applied` + 3× `rolledBack` (reverse order, same new `runId`); `status --json` shows all 3 `pending`.
5. **Batches.** `up --to 20260101000002` (batch 1), then `up` (batch 2), then `down --batch` → only
   migration 3 is reverted; migrations 1–2 keep `batch: 1`.
6. **Round trip.** `up` → `down --batch` → `up` gives the same final state as scenario 1, with
   `batch: 2` and 9 history events.
7. **Failure (non-transactional).** `FailingMigrations` throws in migration 2. Exit `1`; migration 1
   `applied`; migration 2 `state: "failed"` with `error.message`; migration 3 not run; history has a
   `failed` event; lock released. A second `up` exits `1` with the "resolve with `mark`" message;
   `mark --unapplied` then lets `up` run after the fixture is fixed.
8. **Failure (transactional).** Migration 2 writes docs then throws. No docs from migration 2 remain;
   no `_migrations` doc for it (state write rolled back with the data); history records `failed`.
9. **Checksum drift.** After `up`, edit migration 2's body in the temp project; `up`/`validate` exit `4`
   naming `20260101000002`. A formatting-only edit (whitespace/comments) does **not** trigger it.
10. **Concurrency.** Two tool processes run `up` at once (`SlowMigrations`). Both exit `0`, each migration
    has exactly one `applied` history event, and one run reports waiting for the lock.
11. **Stale lock.** Insert an expired lock doc by hand, then `up` succeeds and takes it over. A
    non-expired lock with `lockWaitTimeout: 2` exits `3`; `unlock --force` clears it.
12. **Cancellation.** Send SIGINT/Ctrl+C during `SlowMigrations`. The current migration finishes, the
    lock is released, and the remaining migrations stay pending.
13. **`plan` / `status` exit codes.** `plan` → `2` with pending, `0` after `up`; `status --json` matches the
    expected document.
14. **Newer state version.** Insert a `_migrations` doc with `v: 99`; `up` refuses to write and exits `1`.
15. **Bundle.** `bundle -r <current rid>`, then run the bundle exe directly with `up`; the asserts match
    scenario 1. Also `migrate-mongo up --runner <bundle>`.
16. **Tunnel** (`Category=Tunnel`, Linux only). Testcontainers network with Atlas Local (alias `mongo`, no
    published port) and `linuxserver/openssh-server` (published 2222, generated key). The config uses
    `mongodb://mongo:27017/?directConnection=true` plus a `tunnel:` block, and scenario 1 passes through the
    SOCKS tunnel. A wrong host key fingerprint fails before any DB connection.

**Running locally:**
```bash
# Docker Desktop / Podman running
dotnet test tests/Kevsoft.MigrateMongo.Tool.IntegrationTests
# against the AOT binary instead of a framework-dependent publish
dotnet publish src/Kevsoft.MigrateMongo.Tool -r win-x64 -c Release -o artifacts/tool
$env:MIGRATE_MONGO_TOOL_PATH = "artifacts/tool/migrate-mongo.exe"; dotnet test tests/Kevsoft.MigrateMongo.Tool.IntegrationTests
```

### 8.3 Compatibility matrices
* **Driver:** adapter + tool integration tests run against `MongoDB.Driver` **3.5.0 (floor)** and **latest
  stable** on every PR, and **latest prerelease** nightly. The version is overridden at restore with
  `-p:MongoDriverVersion=<v>`, which central package management uses as `VersionOverride`.
* **Server:** Atlas Local `8.0` (pinned) on every PR; `latest` nightly; plain `mongo:7.0` standalone nightly,
  covering `transaction: auto` falling back to non-transactional.
* **API:** package validation / ApiCompat baselines on `Abstractions` and protocol contracts.
* **Protocol:** the newest tool against fixture runners built with previous library majors, and vice versa.
* **Runtime (smoke):** install the tool in `runtime-deps` (no .NET) and `dotnet/sdk:8.0/9.0/10.0/11.0-preview`;
  fixture projects targeting `net8.0`/`net9.0`/`net10.0`; Windows and macOS runners.

---

## 9. GitHub repository & Actions

### 9.1 Repo setup
* `global.json`: .NET 10 SDK, `rollForward: latestFeature`.
* `Directory.Build.props`:
  * `Nullable`, `TreatWarningsAsErrors`, `Deterministic`, `ContinuousIntegrationBuild` when `CI=true`;
  * package metadata (license MIT, repo URL, icon, README), SourceLink (built into the SDK) and
    `.snupkg` symbols;
  * `EnablePackageValidation` on shipped libraries; MinVer for versions from `v*` git tags.
* `Directory.Packages.props`: central package management (one place for driver, Testcontainers, etc.).
* `dependabot.yml`: weekly updates for NuGet, GitHub Actions and Docker. Actions are pinned by commit SHA.
* Branch protection on `main`: PRs required, CI checks required, linear history.
* Environments:
  * `nuget` (required reviewer) for publishing;
  * `ghcr` for container images.
* Workflow `permissions:` are least-privilege per job (`contents: read` by default).

### 9.2 `ci.yml` — on pull request and push to `main`

```
 build ──┬─▶ aot-tool (matrix: 7 RIDs) ──┬─▶ integration (linux-x64, linux-arm64) ──┐
         │                               ├─▶ smoke (os × .NET matrix) ──────────────┤
         └─▶ driver-compat (floor/latest)┘                                          └─▶ ci-success (required check)
```

| Job | Runner | Steps |
|-----|--------|-------|
| `build` | ubuntu-latest | checkout (full history for MinVer) → setup-dotnet-cached → `dotnet build -c Release` → unit + generator + protocol tests (TRX + coverage) → `dotnet pack` all libraries, templates and the tool **pointer** package + `any` package → upload `packages` artifact |
| `aot-tool` | matrix: `win-x64` windows-latest, `win-arm64` windows-11-arm, `linux-x64` ubuntu-latest, `linux-arm64` ubuntu-24.04-arm, `linux-musl-x64` ubuntu-latest + `alpine` container, `osx-x64` macos-15-intel, `osx-arm64` macos-latest | Native AOT must build on its target OS, so each RID builds on its own runner: `dotnet pack src/Kevsoft.MigrateMongo.Tool -r <rid>` (RID-specific tool package) **and** `dotnet publish -r <rid>` (standalone binary) → upload `tool-<rid>` artifact. The job fails on any AOT/trim warning. |
| `driver-compat` | ubuntu-latest | `MongoDriver.Tests` with `-p:MongoDriverVersion=3.5.0` and `=<latest>` |
| `integration` | ubuntu-latest (x64) and ubuntu-24.04-arm (arm64); Docker is preinstalled | download `packages` + `tool-linux-<arch>` → set `MIGRATE_MONGO_TOOL_PATH`, `MIGRATE_MONGO_PACKAGE_VERSION` → `dotnet test tests/Kevsoft.MigrateMongo.Tool.IntegrationTests` (incl. `Tunnel`) → upload TRX + container logs on failure. GitHub's Windows/macOS runners can't run Linux containers, so DB tests are Linux-only. |
| `smoke` | windows-latest, macos-latest, ubuntu-latest; plus containers `dotnet/sdk:8.0/9.0/10.0/11.0-preview` and `runtime-deps:10.0` | `dotnet tool install --add-source ./packages` (proves pointer → RID package resolution, and the `any` fallback on SDK 8/9) → `version`, `init`, `create`, `--help`; `runtime-deps` runs the standalone binary |
| `ci-success` | ubuntu-latest | `needs: [all]` aggregate gate, so branch protection needs only one required check |

Test results are published as a PR summary (e.g. `dorny/test-reporter`), and coverage goes to the job summary.

### 9.3 `nightly.yml` — scheduled + manual
* Integration tests against **MongoDB.Driver latest prerelease**, **Atlas Local `latest`** and `mongo:7.0`
  standalone.
* On failure: open or update a GitHub issue labelled `canary`, so breaks are known before users hit them.

### 9.4 `release.yml` — on pushed tag `v*.*.*`
1. Run `ci.yml` via `workflow_call` on the tagged commit. MinVer gives the exact version from the tag.
2. **NuGet** (environment `nuget`): push all `.nupkg`/`.snupkg` using **NuGet Trusted Publishing** (OIDC via
   `NuGet/login`; no long-lived API key). This covers the tool pointer, RID-specific and `any` tool packages,
   plus Abstractions, core, MongoDriver, Runner, Hosting, Tunnel and Templates.
3. **GitHub Release**: standalone AOT binaries as `.zip` (Windows) / `.tar.gz` (Linux/macOS), `SHA256SUMS`,
   `install.sh` / `install.ps1`; release notes generated from PRs.
4. **Supply chain**: `actions/attest-build-provenance` for binaries and packages; NuGet packages signed
   if a certificate is available later.
5. **Docker**: multi-arch (`linux/amd64`, `linux/arm64`) images built from the **prebuilt** AOT binaries
   (no QEMU compile), pushed to `ghcr.io/kevbite/migrate-mongo:{version, major.minor, major, latest}` (no
   `major` tag for 0.x). Two variants: SDK-based (builds projects) and `-bundle` on `runtime-deps` (bundles only).
6. Prerelease tags (`v1.2.0-beta.1`) publish to NuGet as prerelease and skip the `latest` Docker tag.

Pushes to `main` (no tag) publish `-alpha.0.N` packages to **GitHub Packages** so changes can be tried
before release.

---

## 10. Milestones

| # | Milestone | Deliverables |
|---|-----------|--------------|
| 0 | **Repo + spikes** (3–4 days) | Repo skeleton (§9.1), `ci.yml` build/unit/pack + `aot-tool` matrix. Spikes: (a) tool spawns `dotnet build` + `-getProperty:TargetPath` + runner handshake/NDJSON; (b) source generator registry + trivia-free checksum; (c) SSH.NET under AOT exposing loopback SOCKS consumed by a driver 3.5 child process; (d) RID-specific tool pack with `any` fallback built across runners; (e) System.CommandLine + Spectre.Console publish with zero AOT warnings; (f) Atlas Local via Testcontainers in GitHub Actions |
| 1 | Abstractions + engine | `IMigration`, `[Migration]`, `MigrationContext`, `IMigrationSource`, generator; driver-free engine with in-memory ports, fully unit tested |
| 2 | MongoDriver adapter | State/history/lock collections (§5.4), transactions, connection building; adapter tests + `driver-compat` job |
| 3 | Runner + tool core | Protocol v1, `init/create/up/down/status`, build orchestration, `--runner`, rendering, exit codes; **tool integration tests scenarios 1–8** + `integration` job |
| 4 | CI hardening | `plan/validate/baseline/mark/unlock/history`, `--json`, env vars/environments, redaction, CI log groups; scenarios 9–14 |
| 5 | Connectivity | SSH tunnel (SOCKS + local), external proxy, AWS/X.509/OIDC docs; scenario 16 |
| 6 | Distribution | `bundle` (scenario 15), `smoke` job, `release.yml`, `nightly.yml`, GitHub Releases, Docker images |
| 7 | Hosting | DI extensions, `IHostedService`, shared lock/state with CLI |
| 8 | Docs | README, "Coming from migrate-mongo" guide (rewrite to C#, `baseline` existing DBs), samples |
| later | Other formats | `Kevsoft.MigrateMongo.Scripting`: JS (Jint) and JSON-command sources |

---

## 11. Open questions / decisions to confirm

1. **Command name**: `migrate-mongo` (familiar, but clashes if the Node version is also on PATH) vs `dotnet-migrate-mongo` / `mongo-migrate`.
2. **Driver floor**: 3.5 (needed for SOCKS tunnelling) vs 3.0 with tunnelling disabled on older drivers.
3. **Migration id type**: timestamp string (proposed) vs `long`.
4. **Checksum default**: `fail` on changed applied migrations (proposed) vs `warn`.
5. **Minimum fallback TFM** for the `any` tool package: resolved. `net10.0`, since .NET 8 and 9 went out of support.
