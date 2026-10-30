namespace Kevsoft.MigrateMongo;

/// <summary>
/// Marks a class as the definition of a collection whose indexes <c>up</c> keeps in line with the code. The class must
/// implement <see cref="ICollectionDefinition{TDocument}"/>.
/// </summary>
/// <param name="name">The collection's name, e.g. <c>"cases"</c>.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ManagedCollectionAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>
/// Declares what a collection should look like. Unlike a migration, it always describes the current state: every
/// <c>up</c> compares it with the database and makes only the changes needed.
/// </summary>
/// <typeparam name="TDocument">
/// The document type. Typed index keys (<c>k =&gt; k.Ascending(c =&gt; c.Reference)</c>) are rendered with the class maps and
/// conventions registered in the process, so register them before the runner starts.
/// </typeparam>
public interface ICollectionDefinition<TDocument>
{
    void Define(CollectionBuilder<TDocument> collection);
}
