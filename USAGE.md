# Kevsoft.MigrateMongo — Usage Walkthrough

How a developer would go from nothing to running migrations locally, in CI and on deploy agents.
This describes the **planned** behaviour from [PLAN.md](PLAN.md). Command names, flags and output may
change as the design settles.

> Command name used below: `migrate-mongo` (open question §11.1 in the plan).

---

## 1. Install the tool

```bash
# As a global tool (Native AOT; doesn't need any .NET runtime to run)
dotnet tool install -g Kevsoft.MigrateMongo.Tool

# Or pinned per-repo (recommended for CI reproducibility)
dotnet new tool-manifest
dotnet tool install Kevsoft.MigrateMongo.Tool

# Or without .NET at all (e.g. a deploy agent)
curl -fsSL https://github.com/kevbite/Kevsoft.MigrateMongo/releases/latest/download/install.sh | sh
```

```bash
$ migrate-mongo version
migrate-mongo 1.0.0 (linux-x64; aot)
```

---

## 2. Initialise a project

Run from the repository root:

```bash
$ migrate-mongo init --project src/MyApp.Migrations
Created migrate-mongo.yaml
Created src/MyApp.Migrations/MyApp.Migrations.csproj
Created src/MyApp.Migrations/Program.cs
Next: set MONGODB_URL, then run `migrate-mongo create <Name>` and `migrate-mongo status`.
```

What gets generated:

**`migrate-mongo.yaml`**
```yaml
project: src/MyApp.Migrations
mongodb:
  url: ${MONGODB_URL}
  databaseName: myapp
stateCollection: _migrations
lockTtl: 300
lockWaitTimeout: 60
onChecksumMismatch: fail
transaction: auto
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

**`src/MyApp.Migrations/MyApp.Migrations.csproj`**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>   <!-- whatever your repo uses -->
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Kevsoft.MigrateMongo.Runner" Version="1.0.0" />
    <PackageReference Include="MongoDB.Driver" Version="3.5.2" />  <!-- your choice, >= 3.5 -->
  </ItemGroup>
</Project>
```

**`src/MyApp.Migrations/Program.cs`**
```csharp
return await MigrateMongoRunner.RunAsync(args);
```

> The migrations project is an ordinary console app that **you** own. It builds with your SDK and runs on
> your runtime, using the library and driver versions you pick. The global tool only builds it, launches
> it and renders the results.

You can also add migrations to an existing project instead. Add the `Kevsoft.MigrateMongo.Runner`
package and point `project:` at it.

---

## 3. Create a migration

```bash
$ migrate-mongo create AddEmailIndexToUsers
Created src/MyApp.Migrations/Migrations/M20261009145210_AddEmailIndexToUsers.cs
```

The generated file:

```csharp
using Kevsoft.MigrateMongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyApp.Migrations.Migrations;

[Migration("20261009145210", "add-email-index-to-users")]
public sealed class AddEmailIndexToUsers : IMigration
{
    public async Task UpAsync(MigrationContext ctx, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public async Task DownAsync(MigrationContext ctx, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
}
```

Fill it in using the normal MongoDB driver API:

```csharp
[Migration("20261009145210", "add-email-index-to-users", Transaction = TransactionMode.Never)]
public sealed class AddEmailIndexToUsers : IMigration
{
    public async Task UpAsync(MigrationContext ctx, CancellationToken ct)
    {
        var users = ctx.Database.GetCollection<BsonDocument>("users");

        // Normalise existing data first
        await users.UpdateManyAsync(
            Builders<BsonDocument>.Filter.Exists("email"),
            new[] { new BsonDocument("$set", new BsonDocument("email", new BsonDocument("$toLower", "$email"))) },
            cancellationToken: ct);

        await users.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("email"),
                new CreateIndexOptions { Unique = true, Name = "email_1" }),
            cancellationToken: ct);

        ctx.Logger.LogInformation("Created unique index on users.email");
    }

    public Task DownAsync(MigrationContext ctx, CancellationToken ct) =>
        ctx.Database.GetCollection<BsonDocument>("users").Indexes.DropOneAsync("email_1", ct);
}
```

A data change that should be atomic can run in a transaction. This is the default (`auto`) on replica
sets. Pass `ctx.Session` to every operation:

