namespace Kevsoft.MigrateMongo;

/// <summary>
/// A migration discovered by an <see cref="IMigrationSource"/>.
/// </summary>
public sealed class MigrationDescriptor(
    string id,
    string name,
    string source,
    string origin,
    string checksum,
    TransactionMode transaction,
    Func<IServiceProvider, IMigration> factory)
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    /// <summary>Kind of source, e.g. <c>csharp</c>.</summary>
    public string Source { get; } = source;

    /// <summary>Where the migration came from, e.g. a type name or file path.</summary>
    public string Origin { get; } = origin;

    /// <summary>Self-describing checksum, e.g. <c>sha256:…</c>.</summary>
    public string Checksum { get; } = checksum;

    public TransactionMode Transaction { get; } = transaction;

    public IMigration Create(IServiceProvider services) => factory(services);
}
