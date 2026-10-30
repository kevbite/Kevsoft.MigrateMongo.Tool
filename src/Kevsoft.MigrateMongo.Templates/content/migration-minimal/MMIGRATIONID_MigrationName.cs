using Kevsoft.MigrateMongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Company.Migrations;

[Migration("MIGRATIONID", "migration-name")]
public sealed class MigrationName : IMigration
{
    public Task UpAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task DownAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}
