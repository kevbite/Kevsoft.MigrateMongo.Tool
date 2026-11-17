Which NuGet packages to reference, and where.

| You want to... | Reference |
|---|---|
| Run `migrate-mongo` | `Kevsoft.MigrateMongo.Tool` (a .NET tool) |
| Have a migrations project the tool builds and runs | `Kevsoft.MigrateMongo.Runner` and `MongoDB.Driver` |
| Keep migrations in a class library | `Kevsoft.MigrateMongo.Abstractions` (and `MongoDB.Driver`) |
| Run migrations from your app | `Kevsoft.MigrateMongo.Hosting` |
| Connect through an SSH bastion without the tool | `Kevsoft.MigrateMongo.Tunnel` |
| Authenticate with AWS IAM | `Kevsoft.MigrateMongo.Aws` |
| Use `dotnet new` templates | `Kevsoft.MigrateMongo.Templates` (`dotnet new install`) |

## All packages

| Package | Purpose |
|---|---|
| `Kevsoft.MigrateMongo.Tool` | The `migrate-mongo` CLI. |
| `Kevsoft.MigrateMongo.Runner` | Entry point for your migrations project (`MigrateMongoRunner.RunAsync`). Brings in Abstractions, the engine, MongoDriver, Contracts and Cli. |
| `Kevsoft.MigrateMongo.Abstractions` | `IMigration`, `[Migration]`, `MigrationContext`, collection and view definitions, and the source generator. Reference it directly to keep migrations in a class library. |
| `Kevsoft.MigrateMongo` | The engine: planning, locking, state and checksums. |
| `Kevsoft.MigrateMongo.MongoDriver` | The engine's MongoDB driver adapter. |
| `Kevsoft.MigrateMongo.Scripting` | Runs [JavaScript migrations](../guides/JavaScript-Migrations.md) and collection definitions (Jint). Brought in by Runner and the tool. |
| `Kevsoft.MigrateMongo.Contracts` | The runner protocol, resolved configuration and tunnel interfaces. No dependencies. |
| `Kevsoft.MigrateMongo.Cli` | The command line, `migrate-mongo.yaml` loading and console output used by the tool and Runner (System.CommandLine, YamlDotNet). |
| `Kevsoft.MigrateMongo.Hosting` | [Run migrations from your application](../guides/Running-from-Your-Application.md). Brings in the same packages as Runner except Cli. |
| `Kevsoft.MigrateMongo.Tunnel` | Opt-in: lets the runner, bundles and Hosting open [SSH tunnels](../guides/SSH-Tunnels.md) themselves. |
| `Kevsoft.MigrateMongo.Aws` | Opt-in: [AWS IAM authentication](../guides/AWS-IAM-Authentication.md). |
| `Kevsoft.MigrateMongo.Templates` | The `dotnet new` templates. See [Project Setup](../getting-started/Project-Setup.md#with-dotnet-new). |

## Versions

- Keep the `Kevsoft.MigrateMongo.*` packages in a project on the same version.
- `MongoDB.Driver` is yours to choose, from 3.9 upwards. The libraries don't set an upper bound.
- The tool talks to the runner over a versioned protocol, so the tool and the libraries don't have to be on the same
  version. If the tool is too old for the runner, it fails with exit code `5` and asks you to update it. See
  [How It Works](../concepts/How-It-Works.md).
