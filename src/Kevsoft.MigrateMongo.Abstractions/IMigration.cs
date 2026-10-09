namespace Kevsoft.MigrateMongo;

/// <summary>
/// A single MongoDB migration. Implementations must be decorated with <see cref="MigrationAttribute"/>.
/// </summary>
public interface IMigration
{
    Task UpAsync(MigrationContext context, CancellationToken cancellationToken);

    Task DownAsync(MigrationContext context, CancellationToken cancellationToken);
}
