`ghcr.io/kevbite/migrate-mongo` runs the tool without installing anything but Docker. Images are `linux/amd64` and
`linux/arm64`, tagged with the version (`0.2.0`, `0.2`) and `latest`. Pin a version in real pipelines.

## Which image

| Image | Contains | Use it to |
|---|---|---|
| `ghcr.io/kevbite/migrate-mongo:<version>` | The tool on the .NET 11 SDK. | Build and run your migrations project, like the installed tool. A `global.json` that pins an older SDK won't work in it. |
| `ghcr.io/kevbite/migrate-mongo:<version>-bundle` | The tool without .NET. | Run [bundles](./Bundles.md) with `--runner`, or [JavaScript migrations](./JavaScript-Migrations.md#running-in-docker). It can't build a project, so with a `project` and without `--runner` it stops and asks for one. |

## Running it

The working directory is `/src`, so mount your repository there. Pass connection variables with `-e`.

### With the SDK image

Run it as your user so build output in the mount isn't owned by root:

```bash
docker run --rm -v "$PWD:/src" --user "$(id -u):$(id -g)" -e HOME=/tmp \
  -e PROD_MONGODB_URL ghcr.io/kevbite/migrate-mongo:0.2.0 up --env prod
```

### With a bundle

```bash
docker run --rm -v "$PWD:/src" -e PROD_MONGODB_URL \
  ghcr.io/kevbite/migrate-mongo:0.2.0-bundle up --env prod --runner ./out/MyApp.Migrations
```

The bundle must be built for Linux (`-r linux-x64` or `-r linux-arm64`).

## Connecting to a database on your machine

Inside a container, `localhost` is the container itself. Use `host.docker.internal` (Docker Desktop), or
`--network host` on Linux.

## See also

- [Installation](../getting-started/Installation.md#docker)
- [Bundles](./Bundles.md)
