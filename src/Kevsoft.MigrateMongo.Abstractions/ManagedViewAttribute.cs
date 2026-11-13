namespace Kevsoft.MigrateMongo;

/// <summary>
/// Marks a class as the definition of a view that <c>up</c> keeps in line with the code. The class must implement
/// <see cref="IViewDefinition{TDocument}"/>.
/// </summary>
/// <param name="name">The view's name, e.g. <c>"active_customers"</c>.</param>
/// <param name="on">The collection or view it reads from (MongoDB's <c>viewOn</c>), e.g. <c>"customers"</c>.</param>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ManagedViewAttribute(string name, string on) : Attribute
{
    public string Name { get; } = name;

    /// <summary>The collection or view the view reads from.</summary>
    public string On { get; } = on;
}

/// <summary>
/// Declares what a view should look like. Every <c>up</c> creates it if it's missing and changes its pipeline, source or
/// collation when they differ. A view is read-only: it has no indexes, options or validator of its own.
/// </summary>
/// <typeparam name="TDocument">
/// The type of the documents in the collection or view it reads from. A typed pipeline is rendered with its class maps.
/// </typeparam>
public interface IViewDefinition<TDocument>
{
    void Define(ViewBuilder<TDocument> view);
}
