Database migrations for [MongoDB](https://www.mongodb.com/) in .NET. Write migrations in C# with the official
[MongoDB C# driver](https://github.com/mongodb/mongo-csharp-driver), declare what your collections should look like,
and apply both from the command line, your build server or your application.

> [!NOTE]
> These pages are published from the [`wiki/`](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/tree/main/wiki)
> folder on `main`. To fix or improve them, open a pull request there; edits made in the wiki itself are overwritten.

## New here?

1. [Installation](./getting-started/Installation.md): install the `migrate-mongo` tool.
2. [Getting Started](./getting-started/Getting-Started.md): from nothing to an applied, inspected and rolled-back
   migration against a local MongoDB.
3. [Project Setup](./getting-started/Project-Setup.md): `init`, the `dotnet new` templates and using an existing project.

## I want to...

| Write migrations | Keep collections in line | Deploy and connect |
|---|---|---|
| [Write a migration](./guides/Writing-Migrations.md) | [Declare collections](./guides/Managing-Collections.md) | [Use per-environment config and secrets](./guides/Environments-and-Secrets.md) |
| [Use transactions](./guides/Transactions.md) | [Manage indexes](./guides/Indexes.md) | [Run in CI](./guides/Running-in-CI.md) |
| [Record what a migration did](./guides/Recording-Results.md) | [Manage Atlas Search indexes](./guides/Search-Indexes.md) | [Build once, deploy many with bundles](./guides/Bundles.md) |
| [Inject services into migrations](./guides/Dependency-Injection.md) | [Set collection options](./guides/Collection-Options.md) | [Run in Docker](./guides/Docker.md) |
| [Customise the `create` template](./guides/Custom-Templates.md) | [Add a `$jsonSchema` validator](./guides/Validators.md) | [Run from my application](./guides/Running-from-Your-Application.md) |
| [Adopt an existing database](./guides/Adopting-an-Existing-Database.md) | [Declare views](./guides/Views.md) | [Tunnel through an SSH bastion](./guides/SSH-Tunnels.md) |
| [Fix a failed run](./guides/Troubleshooting.md) | [Clean up undeclared collections](./guides/Undeclared-Collections.md) | [Use AWS IAM authentication](./guides/AWS-IAM-Authentication.md) |

## Look something up

- [Configuration](./reference/Configuration.md): every `migrate-mongo.yaml` setting.
- [Commands](./reference/Commands.md): every command and option.
- [Exit Codes](./reference/Exit-Codes.md), [Build Errors](./reference/Build-Errors.md),
  [What Gets Stored](./reference/What-Gets-Stored.md) and [Packages](./reference/Packages.md).

## Understand how it works

- [How It Works](./concepts/How-It-Works.md): the tool, your migrations project and why versions don't clash.
- [Migrations vs Collection Definitions](./concepts/Migrations-vs-Collection-Definitions.md): which to use when.
- [Safety](./concepts/Safety.md): locking, checksums, transactions and cancellation.

## Requirements

- A [.NET SDK](https://dotnet.microsoft.com/download), 10 or later, to build your migrations project. Running the tool
  or a [bundle](./guides/Bundles.md) doesn't need .NET.
- `MongoDB.Driver` 3.9 or later in your migrations project.
- MongoDB 5.0 or later. Transactions need a replica set or sharded cluster.
