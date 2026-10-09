namespace Kevsoft.MigrateMongo;

/// <summary>
/// Marks a class as a migration.
/// </summary>
/// <param name="id">Sortable 14-digit timestamp id (<c>yyyyMMddHHmmss</c>), e.g. <c>"20261009120000"</c>.</param>
/// <param name="name">Human readable, kebab-case name, e.g. <c>"add-email-index"</c>.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class MigrationAttribute(string id, string name) : Attribute
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    /// <summary>
    /// Whether this migration runs inside a transaction. <see cref="TransactionMode.Auto"/> defers to the
    /// project-level <c>transaction</c> setting.
    /// </summary>
    public TransactionMode Transaction { get; set; } = TransactionMode.Auto;
}
