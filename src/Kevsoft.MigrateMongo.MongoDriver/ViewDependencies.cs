using MongoDB.Bson;

namespace Kevsoft.MigrateMongo.MongoDriver;

/// <summary>
/// The collections and views a view reads from, so pruning never drops a namespace a managed view depends on. A view reads
/// from its <c>viewOn</c> source and from every namespace named in its pipeline: <c>$lookup</c>/<c>$graphLookup</c> <c>from</c>,
/// <c>$unionWith</c> (a collection name or <c>coll</c>), and the same inside the nested pipelines of <c>$lookup</c>,
/// <c>$unionWith</c> and <c>$facet</c>.
/// </summary>
internal static class ViewDependencies
{
    /// <summary>The namespaces the view described by a <c>listCollections</c> <c>options</c> document reads from.</summary>
    public static IEnumerable<string> ReferencedBy(BsonDocument viewOptions)
    {
        if (viewOptions.GetValue("viewOn", null) is BsonString { Value.Length: > 0 } on)
        {
            yield return on.Value;
        }

        if (viewOptions.GetValue("pipeline", null) is BsonArray pipeline)
        {
            foreach (var name in Referenced(pipeline))
            {
                yield return name;
            }
        }
    }

    /// <summary>The namespaces an aggregation pipeline reads from, following its nested sub-pipelines.</summary>
    public static IEnumerable<string> Referenced(IEnumerable<BsonValue> pipeline)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stage in pipeline)
        {
            if (stage is BsonDocument document)
            {
                Collect(document, names);
            }
        }

        return names;
    }

    private static void Collect(BsonDocument stage, HashSet<string> names)
    {
        foreach (var element in stage)
        {
            switch (element.Name)
            {
                case "$lookup" when element.Value is BsonDocument lookup:
                    AddName(lookup.GetValue("from", null), names);
                    CollectPipeline(lookup.GetValue("pipeline", null), names);
                    break;
                case "$graphLookup" when element.Value is BsonDocument graphLookup:
                    AddName(graphLookup.GetValue("from", null), names);
                    break;
                case "$unionWith":
                    if (element.Value is BsonDocument union)
                    {
                        AddName(union.GetValue("coll", null), names);
                        CollectPipeline(union.GetValue("pipeline", null), names);
                    }
                    else
                    {
                        AddName(element.Value, names);
                    }

                    break;
                case "$facet" when element.Value is BsonDocument facet:
                    foreach (var branch in facet)
                    {
                        CollectPipeline(branch.Value, names);
                    }

                    break;
            }
        }
    }

    private static void CollectPipeline(BsonValue? pipeline, HashSet<string> names)
    {
        if (pipeline is BsonArray stages)
        {
            foreach (var stage in stages)
            {
                if (stage is BsonDocument document)
                {
                    Collect(document, names);
                }
            }
        }
    }

    private static void AddName(BsonValue? value, HashSet<string> names)
    {
        if (value is BsonString { Value.Length: > 0 } name)
        {
            names.Add(name.Value);
        }
    }
}
