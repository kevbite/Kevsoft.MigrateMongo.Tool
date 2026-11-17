Migrations and collection definitions can be written in JavaScript instead of C#. They run inside the
`migrate-mongo` tool itself, so a JavaScript-only project needs no .NET SDK, no `.csproj` and no build. The installed
tool, the release binaries and both [Docker images](./Docker.md) (including `-bundle`) run them.

They use the same engine as C# migrations: the same lock, state, history, checksums, transactions, recorded results
and collection reconciliation. Every command works with them, and C# and JavaScript migrations can live in one
project.

## Getting started

```
$ migrate-mongo init --js
Created migrate-mongo.yaml
Created types/migrate-mongo.d.ts
Created jsconfig.json
Created package.json
Next: set MONGODB_URL, then run `migrate-mongo create <name>` and `migrate-mongo status`. For type checking, run `npm install` then `npm run typecheck`.

$ migrate-mongo create AddEmailIndexToUsers
Created migrations/20261014164028-add-email-index-to-users.js
```

`init --js` sets up the folder the config file is in (or the one given with `--project`):

```
migrate-mongo.yaml        scripts: .
migrations/               one file per migration
collections/              collection and view definitions
types/migrate-mongo.d.ts  editor types for the tool's API
jsconfig.json             type checks the scripts with TypeScript
package.json              npm run typecheck
```

The config's `scripts` setting points at the folder holding `migrations/` and `collections/`, relative to the config
file:

```yaml
scripts: .
mongodb:
  url: ${MONGODB_URL}
  databaseName: myapp
```

`jsconfig.json` and `package.json` are only written if they don't exist. `--auth aws` isn't available with `--js`.

## Writing a migration

A migration's file name is its id then its name: `migrations/20261014164028-add-email-index-to-users.js` has id
`20261014164028` and name `add-email-index-to-users`. `create` makes one with the current UTC time. Migrations run in
id order, like C# ones.

```js
// @ts-check
import { defineMigration } from '@kevsoft/migrate-mongo';

export default defineMigration({
  async up(db, context) {
    const result = await db.collection('users').updateMany(
      { email: { $exists: false } },
      { $set: { email: null } },
    );
    context.results.set('backfilled', result.modifiedCount);
  },

  async down(db, context) {
    await db.collection('users').updateMany({ email: null }, { $unset: { email: '' } });
  },
});
```

`defineMigration` returns what it's given; it's there so editors know the types of `db` and `context`. These forms all
work too:

```js
// Named exports.
export const transaction = 'never';
export async function up(db) { await db.createCollection('users'); }
export async function down(db) { await db.dropCollection('users'); }
```

```js
// CommonJS, as written for the Node migrate-mongo tool.
module.exports = {
  async up(db) { await db.collection('roles').insertOne({ _id: 'admin' }); },
  async down(db) { await db.collection('roles').deleteOne({ _id: 'admin' }); },
};
```

| Export | Meaning |
|---|---|
| `up(db, context)` | Required. Applies the migration. |
| `down(db, context)` | Optional. Rolls it back; without one, `down` refuses to roll the migration back. |
| `transaction` | `'auto'` (default), `'always'` or `'never'`, like a C# migration's `Transaction`. See [Transactions](./Transactions.md). |

Helpers can live in any folder under the scripts folder except `migrations/` and `collections/`, and are imported with
relative paths (`import { normalise } from '../lib/emails.js'`). Imports can't reach outside the scripts folder, and
there's no `node_modules` or Node.js API: scripts only see the `@kevsoft/migrate-mongo` module and standard
JavaScript. CommonJS files can only `require('@kevsoft/migrate-mongo')`; use an ES module to share code.

## The db object

`db` follows the Node.js MongoDB driver, so most migrate-mongo code ports unchanged. Calls run in the migration's
transaction and session automatically.

| | |
|---|---|
| `db.collection(name, options?)` | `insertOne`, `insertMany`, `updateOne`, `updateMany`, `replaceOne`, `deleteOne`, `deleteMany`, `bulkWrite`, `find`, `findOne`, `aggregate`, `countDocuments`, `estimatedDocumentCount`, `distinct`, `createIndex`, `dropIndex`, `dropIndexes`, `listIndexes`, `indexExists`, `drop`, `rename`. |
| `db.command(command, options?)` | Runs a database command. |
| `db.createCollection(name, options?)` | |
| `db.dropCollection(name)` / `db.renameCollection(from, to)` | |
| `db.listCollections(filter?)` | |

`find`, `aggregate`, `listIndexes` and `listCollections` return cursors: `for await (const doc of cursor)`,
`toArray()`, `next()`, `hasNext()`, `forEach()`, and `sort`/`skip`/`limit`/`project` on `find`.

Every write is recorded in the migration's history and shown by [`show`](../reference/commands/Command-show.md), like
C#'s tracked collections. Pass `{ label: 'normalise' }` to `db.collection` to name the operations, or
`{ captureStatement: false }` to leave filters and updates out of the history (e.g. when they hold personal data).

Documents are plain objects with field names as they're stored. BSON types come from the module:

```js
import { ObjectId, Decimal128, Long, UUID, Timestamp } from '@kevsoft/migrate-mongo';

await db.collection('prices').insertOne({ _id: new ObjectId(), amount: Decimal128('9.99'), at: new Date() });
```

