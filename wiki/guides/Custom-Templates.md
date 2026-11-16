To change what [`create`](../reference/commands/Command-create.md) writes (a base class, your team's usings, a header
comment, ...), point [`migrationTemplate`](../reference/Configuration.md#migrations) at your own file.

```yaml
migrationTemplate: templates/migration.cs.template
```

The path is relative to `migrate-mongo.yaml`.

## Placeholders

These placeholders are replaced and everything else is copied as-is:

| Placeholder | Example |
|---|---|
| `{{namespace}}` | `MyApp.Migrations.Migrations` |
| `{{id}}` | `20261014164028` |
| `{{className}}` | `AddEmailIndexToUsers` |
| `{{name}}` | `add-email-index-to-users` |

## Example

```csharp
using Kevsoft.MigrateMongo;
using MongoDB.Driver;

namespace {{namespace}};

[Migration("{{id}}", "{{name}}")]
public sealed class {{className}} : IMigration
{
    public Task UpAsync(MigrationContext context, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) => throw new NotImplementedException();
}
```

## Gotchas

- If the template lives inside the migrations project, don't give it a `.cs` extension, or it'll be compiled.
- `create --minimal` ignores `migrationTemplate` and writes the minimal built-in template.
- `init` and `dotnet new` always use the built-in templates.
