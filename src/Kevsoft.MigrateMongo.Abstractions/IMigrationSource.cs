namespace Kevsoft.MigrateMongo;

/// <summary>
/// Loads migrations. C# migrations are provided by a compile-time generated source; other formats
/// (e.g. scripts) can plug in by implementing this interface.
/// </summary>
public interface IMigrationSource
{
    IReadOnlyCollection<MigrationDescriptor> GetMigrations();
}
