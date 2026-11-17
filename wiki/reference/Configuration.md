Everything `migrate-mongo.yaml` can hold. For a walkthrough of environments and secrets, see
[Environments and Secrets](../guides/Environments-and-Secrets.md).

## Finding the file

`migrate-mongo.yaml` (or `migrate-mongo.yml`) is read from the current directory, or from the path passed with
`-f <path>`. A [bundle](../guides/Bundles.md) also looks next to its executable. Paths in the file (`project`,
`scripts`, `migrationTemplate`, `tunnel.privateKeyPath`, `tunnel.knownHostsPath`) are relative to the file itself.

Unknown settings are errors, in every environment and not just the one `--env` selects, so typos don't go unnoticed.

## Environment variables

Any value can reference an environment variable with `${NAME}`. Use this for connection strings, passwords and keys,
so nothing secret is committed:

```yaml
mongodb:
  url: ${MONGODB_URL}
  databaseName: ${MONGODB_DATABASE:-myapp}   # "myapp" when MONGODB_DATABASE isn't set
```

A variable that isn't set, or is empty, is an error unless it has a `:-default`. Commands that don't connect, like
`create`, only read `project`, `scripts` and `migrationTemplate`, so they work without the connection variables.

## A complete example

Every setting, with its default where it has one:

```yaml
project: src/MyApp.Migrations        # the migrations project, relative to this file
scripts: scripts                     # JavaScript migrations and collection definitions, relative to this file
mongodb:
  url: ${MONGODB_URL}
  databaseName: myapp
stateCollection: _migrations         # also uses _migrations.history and _migrations.lock
lockTtl: 300
lockWaitTimeout: 60
onChecksumMismatch: fail
transaction: auto
transactionRetryTimeout: 120
allowOutOfOrder: false
migrationTemplate: templates/migration.cs.template
results:
  captureStatements: true
  maxBytes: 65536
indexes:
  prune: report
  onRebuild: allow
  dropHiddenAfter: 604800
  supersededWindow: 3600
searchIndexes:
  prune: report
  waitForReady: false
  waitTimeout: 300
collections:
  onConvertToCapped: fail
  onUnchangeable: fail
  prune: off
  ignore: ["tmp_*"]
validators:
  prune: report
  onInvalidDocuments: warn
  countInvalidDocuments: true
environments:
  staging:
    mongodb:
      url: ${STAGING_MONGODB_URL}
  prod:
    mongodb:
      url: ${PROD_MONGODB_URL}
    tunnel:
      host: bastion.example.com
      user: deploy
      privateKey: ${BASTION_SSH_KEY}
      hostKeyFingerprint: SHA256:x9Yb...
```

## Project and connection

| Setting | Default | Meaning |
|---|---|---|
| `project` | | The migrations project directory or `.csproj`, relative to the config file. |
| `scripts` | | The folder holding [JavaScript](../guides/JavaScript-Migrations.md) `migrations/` and `collections/`, relative to the config file. Without `project`, the tool runs them itself. |
| `mongodb.url` | | Connection string. Required. Any driver option works here (`tls`, `authMechanism`, `proxyHost`, ...). |
| `mongodb.databaseName` | | Database to migrate. Required. |

## Migrations

| Setting | Default | Meaning |
|---|---|---|
| `stateCollection` | `_migrations` | State collection. History and lock use `<name>.history` and `<name>.lock`. See [What Gets Stored](./What-Gets-Stored.md). |
| `lockTtl` | `300` | Seconds the lock lasts without renewal. It's renewed while a run is in progress. |
| `lockWaitTimeout` | `60` | Seconds to wait for another run's lock before failing with exit code `3`. |
| `onChecksumMismatch` | `fail` | `fail`, `warn` or `ignore` when an applied migration has been edited. |
| `transaction` | `auto` | `auto` (use transactions when the deployment supports them), `always` or `never`. A migration's own `Transaction` setting overrides this. See [Transactions](../guides/Transactions.md). |
| `transactionRetryTimeout` | `120` | Seconds to keep retrying transient transaction errors. `0` disables retries. |
| `allowOutOfOrder` | `false` | Allow pending migrations older than the newest applied one (e.g. from a merged branch). |
| `migrationTemplate` | | A file `create` uses instead of the built-in template. See [Custom Templates](../guides/Custom-Templates.md). |

## results

See [Recording Results](../guides/Recording-Results.md).

