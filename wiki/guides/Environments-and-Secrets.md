One `migrate-mongo.yaml` describes every environment (local, staging, production), and secrets come from environment
variables, so the file can be committed.

## Environments

Top-level settings are the defaults. Each entry under `environments` is merged over them, so it only needs what's
different:

```yaml
project: src/MyApp.Migrations
mongodb:
  url: ${MONGODB_URL:-mongodb://localhost:27017/?directConnection=true}
  databaseName: myapp
indexes:
  onRebuild: allow

environments:
  staging:
    mongodb:
      url: ${STAGING_MONGODB_URL}
  prod:
    mongodb:
      url: ${PROD_MONGODB_URL}
    indexes:
      onRebuild: fail        # never drop an index in production without --allow-rebuild
    tunnel:
      host: bastion.example.com
      user: deploy
      privateKey: ${BASTION_SSH_KEY}
      hostKeyFingerprint: SHA256:x9Yb...
```

Select one with `--env` (or `-e`):

```
migrate-mongo status                 # top-level settings, e.g. a local database
migrate-mongo plan --env staging
migrate-mongo up --env prod
```

- Any setting can be overridden per environment. An empty environment changes nothing.
- Unknown settings are errors in every environment, not just the selected one, so a typo in `prod` is caught when you
  run against `staging`.
- The environment's name is recorded with each run, and is available to migrations as `context.Run.Environment`.

## Secrets

Any value can reference an environment variable with `${NAME}`, or `${NAME:-default}` for a fallback. A variable that
isn't set, or is empty, is an error unless it has a default, so a missing secret fails fast instead of connecting
somewhere unexpected.

Keep secrets out of the file:

- **Connection strings** (they usually contain a password): `mongodb.url: ${PROD_MONGODB_URL}`.
- **SSH keys and passwords** for [tunnels](./SSH-Tunnels.md): `privateKey: ${BASTION_SSH_KEY}`.
- Or avoid passwords altogether with [AWS IAM authentication](./AWS-IAM-Authentication.md).

`migrate-mongo` passes its environment to your migrations project (and [bundles](./Bundles.md) read it directly), so
set the variables wherever you run the command.

### In GitHub Actions

Use [deployment environments](https://docs.github.com/actions/deployment/targeting-different-environments) to scope
secrets and add approvals:

```yaml
jobs:
  migrate:
    runs-on: ubuntu-latest
    environment: production
    env:
      PROD_MONGODB_URL: ${{ secrets.PROD_MONGODB_URL }}
      BASTION_SSH_KEY: ${{ secrets.BASTION_SSH_KEY }}
    steps:
      # ...
      - run: dotnet tool run migrate-mongo up --env prod
```

Every command that connects needs the variables, `validate` included. See [Running in CI](./Running-in-CI.md).

### Locally

Export them in your shell, or keep the top-level settings pointing at a local database (as above) so plain
`migrate-mongo up` needs no secrets at all.

## Database users

The tool needs a user that can read and write the database being migrated, including the
[state collections](../reference/What-Gets-Stored.md). Collection definitions also need to create, modify and drop
collections and indexes. A read-only user is enough for `validate`, e.g. as a pull request gate.

## See also

- [Configuration](../reference/Configuration.md)
- [Running in CI](./Running-in-CI.md)
