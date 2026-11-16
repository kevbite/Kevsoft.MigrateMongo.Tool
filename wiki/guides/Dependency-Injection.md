Migrations and collection definitions can take constructor dependencies, e.g. a clock, your own repository classes, or
configuration. The source generator writes the factory, so no reflection is used.

## Registering services

In the migrations project's `Program.cs`:

```csharp
using Kevsoft.MigrateMongo;
using Microsoft.Extensions.DependencyInjection;

return await MigrateMongoRunner.RunAsync(args, services =>
{
    services.AddSingleton<IClock, SystemClock>();
});
```

## Using them

```csharp
[Migration("20261101090000", "stamp-created-at")]
public sealed class StampCreatedAt(IClock clock) : IMigration
{
    public Task UpAsync(MigrationContext context, CancellationToken cancellationToken) =>
        context.Database.GetCollection<BsonDocument>("orders").UpdateManyAsync(
            context.Session,
            Builders<BsonDocument>.Filter.Exists("createdAt", false),
            Builders<BsonDocument>.Update.Set("createdAt", clock.UtcNow),
            cancellationToken: cancellationToken);

    public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}
```

`context.Services` is the run's `IServiceProvider`, for anything you'd rather resolve inside a method.

## `MigrateMongoRunnerOptions`

For more than services, pass options:

```csharp
return await MigrateMongoRunner.RunAsync(args, new MigrateMongoRunnerOptions
{
    ConfigureServices = services => services.AddSingleton<IClock, SystemClock>(),
    Assemblies = { typeof(MyApp.Data.Migrations.Marker).Assembly },
});
```

| Option | Meaning |
|---|---|
| `ConfigureServices` | Registers services, as above. |
| `Assemblies` | Extra assemblies to find migrations and collection definitions in, e.g. a class library that references `Kevsoft.MigrateMongo.Abstractions`. See [Project Setup](../getting-started/Project-Setup.md#keeping-migrations-in-a-class-library). |
| `TunnelProvider` | Opens SSH tunnels without the tool. See [SSH Tunnels](./SSH-Tunnels.md). |

When you [run from your application](./Running-from-Your-Application.md), migrations are resolved from your app's
service provider instead, in a new scope for each run, so they can depend on scoped services.

## See also

- [Writing Migrations](./Writing-Migrations.md)
