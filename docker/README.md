# Run Kevsoft.MigrateMongo in Docker

The `ghcr.io/kevbite/migrate-mongo` image lets you run MongoDB migrations without installing the tool on the host.
It has two variants:

| Image | Includes | Use it when |
|---|---|---|
| `ghcr.io/kevbite/migrate-mongo:<tag>` | .NET 11 SDK and .NET 10 runtime | You want the container to build and run your migrations project. |
| `ghcr.io/kevbite/migrate-mongo:<tag>-bundle` | Runtime dependencies only | You have a prebuilt Linux bundle and don't need an SDK in the image. |

Both variants support `linux/amd64` and `linux/arm64`. Releases publish version tags, such as `1.0.1`, and a moving
`latest` tag (or `latest-bundle`). Pin a version tag in deployment pipelines to control upgrades.

## Run a migrations project

Mount your repository at `/src`. The image starts in that directory, so `migrate-mongo.yaml` and the project path in
its `project` setting should be relative to your repository root. For example:

```yaml
project: src/MyApp.Migrations
mongodb:
  url: ${MONGODB_URL}
  databaseName: myapp
```

Set `MONGODB_URL` in the host environment or your CI secret store, then run:

```bash
docker run --rm \
  --mount "type=bind,source=$PWD,target=/src" \
  --env MONGODB_URL \
  ghcr.io/kevbite/migrate-mongo:latest \
  up --env prod
```

In PowerShell, use:

```powershell
docker run --rm `
  --mount "type=bind,source=$($PWD.Path),target=/src" `
  --env MONGODB_URL `
  ghcr.io/kevbite/migrate-mongo:latest `
  up --env prod
```

The SDK image contains .NET 11 and the .NET 10 runtime. It can build projects targeting supported frameworks, but a
`global.json` that pins an SDK not in the image will prevent the build. If you run on Linux, you can pass
`--user "$(id -u):$(id -g)" --env HOME=/tmp` to avoid root-owned build files in the mounted repository.

## Run a bundle

Build a bundle for the container's architecture on a machine with the tool and a .NET SDK:

```bash
migrate-mongo bundle -r linux-x64 -o out
```

Use `linux-arm64` when deploying on an ARM64 host. Mount the repository and pass the bundle to the `-bundle` image:

```bash
docker run --rm \
  --mount "type=bind,source=$PWD,target=/src" \
  --env MONGODB_URL \
  ghcr.io/kevbite/migrate-mongo:latest-bundle \
  up --env prod --runner ./out/MyApp.Migrations
```

Replace `MyApp.Migrations` with the executable name produced by your project. The config file can be in the working
directory or next to the bundle. This image cannot build a project; `up` needs the `--runner` bundle path.

## Connect to MongoDB

`localhost` inside the container refers to the container, not the host. If MongoDB runs on the host, use
`host.docker.internal` in the connection string on Docker Desktop. On Linux, add
`--add-host host.docker.internal:host-gateway` to `docker run`, or use host networking. If MongoDB runs in Docker
Compose, connect to its service name on the shared network instead.

For the full command reference and migration project setup, see the
[project README](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool#readme) and
[Docker guide](https://github.com/kevbite/Kevsoft.MigrateMongo.Tool/wiki/Docker).