```csharp
[Migration("20261009150030", "split-full-name")]
public sealed class SplitFullName : IMigration
{
    public async Task UpAsync(MigrationContext ctx, CancellationToken ct)
    {
        var users = ctx.Database.GetCollection<BsonDocument>("users");
        var pipeline = new EmptyPipelineDefinition<BsonDocument>()
            .AppendStage<BsonDocument, BsonDocument, BsonDocument>(BsonDocument.Parse("""
                { $set: {
                    firstName: { $arrayElemAt: [ { $split: ["$fullName", " "] }, 0 ] },
                    lastName:  { $arrayElemAt: [ { $split: ["$fullName", " "] }, 1 ] } } }
                """));

        await users.UpdateManyAsync(ctx.Session, Builders<BsonDocument>.Filter.Exists("fullName"),
            pipeline, cancellationToken: ct);
    }

    public Task DownAsync(MigrationContext ctx, CancellationToken ct) => /* reverse */ Task.CompletedTask;
}
```

Mistakes are caught at **compile time** by the source generator:

```
error MM0001: Duplicate migration id '20261009145210' (AddEmailIndexToUsers, AddEmailIndex)
error MM0002: Migration id 'abc' must be a 14-digit timestamp (yyyyMMddHHmmss)
```

---

## 4. Check what will happen

```bash
$ export MONGODB_URL="mongodb://localhost:27017/?replicaSet=rs0"

$ migrate-mongo status
Building src/MyApp.Migrations ... done (3.1s)
Runner: Kevsoft.MigrateMongo 1.0.0 · MongoDB.Driver 3.5.2 · .NET 10.0.1 · protocol 1

 ID              Name                        State     Batch  Applied
 20261009144500  initial                     applied   1      2026-10-09 14:46:02
 20261009145210  add-email-index-to-users    pending
 20261009150030  split-full-name             pending

$ migrate-mongo plan
Would apply 2 migration(s) as batch 2:
  20261009145210  add-email-index-to-users   (no transaction)
  20261009150030  split-full-name            (transaction)
$ echo $?
2          # 2 = there is pending work (useful as a CI gate)
```

---

## 5. Apply migrations

```bash
$ migrate-mongo up
Building src/MyApp.Migrations ... done (0.8s, up to date)
Acquired lock (run 6704a1f2...)
▶ 20261009145210 add-email-index-to-users
  info: Created unique index on users.email
✔ 20261009145210 add-email-index-to-users   (1.42s)
▶ 20261009150030 split-full-name
✔ 20261009150030 split-full-name            (0.31s, transaction)
Applied 2 migration(s) in batch 2. Released lock.
```

Other variants:

```bash
migrate-mongo up --to 20261009145210     # stop after a specific migration
migrate-mongo up --env prod              # use the `prod` environment from migrate-mongo.yaml
migrate-mongo up --json                  # machine-readable output
migrate-mongo up --no-build              # reuse the last build
```

What's now in MongoDB (`_migrations`):

```js
{
  _id: "20261009145210", v: 1, name: "add-email-index-to-users", source: "csharp",
  origin: "MyApp.Migrations.Migrations.AddEmailIndexToUsers, MyApp.Migrations",
  checksum: "sha256:4b2e...", state: "applied", batch: 2, transactional: false,
  appliedAt: ISODate("2026-10-09T13:55:01Z"), durationMs: 1420,
  appliedBy: { host: "kev-laptop", user: "kev", tool: "migrate-mongo/1.0.0 (win-x64; aot)",
               library: "Kevsoft.MigrateMongo/1.0.0", driver: "3.5.2", runtime: "10.0.1" }
}
```

---

## 6. Roll back

```bash
$ migrate-mongo down                     # last migration only
◀ 20261009150030 split-full-name
✔ rolled back (0.20s)

$ migrate-mongo down --batch             # everything from the last `up` run
$ migrate-mongo down --to 20261009144500 # back to (but not including) a given migration
$ migrate-mongo history 20261009150030   # full audit trail incl. rollbacks
```

---

## 7. When something goes wrong

**Changed an applied migration**
```bash
$ migrate-mongo up
✖ Checksum mismatch: 20261009145210 add-email-index-to-users was modified after being applied.
  Applied: sha256:4b2e...  Current: sha256:91ac...
  Create a new migration instead, or run `migrate-mongo mark 20261009145210 --applied` to accept the change.
$ echo $?
4
```

**Migration crashed half-way (no transaction)**
```bash
$ migrate-mongo status
 20261009160000  backfill-orders   applying   3   (run 6704b0c1 on build-agent-12, 14 min ago)

$ migrate-mongo up
✖ 20261009160000 is in state 'applying' from a previous run. Inspect the data, then resolve it with:
    migrate-mongo mark 20261009160000 --applied     # it actually finished
    migrate-mongo mark 20261009160000 --unapplied   # it didn't; it will run again
```