| Setting | Default | Meaning |
|---|---|---|
| `results.captureStatements` | `true` | Record each tracked operation's filter, update or index keys. `false` keeps only the kind, counts and timings. |
| `results.maxBytes` | `65536` | The most a migration's named results can take up. A migration that records more fails. |

## indexes

See [Indexes](../guides/Indexes.md).

| Setting | Default | Meaning |
|---|---|---|
| `indexes.prune` | `report` | What happens to an index a collection definition doesn't declare: `report`, `hide` or `drop`. |
| `indexes.onRebuild` | `allow` | `fail` refuses to drop and re-create an index (the collection is without it while it builds). |
| `indexes.dropHiddenAfter` | `604800` | With `prune: hide`, seconds an undeclared index stays hidden before it's dropped (7 days). |
| `indexes.supersededWindow` | `3600` | Seconds after a collection definition is replaced during which a run bringing back the previous one is skipped as an older build. `0` turns this off. See [rolling deploys](../guides/Managing-Collections.md#rolling-deploys). |

## searchIndexes

See [Search Indexes](../guides/Search-Indexes.md).

| Setting | Default | Meaning |
|---|---|---|
| `searchIndexes.prune` | `report` | What happens to an Atlas Search index a collection doesn't declare: `report` or `drop`. |
| `searchIndexes.waitForReady` | `false` | Wait for each created, updated or still-building declared search index to become queryable, and each dropped one to be gone, before the run returns. |
| `searchIndexes.waitTimeout` | `300` | With `waitForReady`, seconds to wait for each search index before failing (5 minutes). |

## collections

See [Collection Options](../guides/Collection-Options.md) and [Undeclared Collections](../guides/Undeclared-Collections.md).

| Setting | Default | Meaning |
|---|---|---|
| `collections.onConvertToCapped` | `fail` | `allow` lets an existing collection declared capped be converted with `convertToCapped`, which locks the database while it copies the data. |
| `collections.onUnchangeable` | `fail` | What happens to a declared option MongoDB can't change on an existing collection: `fail` or `skip` (left as it is, with a warning). |
| `collections.prune` | `off` | What happens to a collection or view in the database that nothing declares: `off`, `report` or `drop`. |
| `collections.ignore` | | Name patterns (`*` matches anything) `collections.prune` leaves alone. |

## validators

See [Validators](../guides/Validators.md).

| Setting | Default | Meaning |
|---|---|---|
| `validators.prune` | `report` | What happens to a validator on the server that a definition doesn't declare: `report` or `remove`. |
| `validators.onInvalidDocuments` | `warn` | What happens when existing documents fail a new or changed validator: `warn` or `fail`. |
| `validators.countInvalidDocuments` | `true` | Count the existing documents that fail a new or changed validator before applying it. |

## tunnel

Connects through an SSH bastion. Usually set per environment. See [SSH Tunnels](../guides/SSH-Tunnels.md).

| Setting | Meaning |
|---|---|
| `tunnel.host` / `tunnel.port` | The SSH server. `host` is required; `port` defaults to `22`. |
| `tunnel.user` | The SSH user. Required. |
| `tunnel.password` | Password authentication (also answers keyboard-interactive prompts). |
| `tunnel.privateKey` | The private key's contents, e.g. from a CI secret. Literal `\n` sequences become new lines. |
| `tunnel.privateKeyPath` | A private key file. `~/` and paths relative to the config file work. |
| `tunnel.passphrase` | The passphrase for an encrypted private key. |
| `tunnel.hostKeyFingerprint` | The server's `SHA256:...` host key fingerprint, as printed by `ssh-keygen -lf`. |
| `tunnel.knownHostsPath` | A `known_hosts` file to check the host key against. Defaults to `~/.ssh/known_hosts`. |
| `tunnel.mode` | `socks` (default) or `local`. |

## environments

Named overrides, selected with `--env <name>`. Each is merged over the top-level settings, so it only needs what's
different; an empty one changes nothing. Any setting above can be overridden per environment.

```yaml
indexes:
  onRebuild: allow
environments:
  prod:
    mongodb:
      url: ${PROD_MONGODB_URL}
    indexes:
      onRebuild: fail      # never drop an index in production without --allow-rebuild
```

## From your application

When you [run migrations from your application](../guides/Running-from-Your-Application.md#options), the same
settings are properties on `MongoMigrationOptions`.
