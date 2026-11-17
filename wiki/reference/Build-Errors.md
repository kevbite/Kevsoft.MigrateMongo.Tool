A source generator in `Kevsoft.MigrateMongo.Abstractions` finds migrations, collection definitions and views at compile
time, without reflection, and reports mistakes as build errors. Through the tool they fail the build with exit code `6`.

| Code | Meaning | Fix |
|---|---|---|
| `MM0001` | Two migrations share the same id. | Give one a new id, e.g. by creating it again with `migrate-mongo create`. |
| `MM0002` | The id isn't a 14-digit `yyyyMMddHHmmss` timestamp. | Use `migrate-mongo create`, which generates one. |
| `MM0003` | A `[Migration]` class doesn't implement `IMigration`. | Implement `IMigration`. |
| `MM0004` | The migration class can't be constructed (abstract, private, or no accessible constructor). | Make it a public, non-abstract class with a public constructor. |
| `MM0005` | The migration name is empty. | Pass a name to `[Migration(id, name)]`. |
| `MM0006` | A `[ManagedCollection]` class doesn't implement `ICollectionDefinition<TDocument>` (exactly once). | Implement it once. |
| `MM0007` | The collection name is empty, contains `$` or starts with `system.`. | Use a valid collection name. |
| `MM0008` | The collection definition can't be constructed. | Make it a public, non-abstract class with a public constructor. |
| `MM0009` | Two classes define the same collection. | Merge them into one definition. |
| `MM0010` | A `[ManagedView]` class doesn't implement `IViewDefinition<TDocument>` (exactly once). | Implement it once. |
| `MM0011` | Views read from each other in a cycle. | Break the cycle; see [Views](../guides/Views.md). |

Constructor parameters are fine: they're resolved from the service provider. See
[Dependency Injection](../guides/Dependency-Injection.md).