**Another run is holding the lock**
```bash
$ migrate-mongo up
… waiting for lock held by run 6704b0c1 (build-agent-07), expires in 42s
✖ Timed out after 60s waiting for lock      # exit code 3
$ migrate-mongo unlock --force                # only if you're sure that run is dead
```

**Tool and library versions incompatible**
```bash
$ migrate-mongo up
✖ The runner in src/MyApp.Migrations uses Kevsoft.MigrateMongo 3.0.0 (protocol 3);
  this tool supports protocols 1–2. Update the tool: dotnet tool update Kevsoft.MigrateMongo.Tool
$ echo $?
5
```

---

## 8. Running in CI (GitHub Actions)

### Option A: build and migrate in one job, through an SSH bastion

```yaml
jobs:
  migrate:
    runs-on: ubuntu-latest
    environment: production
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4        # only needed to *build* the migrations project
        with: { global-json-file: global.json }
      - run: dotnet tool restore
      - run: |
          dotnet tool run migrate-mongo validate --env prod
          dotnet tool run migrate-mongo up --env prod
        env:
          PROD_MONGODB_URL: ${{ secrets.PROD_MONGODB_URL }}   # e.g. mongodb://db1.internal,db2.internal/?replicaSet=rs0
          BASTION_SSH_KEY: ${{ secrets.BASTION_SSH_KEY }}
```

Because `prod` has a `tunnel:` section, the tool:
1. opens an SSH connection to `bastion.example.com` and verifies the host key fingerprint;
2. starts a SOCKS5 proxy on `127.0.0.1:<random port>`;
3. launches the runner with the proxy settings, so the driver reaches every replica set member
   (`db1.internal`, `db2.internal`, …) through the bastion;
4. closes the tunnel when the command finishes.

### Option B: build once, deploy many (bundles)

```yaml
jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { global-json-file: global.json }
      - run: dotnet tool restore
      - run: dotnet tool run migrate-mongo bundle -r linux-x64 -o out/migrations
      - uses: actions/upload-artifact@v4
        with: { name: migrations, path: out/ }

  deploy-staging:
    needs: build
    runs-on: [self-hosted, staging]           # no .NET SDK/runtime needed here
    steps:
      - uses: actions/download-artifact@v4
        with: { name: migrations }
      - run: chmod +x migrations && ./migrations up --env staging
        env: { STAGING_MONGODB_URL: ${{ secrets.STAGING_MONGODB_URL }} }
```

A bundle is a self-contained single-file build of your migrations project. `migrate-mongo.yaml` is copied
next to it in `out/`. It accepts the same commands as the tool. If you also want the tool's rendering and tunnelling on the deploy agent, use the standalone
tool binary to drive it:

```bash
migrate-mongo up --env prod --runner ./migrations
```

### Option C: Docker

```bash
docker run --rm -v "$PWD:/src" -w /src \
  -e PROD_MONGODB_URL -e BASTION_SSH_KEY \
  ghcr.io/kevbite/migrate-mongo:1 up --env prod
```

### PR gate: fail if someone edited an applied migration

```yaml
      - run: dotnet tool run migrate-mongo validate --env staging --json > validate.json
```

`validate` exits with code 4 on checksum drift, missing migrations or out-of-order ids. The tool also
emits GitHub annotations pointing at the offending file.

---

## 9. Running on application startup (optional)

The same migrations project can be referenced from your app instead of, or as well as, the CLI:

```csharp
// Program.cs of MyApp.Api
builder.Services
    .AddMongoMigrations(o =>
    {
        o.ConnectionString = builder.Configuration.GetConnectionString("Mongo")!;
        o.DatabaseName = "myapp";
    })
    .FromAssemblyContaining<MyApp.Migrations.Migrations.AddEmailIndexToUsers>()
    .RunOnStartup();          // IHostedService; blocks startup until migrations complete
```

App instances and CLI runs share the same `_migrations` state and `_migrations.lock` lock. If five pods
start at once, one applies the migrations, the others wait for the lock, see nothing pending, and carry on.

---

## 10. End-to-end summary

```
 dev machine                         CI (build)                       deploy agent / CI (release)
 ───────────                         ──────────                       ───────────────────────────
 migrate-mongo init                  dotnet tool restore              ./migrations validate --env prod
 migrate-mongo create <Name>         migrate-mongo validate           ./migrations up --env prod
 edit Migrations/M..._<Name>.cs      migrate-mongo bundle -r <rid>      (or migrate-mongo up --env prod
 migrate-mongo plan / up / down        → artifact                         for tunnel + rich output)
 git commit
```
