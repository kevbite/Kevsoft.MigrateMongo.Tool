# Kevsoft.MigrateMongo [![Continuous Integration Workflow](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/actions/workflows/ci.yml/badge.svg)](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/actions/workflows/ci.yml) [![install from nuget](http://img.shields.io/nuget/v/Kevsoft.MigrateMongo.Tool.svg?style=flat-square)](https://www.nuget.org/packages/Kevsoft.MigrateMongo.Tool) [![downloads](http://img.shields.io/nuget/dt/Kevsoft.MigrateMongo.Tool.svg?style=flat-square)](https://www.nuget.org/packages/Kevsoft.MigrateMongo.Tool)

Database migrations for [MongoDB](https://www.mongodb.com/) in .NET. Write migrations in C# with the official
[MongoDB C# driver](https://github.com/mongodb/mongo-csharp-driver), then apply and roll them back from the command
line, your build server or your application.

- **Your SDK, your runtime, your driver.** Migrations live in a small console project you own. The `migrate-mongo`
  tool builds and runs it with whatever .NET (10 or later) and `MongoDB.Driver` (3.5 or later) you use, so upgrading
  either side doesn't break the other.
- **Safe to run anywhere.** A distributed lock stops concurrent runs, migrations run in transactions where possible,
  and checksums catch migrations edited after they were applied.
- **Built for CI.** Meaningful exit codes, `--json` output, per-environment config with `${ENV_VAR}` secrets, dry
  runs, SSH tunnels through a bastion, AWS IAM authentication, and self-contained bundles for agents without .NET.
- **Mistakes caught at compile time.** A source generator finds migrations without reflection and reports duplicate
  or malformed ids as build errors.

## Getting started

You need a [.NET SDK](https://dotnet.microsoft.com/download) (10 or later) and MongoDB 4.2 or later. Transactions need a
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

On Windows, Linux and macOS (x64 and arm64) you get a Native AOT build that doesn't need a .NET runtime. To install without .NET,
use the install script or the Docker image:

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
        context.Database.GetCollection<BsonDocument>("users").Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("email"),
                new CreateIndexOptions { Unique = true, Name = "email_1" }),
            cancellationToken: cancellationToken);

    public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) =>
        context.Database.GetCollection<BsonDocument>("users").Indexes.DropOneAsync("email_1", cancellationToken);
}
```

### 4. Run it

```bash
export MONGODB_URL="mongodb://localhost:27017/?directConnection=true"

migrate-mongo status            # every migration and its state
migrate-mongo up --dry-run      # what would be applied, without changing anything
migrate-mongo up                # apply pending migrations
migrate-mongo down              # roll back the last one
migrate-mongo up --env prod     # use the prod environment
```

In CI, `migrate-mongo validate` fails if an applied migration was edited, and `migrate-mongo bundle -r linux-x64`
builds a single executable to run on deploy agents without .NET. To apply migrations when your app starts, use
`Kevsoft.MigrateMongo.Hosting`:

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

## Documentation

[USAGE.md](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/blob/main/USAGE.md) covers everything in detail:

- [Installing](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/blob/main/USAGE.md#installing) and
  [setting up a project](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/blob/main/USAGE.md#setting-up-a-project)
- [Configuration](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/blob/main/USAGE.md#configuration)
- [Writing migrations](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/blob/main/USAGE.md#writing-migrations):
  transactions, dependency injection and custom templates
- [Commands](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/blob/main/USAGE.md#commands) and
  [exit codes](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/blob/main/USAGE.md#exit-codes)
- [When something goes wrong](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/blob/main/USAGE.md#when-something-goes-wrong)
- [Running in CI](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/blob/main/USAGE.md#running-in-ci),
  [bundles](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/blob/main/USAGE.md#bundles) and
  [Docker](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/blob/main/USAGE.md#docker)
- [Tunnelling through a bastion](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/blob/main/USAGE.md#tunnelling-through-a-bastion)
- [AWS IAM authentication (Atlas)](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/blob/main/USAGE.md#aws-iam-authentication-atlas)
- [Running from your application](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/blob/main/USAGE.md#running-from-your-application)

The [tests/fixtures](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/tree/main/tests/fixtures) folder has small
working migrations projects, including one that runs migrations from an app with Hosting.

## Contributing

1. Issue
1. Fork
1. Hack!
1. Pull Request

You'll need the .NET 10 SDK ([global.json](global.json) pins the one used to build) and
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
