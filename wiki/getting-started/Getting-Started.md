This walks you from an empty folder to a migration that's been applied, inspected and rolled back, then a collection
whose indexes are kept in line with the code. It takes about ten minutes.

You need:

- a [.NET SDK](https://dotnet.microsoft.com/download), 10 or later;
- [Docker](https://www.docker.com/), to run MongoDB locally (or any MongoDB 5.0+ you can connect to).

## 1. Start MongoDB

[MongoDB Atlas Local](https://hub.docker.com/r/mongodb/mongodb-atlas-local) is a single-node replica set, so
transactions work, and it also runs Atlas Search:

```bash
docker run -d --name mongo -p 27017:27017 mongodb/mongodb-atlas-local:8.0
```

## 2. Install the tool

From the root of your repository:

```bash
dotnet new tool-manifest
dotnet tool install Kevsoft.MigrateMongo.Tool
```

See [Installation](./Installation.md) for other ways to install it. A local tool runs as `dotnet migrate-mongo`; this
page writes `migrate-mongo` for short.

## 3. Create a migrations project

Migrations live in a small console project that you own:

```
$ migrate-mongo init --project src/MyApp.Migrations
Created migrate-mongo.yaml
Created src/MyApp.Migrations/MyApp.Migrations.csproj
Created src/MyApp.Migrations/Program.cs
Created src/MyApp.Migrations/.editorconfig
Next: set MONGODB_URL, then run `migrate-mongo create <Name>` and `migrate-mongo status`.
```

`migrate-mongo.yaml` says where the project is and which database to use. The connection string comes from an
environment variable, so no secrets go in the file:

```yaml
project: src/MyApp.Migrations
mongodb:
  url: ${MONGODB_URL}
  databaseName: myapp
```

Set the variable for your shell:

```bash
export MONGODB_URL="mongodb://localhost:27017/?directConnection=true"
```

```powershell
$env:MONGODB_URL = "mongodb://localhost:27017/?directConnection=true"
```

## 4. Write a migration

```
$ migrate-mongo create AddEmailIndexToUsers
Created src/MyApp.Migrations/Migrations/M20261014164028_AddEmailIndexToUsers.cs
```

The id (`20261014164028`) is the current UTC time; migrations run in id order. Replace the generated methods with:

```csharp
using Kevsoft.MigrateMongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyApp.Migrations.Migrations;

[Migration("20261014164028", "add-email-index-to-users", Transaction = TransactionMode.Never)]
public sealed class AddEmailIndexToUsers : IMigration
{
    public Task UpAsync(MigrationContext context, CancellationToken cancellationToken) =>
        Users(context).CreateIndexAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("email"),
                new CreateIndexOptions { Unique = true, Name = "email_1" }),
            cancellationToken: cancellationToken);

    public Task DownAsync(MigrationContext context, CancellationToken cancellationToken) =>
        Users(context).DropIndexAsync("email_1", cancellationToken: cancellationToken);

    // Records each operation, so `migrate-mongo show 20261014164028` lists what ran.
    private static TrackedCollection<BsonDocument> Users(MigrationContext context) =>
        context.Track(context.Database.GetCollection<BsonDocument>("users"));
}
```

`UpAsync` makes the change and `DownAsync` undoes it. `Transaction = TransactionMode.Never` is there because building
an index on an existing collection can't run in a transaction; see [Transactions](../guides/Transactions.md).

## 5. See what would happen

```
$ migrate-mongo status
Built src/MyApp.Migrations/MyApp.Migrations.csproj (3.6s)
Runner: Kevsoft.MigrateMongo 0.2.0 · MongoDB.Driver 3.12.0 · .NET 10.0.12 · protocol 1
 ID              Name                      State    Batch  Applied
 20261014164028  add-email-index-to-users  pending
1 pending migration(s).
```

The tool built your project with your SDK and ran it. `migrate-mongo up --dry-run` shows what `up` would do without
changing anything.

## 6. Apply it

```
$ migrate-mongo up
Acquired lock (run ba6b5b60f25e4861a4ba25597d63fd8d).
▶ 20261014164028 add-email-index-to-users
✔ 20261014164028 add-email-index-to-users   (0.13s)
Applied 1 migration(s) in batch 1.
```

Run it again and there's nothing to do: the migration is recorded as applied in the `_migrations` collection.

## 7. Inspect it

```bash
migrate-mongo show 20261014164028    # its state, who applied it, and the createIndex it ran
migrate-mongo history                # the audit trail of every run
```

## 8. Roll it back

```
$ migrate-mongo down
Acquired lock (run ff6217413f1343948fb456e1ff2e435a).
◀ 20261014164028 add-email-index-to-users
✔ 20261014164028 add-email-index-to-users   (0.04s)
Rolled back 1 migration(s).
```

Run `migrate-mongo up` again to put it back.

## 9. Declare a collection

A migration runs once. A **collection definition** says what a collection should look like now, and every `up` makes
only the changes needed to get there. Add this class anywhere in the migrations project:

```csharp
using Kevsoft.MigrateMongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MyApp.Migrations.Collections;

[ManagedCollection("orders")]
public sealed class Orders : ICollectionDefinition<BsonDocument>
{
    public void Define(CollectionBuilder<BsonDocument> collection) =>
        collection.Indexes
            .Add(k => k.Ascending("companyId").Descending("createdAt"))
            .Add(k => k.Ascending("reference"), new() { Unique = true });
}
```

```bash
migrate-mongo plan   # shows the orders collection and its two indexes would be created
migrate-mongo up     # creates them
```

Change the definition (add an index, make one a TTL index) and run `plan` and `up` again: only the difference is
applied. See [Managing Collections](../guides/Managing-Collections.md).

## Where next

- [Writing Migrations](../guides/Writing-Migrations.md) covers everything a migration can do.
- [Environments and Secrets](../guides/Environments-and-Secrets.md) adds staging and production.
- [Running in CI](../guides/Running-in-CI.md) runs it from your pipeline.
- [Running from Your Application](../guides/Running-from-Your-Application.md) applies migrations when your app starts.
