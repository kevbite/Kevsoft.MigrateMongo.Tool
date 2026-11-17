`migrate-mongo` is a command-line tool. Install it as a .NET tool, with an install script, or use the Docker image.

## As a .NET tool (recommended)

Pin the tool in a local tool manifest, so everyone on the team and your CI use the same version:

```bash
dotnet new tool-manifest      # once per repository; creates .config/dotnet-tools.json
dotnet tool install Kevsoft.MigrateMongo.Tool
dotnet tool run migrate-mongo version
```

Commit `.config/dotnet-tools.json`. Anyone who clones the repository then runs `dotnet tool restore` to get the same
version. A local tool isn't on your `PATH`: inside the repository, run it as `dotnet tool run migrate-mongo` or
`dotnet migrate-mongo`. These docs write plain `migrate-mongo` for short.

To install it for your user everywhere instead:

```bash
dotnet tool install -g Kevsoft.MigrateMongo.Tool
```

Installing needs the .NET 10 SDK or later. On Windows, Linux (glibc and musl) and macOS, on x64 and arm64, you get a
**Native AOT** build: it starts instantly and doesn't need a .NET runtime to run. Other platforms get a
framework-dependent build that runs on any .NET runtime from 10 upwards. `migrate-mongo version` shows which you have:

```
migrate-mongo/0.2.0 (linux-x64; aot)
migrate-mongo/0.2.0 (win-x86; .NET 10.0.12)
```

To update: `dotnet tool update Kevsoft.MigrateMongo.Tool` (add `-g` for a global install).

## Without .NET

The install scripts download the standalone Native AOT binary for your platform from
[GitHub Releases](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/releases), check it against `SHA256SUMS`, and
install it.

Linux and macOS install to `~/.local/bin` (`/usr/local/bin` as root):

```bash
curl -fsSL https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/releases/latest/download/install.sh | sh

# a specific version, somewhere else
curl -fsSL https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/releases/latest/download/install.sh | sh -s -- --version 0.2.0 --install-dir ./bin
```

Windows installs to `%LOCALAPPDATA%\Programs\migrate-mongo` and adds it to your user `PATH`:

```powershell
irm https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/releases/latest/download/install.ps1 | iex
```

Both scripts also read these environment variables:

| Variable | Meaning |
|---|---|
| `MIGRATE_MONGO_VERSION` | The version to install (default: the latest release). |
| `MIGRATE_MONGO_INSTALL_DIR` | Where to install it. |
| `MIGRATE_MONGO_NO_PATH=1` | Stop `install.ps1` changing your user `PATH`. |

You still need a .NET SDK to *build* a migrations project. Without one, run a pre-built [bundle](../guides/Bundles.md).

## Docker

`ghcr.io/kevbite/migrate-mongo` has the tool with the .NET SDK (to build and run your project) and a `-bundle` variant
without .NET (to run bundles). See [Docker](../guides/Docker.md).

## Next

- [Getting Started](./Getting-Started.md) walks through your first migration.
- [Project Setup](./Project-Setup.md) covers `init`, the `dotnet new` templates and existing projects.
