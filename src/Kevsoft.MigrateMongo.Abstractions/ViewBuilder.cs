using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Kevsoft.MigrateMongo;

/// <summary>Collects a <see cref="IViewDefinition{TDocument}"/>'s declarations: the pipeline and default collation.</summary>
public sealed class ViewBuilder<TDocument>
{
    internal ViewBuilder(string name, string on)
    {
        Name = name;
        On = on;
    }

    /// <summary>The view's name.</summary>
    public string Name { get; }

    /// <summary>The collection or view it reads from.</summary>
    public string On { get; }

    internal Func<RenderArgs<TDocument>, IReadOnlyList<BsonDocument>>? RenderPipeline { get; private set; }

    internal Collation? DeclaredCollation { get; private set; }

    /// <summary>
    /// The pipeline, built from an empty one over <typeparamref name="TDocument"/>, e.g.
    /// <c>p =&gt; p.Match(c =&gt; c.Active).Project(c =&gt; new { c.Email })</c>. It's rendered through the class maps.
    /// <c>$out</c> and <c>$merge</c> aren't allowed in a view.
    /// </summary>
    public ViewBuilder<TDocument> Pipeline<TOutput>(Func<PipelineDefinition<TDocument, TDocument>, PipelineDefinition<TDocument, TOutput>> pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return Pipeline(pipeline(new EmptyPipelineDefinition<TDocument>()));
    }

    /// <inheritdoc cref="Pipeline{TOutput}(Func{PipelineDefinition{TDocument, TDocument}, PipelineDefinition{TDocument, TOutput}})"/>
    public ViewBuilder<TDocument> Pipeline<TOutput>(PipelineDefinition<TDocument, TOutput> pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        RenderPipeline = args => pipeline.Render(args).Documents.ToList();
        return this;
    }

    /// <summary>
    /// The pipeline as a JSON array of stages, e.g. <c>[ { "$match": { "status": "active" } } ]</c>. Field names are written
    /// as stored. <c>$out</c> and <c>$merge</c> aren't allowed in a view.
    /// </summary>
    /// <exception cref="FormatException">The JSON can't be parsed as an array of stages.</exception>
    public ViewBuilder<TDocument> Pipeline(string pipeline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeline);
        BsonArray parsed;
        try
        {
            parsed = BsonSerializer.Deserialize<BsonArray>(pipeline);
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or BsonSerializationException)
        {
            throw new FormatException($"The pipeline of view '{Name}' isn't a valid JSON array: {e.Message}", e);
        }

        if (parsed.Any(s => s is not BsonDocument))
        {
            throw new FormatException($"The pipeline of view '{Name}' must be an array of stage documents.");
        }

        return Pipeline(parsed.Cast<BsonDocument>());
    }

    /// <summary>The pipeline as its stage documents. <c>$out</c> and <c>$merge</c> aren't allowed in a view.</summary>
    public ViewBuilder<TDocument> Pipeline(IEnumerable<BsonDocument> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        var copy = stages.Select(s => (BsonDocument)s.DeepClone()).ToList();
        RenderPipeline = _ => copy.Select(s => (BsonDocument)s.DeepClone()).ToList();
        return this;
    }

    /// <summary>
    /// The view's default collation. MongoDB can't change it in place, so a different one drops the view and creates it
    /// again, which is quick since a view holds no data.
    /// </summary>
    public ViewBuilder<TDocument> Collation(Collation collation)
    {
        ArgumentNullException.ThrowIfNull(collation);
        DeclaredCollation = collation;
        return this;
    }
}
