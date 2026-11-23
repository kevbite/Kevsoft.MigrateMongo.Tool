# Kevsoft.MigrateMongo [![Continuous Integration Workflow](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/actions/workflows/ci.yml/badge.svg)](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/actions/workflows/ci.yml) [![install from nuget](http://img.shields.io/nuget/v/Kevsoft.MigrateMongo.Tool.svg?style=flat-square)](https://www.nuget.org/packages/Kevsoft.MigrateMongo.Tool) [![downloads](http://img.shields.io/nuget/dt/Kevsoft.MigrateMongo.Tool.svg?style=flat-square)](https://www.nuget.org/packages/Kevsoft.MigrateMongo.Tool)

Database migrations for [MongoDB](https://www.mongodb.com/) in .NET. Write migrations in C# with the official
[MongoDB C# driver](https://github.com/mongodb/mongo-csharp-driver), then apply and roll them back from the command
line, your build server or your application.

**Using Docker?** The [Docker image usage guide](docker/README.md) covers the SDK and bundle images, `docker run`
examples and MongoDB networking.

**📖 [Read the documentation](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki)** for guides to every
scenario and a full reference.

- **Your SDK, your runtime, your driver.** Migrations live in a small console project you own. The `migrate-mongo`
  tool builds and runs it with whatever .NET (10 or later) and `MongoDB.Driver` (3.9 or later) you use, so upgrading
  either side doesn't break the other.
- **Safe to run anywhere.** A distributed lock stops concurrent runs, migrations run in transactions where possible,
  and checksums catch migrations edited after they were applied.
- **An audit trail of what changed.** Migrations can record results and notes, and `context.Track(...)` records each
  operation's filter, update and counts. `migrate-mongo show <id>` lists it all, alongside who ran it and when.
  Statements are stored as sent, so [leave them out](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Recording-Results#keeping-secrets-and-personal-data-out) for commands or filters that hold
  passwords or personal data.
- **Collections kept in line with the code.** A `[ManagedCollection]` class declares a collection's options (capped,
  time series, clustered, collation), `$jsonSchema` validator, indexes and search indexes, and `[ManagedView]` declares
  views; every `up` creates what's missing, changes what it can in place, and reports, hides or drops what isn't declared.
- **Built for CI.** Meaningful exit codes, `--json` output, per-environment config with `${ENV_VAR}` secrets, dry
  runs, SSH tunnels through a bastion, AWS IAM authentication, and self-contained bundles for agents without .NET.
- **Mistakes caught at compile time.** A source generator finds migrations without reflection and reports duplicate
  or malformed ids as build errors.

## Getting started

You need a [.NET SDK](https://dotnet.microsoft.com/download) (10 or later) and MongoDB 5.0 or later. Transactions need a
replica set or sharded cluster. Locally, [MongoDB Atlas Local](https://hub.docker.com/r/mongodb/mongodb-atlas-local)
works well:

```bash
docker run -d -p 27017:27017 mongodb/mongodb-atlas-local:8.0
```

### 1. Install the tool

```bash
dotnet new tool-manifest
dotnet tool install Kevsoft.MigrateMongo.Tool
```

On Windows, Linux and macOS (x64 and arm64) you get a Native AOT build that doesn't need a .NET runtime. To install the
native executable without .NET, use one of the install scripts below.

```bash
curl -fsSL https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/releases/latest/download/install.sh | sh
```

```powershell
irm https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/releases/latest/download/install.ps1 | iex
```

### 2. Create a migrations project

From the root of your repository:

```bash
migrate-mongo init --project src/MyApp.Migrations
```

This creates `migrate-mongo.yaml` and a console project that references `Kevsoft.MigrateMongo.Runner` and
`MongoDB.Driver`. You can also use `dotnet new install Kevsoft.MigrateMongo.Templates` and
`dotnet new migratemongo`.

```yaml
project: src/MyApp.Migrations
mongodb:
  url: ${MONGODB_URL}
  databaseName: myapp
environments:
  prod:
    mongodb:
      url: ${PROD_MONGODB_URL}
```

### 3. Write a migration

```bash
migrate-mongo create AddEmailIndexToUsers
```

```csharp
[Migration("20261014164028", "add-email-index-to-users", Transaction = TransactionMode.Never)]
public sealed class AddEmailIndexToUsers : IMigration
{
    public Task UpAsync(MigrationContext context, CancellationToken cancellationToken) =>
        Users(context).CreateIndexAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("email"),
                new CreateIndexOptions { Unique = true, Name = "email_1" }),
            cancellationToken: cancellationToken);

    public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) =>
        Users(context).DropIndexAsync("email_1", cancellationToken: cancellationToken);

    // Records each operation, so `migrate-mongo show 20261014164028` lists what ran.
    private static TrackedCollection<BsonDocument> Users(MigrationContext context) =>
        context.Track(context.Database.GetCollection<BsonDocument>("users"));
}
```

### 4. Declare a collection's indexes

A migration runs once. A collection definition says what a collection's indexes should be now, and every `up` makes
only the changes needed to get there. Add one to the migrations project:

```csharp
[ManagedCollection("orders")]
public sealed class Orders : ICollectionDefinition<BsonDocument>
{
    public void Define(CollectionBuilder<BsonDocument> collection) =>
        collection.Indexes
            .Add(k => k.Ascending("companyId").Descending("createdAt"))
            .Add(k => k.Ascending("reference"), new() { Unique = true })
            .Add(k => k.Ascending("closedAt"), new() { ExpireAfter = TimeSpan.FromDays(30) })
            .Ignore("adhoc_*");
}
```

`up` creates the indexes that are missing and changes the ones it can in place. Indexes that aren't declared are
reported, or hidden or dropped if you set `indexes.prune`, and `Ignore` leaves the ones you make by hand alone. Existing
indexes are matched by their keys, collation and partial filter whatever they're called, so a database that already has
them is adopted as it is. Options are the driver's own `CreateIndexOptions<TDocument>`, and a typed definition
(`ICollectionDefinition<Order>`) renders its keys through your class maps. See
[Managing Collections](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Managing-Collections) and
[Indexes](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Indexes) for rebuilds, pruning and rolling deploys.

### 5. Run it

```bash
export MONGODB_URL="mongodb://localhost:27017/?directConnection=true"

migrate-mongo status            # every migration and collection, and its state
migrate-mongo up --dry-run      # what would be applied and changed, without changing anything
migrate-mongo up                # apply pending migrations, then bring collections in line
migrate-mongo down              # roll back the last migration
migrate-mongo up --env prod     # use the prod environment
```

In CI, `migrate-mongo validate` fails if an applied migration was edited, and `migrate-mongo bundle -r linux-x64`
builds a single executable to run on deploy agents without .NET. To apply migrations and collection definitions when
your app starts, use `Kevsoft.MigrateMongo.Hosting`:

```csharp
builder.Services
    .AddMongoMigrations(o =>
    {
        o.ConnectionString = builder.Configuration.GetConnectionString("Mongo");
        o.DatabaseName = "myapp";
    })
    .FromAssemblyContaining<AddEmailIndexToUsers>()
    .RunOnStartup();
```

Index builds and blocked index changes delay or stop startup; see
[Running from Your Application](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Running-from-Your-Application).

## Documentation

The [wiki](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki) covers everything in detail:

- **Get started:** [Installation](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Installation),
  [Getting Started](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Getting-Started) (a tutorial) and
  [Project Setup](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Project-Setup)
- **Migrations:** [Writing Migrations](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Writing-Migrations),
  [Transactions](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Transactions),
  [Recording Results](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Recording-Results),
  [Dependency Injection](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Dependency-Injection),
  [Custom Templates](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Custom-Templates) and
  [Adopting an Existing Database](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Adopting-an-Existing-Database)
- **Collections:** [Managing Collections](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Managing-Collections),
  [Indexes](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Indexes),
  [Search Indexes](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Search-Indexes),
  [Collection Options](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Collection-Options),
  [Validators](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Validators),
  [Views](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Views) and
  [Undeclared Collections](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Undeclared-Collections)
- **Deploying:** [Environments and Secrets](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Environments-and-Secrets),
  [Running in CI](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Running-in-CI),
  [Bundles](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Bundles),
  [Docker](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Docker),
  [Running from Your Application](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Running-from-Your-Application) and
  [Troubleshooting](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Troubleshooting)
- **Connecting:** [SSH Tunnels](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/SSH-Tunnels) and
  [AWS IAM Authentication](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/AWS-IAM-Authentication)
- **Reference:** [Commands](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Commands),
  [Configuration](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Configuration),
  [Exit Codes](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Exit-Codes),
  [Build Errors](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Build-Errors),
  [What Gets Stored](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/What-Gets-Stored) and
  [Packages](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Packages)
- **Concepts:** [How It Works](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/How-It-Works),
  [Migrations vs Collection Definitions](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Migrations-vs-Collection-Definitions) and
  [Safety](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Safety)

The docs live in the [`wiki/`](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/tree/main/wiki) folder and are
published to the wiki on every push to `main`, so change them there with a pull request.

The [tests/fixtures](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/tree/main/tests/fixtures) folder has small
working migrations projects, including one that runs migrations from an app with Hosting.

## Contributing

1. Issue
1. Fork
1. Hack!
1. Pull Request

You'll need the .NET 11 SDK ([global.json](global.json) pins the one used to build), the .NET 10 runtime (for tests), and
[Docker](https://www.docker.com/). The integration tests pack the repo into a local NuGet feed, install the tool from
it, and start MongoDB Atlas Local with [Testcontainers](https://dotnet.testcontainers.org/):

```bash
dotnet build
dotnet test --project tests/Kevsoft.MigrateMongo.Tests               # unit tests
dotnet test --project tests/Kevsoft.MigrateMongo.IntegrationTests    # tool tests against MongoDB in Docker
```

To use your own MongoDB instead of a container, set `MIGRATE_MONGO_TEST_MONGODB_URL` (a replica set, so
transactions work). To test packages you've already packed, set `MIGRATE_MONGO_PACKAGE_VERSION` and
`MIGRATE_MONGO_PACKAGE_FEED`. [PLAN.md](PLAN.md) describes the design.
