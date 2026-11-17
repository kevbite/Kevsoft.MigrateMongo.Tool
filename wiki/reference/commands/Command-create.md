Creates a new migration file. Tool only. It only reads `project`, `scripts` and `migrationTemplate` from the config, so
it works without connection variables set.

```
migrate-mongo create <name> [--minimal] [--js]
```

```
$ migrate-mongo create AddEmailIndexToUsers
Created src/MyApp.Migrations/Migrations/M20261014164028_AddEmailIndexToUsers.cs
```

## Arguments and options

| | Meaning |
|---|---|
| `<name>` | The migration's name in PascalCase, e.g. `AddEmailIndexToUsers`. It becomes the class name, and in kebab-case (`add-email-index-to-users`) the migration's name. |
| `--minimal` | Write bare `UpAsync` and `DownAsync` methods, without the commented results and tracking examples. Ignores `migrationTemplate`. |
| `--js` | Write a JavaScript migration in the `scripts` folder when the config has both `project` and `scripts`. Without a `project`, migrations are always JavaScript. |

## What it writes

The file goes in the project's `Migrations` folder, as `M<id>_<Name>.cs`, in the `<project root namespace>.Migrations`
namespace. The id is the current UTC time (`yyyyMMddHHmmss`); if a migration already has that id, it steps forward a
second.

With [`migrationTemplate`](../Configuration.md#migrations) set, that file is used instead of the built-in template.
See [Custom Templates](../../guides/Custom-Templates.md).

A JavaScript migration goes in `<scripts>/migrations/` as `<id>-<kebab-name>.js`, e.g.
`migrations/20261014164028-add-email-index-to-users.js`. If `types/migrate-mongo.d.ts` exists, it's updated to match
the tool. See [JavaScript Migrations](../../guides/JavaScript-Migrations.md).

## See also

- [Writing Migrations](../../guides/Writing-Migrations.md)