`Date`s are stored as dates. Values read back come as `ObjectId`, `Decimal128`, `UUID` and `Timestamp` objects, numbers
and `Date`s.

## The context object

| JavaScript | C# equivalent | |
|---|---|---|
| `context.results.set(key, value)` / `get` / `increment` / `remove` | `context.Results` | Named results, shown by `status` and `show`. See [Recording Results](./Recording-Results.md). |
| `context.results.note(message, 'info' \| 'warning')` | `context.Results.Note` | |
| `context.logger` / `console` | `context.Logger` | `log`, `info`, `debug`, `warn`, `error`. Output appears under the migration. |
| `context.run` | `context.Run` | `id`, `batch`, `direction`, `transactional`, `environment`. |
| `context.migration` | `context.Migration` | `id`, `name`. |

Cancelling a run (`Ctrl+C`) stops the script, like a C# migration's `CancellationToken`.

## Collection and view definitions

Files in `collections/` declare how collections and views should be, like C#
[collection definitions](./Managing-Collections.md). Export one or more definitions (named exports, a default export
or arrays of them):

```js
// @ts-check
import { collection, view } from '@kevsoft/migrate-mongo';

export const orders = collection('orders', c => {
  c.indexes
    .add({ customerId: 1, createdAt: -1 })
    .add({ reference: 1 }, { unique: true, name: 'reference_unique' })
    .add({ expiresAt: 1 }, { expireAfter: '30d' });
  c.indexes.prune = 'drop';
  c.validator.jsonSchema({ bsonType: 'object', required: ['customerId', 'reference'] }).level('moderate');
});

export const openOrders = view('open_orders', { on: 'orders' }, v =>
  v.pipeline([{ $match: { status: 'open' } }, { $project: { reference: 1, customerId: 1 } }]));
```

The builders mirror C#'s, with field names as they're stored:

| | |
|---|---|
| `c.indexes` | `add(keys, options?)`, `ignore(pattern)`, `prune`, `onRebuild`. Options include `unique`, `sparse`, `hidden`, `expireAfter`, `partialFilterExpression`, `collation`, text and geo options. See [Indexes](./Indexes.md). |
| `c.searchIndexes` | `add(name, definition)`, `addVector(name, definition)`, `ignore`, `prune`. See [Search Indexes](./Search-Indexes.md). |
| `c.options` | `capped`, `timeSeries`, `expireAfter`, `clustered`, `collation`, `changeStreamPreAndPostImages`, `onUnchangeable`, `onConvertToCapped`. See [Collection Options](./Collection-Options.md). |
| `c.validator` | `jsonSchema`, `level`, `action`. See [Validators](./Validators.md). |
| `view(name, { on }, v => ...)` | `pipeline(stages)`, `collation(collation)`. See [Views](./Views.md). |

Durations are seconds or strings such as `'30d'`, `'1d12h'`, `'90m'` or `'2w'`.

## Types and type checking

`types/migrate-mongo.d.ts` describes the whole API, so VS Code and other editors complete `db`, `context` and the
builders, and `// @ts-check` flags mistakes as you type. To check every script, e.g. in CI:

```
npm install
npm run typecheck
```

This runs TypeScript (`tsc -p jsconfig.json`) without emitting anything. Documents, filters and pipelines are typed as
open objects (`Record<string, any>`), the JavaScript equivalent of a `BsonDocument`, so any field name is allowed.
`create` keeps the types file up to date when the tool is upgraded; don't edit it.

## Mixed C# and JavaScript projects

A C# migrations project can also have JavaScript migrations. Add `scripts` next to `project`:

```yaml
project: src/MyApp.Migrations
scripts: scripts
```

The C# and JavaScript migrations form one timeline, ordered by id; an id (or a collection) defined in both is an
error. `create` makes C#
migrations; `create --js` makes a JavaScript one in the scripts folder. The JavaScript files run in your migrations
project's runner (through `Kevsoft.MigrateMongo.Runner`), with your project's driver version.

## Running in Docker

A JavaScript-only project runs in the `-bundle` image, which has no .NET at all. There's nothing to
[bundle](./Bundles.md), so `bundle` isn't needed:

```bash
docker run --rm -v "$PWD:/src" -e PROD_MONGODB_URL ghcr.io/kevbite/migrate-mongo:0.2.0-bundle up --env prod
```

## Checksums

Like C# migrations, a migration's checksum ignores whitespace and comments, so reformatting an applied migration
doesn't trip [`onChecksumMismatch`](../reference/Configuration.md#migrations). Imported helpers aren't part of the
checksum.

## Limitations

- `.ts` files aren't run; write JavaScript and type check it with `jsconfig.json`.
- npm packages and Node.js APIs (`fs`, `process`, `fetch`, timers) aren't available.
- [Running from your application](./Running-from-Your-Application.md) and `bundle` only run C# migrations; a mixed
  project's bundle doesn't include its scripts.
- [AWS IAM authentication](./AWS-IAM-Authentication.md) needs a C# migrations project.

## See also

- [Writing Migrations](./Writing-Migrations.md) for the C# equivalent
- [Configuration](../reference/Configuration.md#project-and-connection)
- [init](../reference/commands/Command-init.md) and [create](../reference/commands/Command-create.md)
