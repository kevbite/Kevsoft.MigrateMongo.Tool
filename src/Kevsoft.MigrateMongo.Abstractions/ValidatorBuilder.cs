using MongoDB.Bson;

namespace Kevsoft.MigrateMongo;

/// <summary>Which writes a collection's validator checks (<c>validationLevel</c>).</summary>
public enum ValidationLevel
{
    /// <summary>Every insert and update (MongoDB's default).</summary>
    Strict,

    /// <summary>Inserts, and updates to documents that are already valid.</summary>
    Moderate,

    /// <summary>Every insert and update, and guarantees every document is valid (MongoDB 9.0+).</summary>
    Constraint,

    /// <summary>Nothing.</summary>
    Off,
}

/// <summary>What happens to a write that fails a collection's validator (<c>validationAction</c>).</summary>
public enum ValidationAction
{
    /// <summary>The write is rejected (MongoDB's default).</summary>
    Error,

    /// <summary>The write is allowed and the failure is logged to the <c>mongod</c> log.</summary>
    Warn,

    /// <summary>The write is rejected and logged (MongoDB 8.1+).</summary>
    ErrorAndLog,
}

/// <summary>What happens to a validator that's on the server but not declared.</summary>
public enum ValidatorPrune
{
    /// <summary>Left alone, with a warning.</summary>
    Report,

    /// <summary>Removed (<c>collMod</c> with <c>validator: {}</c>). The previous validator is recorded first.</summary>
    Remove,
}

/// <summary>What happens when existing documents fail a validator that's being added or changed.</summary>
public enum InvalidDocuments
{
    /// <summary>It's applied anyway, with a warning showing how many documents fail it. MongoDB only checks later writes.</summary>
    Warn,

    /// <summary>The run stops before anything changes, and <c>--allow-invalid-documents</c> approves one collection.</summary>
    Fail,
}

/// <summary>
/// The collection's validator: a raw <c>$jsonSchema</c>, with the level and action it's applied with. A validator, level or
/// action that changes is changed in place with <c>collMod</c>.
/// </summary>
public sealed class ValidatorBuilder
{
    internal ValidatorBuilder()
    {
    }

    internal BsonDocument? Schema { get; private set; }

    internal ValidationLevel? DeclaredLevel { get; private set; }

    internal ValidationAction? DeclaredAction { get; private set; }

    /// <summary>
    /// The <c>$jsonSchema</c> documents must match, as JSON: the schema itself, without the <c>$jsonSchema</c> wrapper. Field
    /// names are written as stored; they aren't rendered through class maps. MongoDB's <c>$jsonSchema</c> is draft 4 plus
    /// <c>bsonType</c>, without <c>$ref</c>, <c>$schema</c>, <c>default</c>, <c>definitions</c>, <c>format</c>, <c>id</c> or
    /// <c>type: integer</c>.
    /// </summary>
    /// <exception cref="FormatException">The JSON can't be parsed.</exception>
    public ValidatorBuilder JsonSchema(string schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        BsonDocument parsed;
        try
        {
            parsed = BsonDocument.Parse(schema);
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or BsonSerializationException)
        {
            throw new FormatException($"The validator's $jsonSchema isn't valid JSON: {e.Message}", e);
        }

        return JsonSchema(parsed);
    }

    /// <inheritdoc cref="JsonSchema(string)"/>
    public ValidatorBuilder JsonSchema(BsonDocument schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        Schema = (BsonDocument)schema.DeepClone();
        return this;
    }

    /// <summary>Which writes are checked (<c>validationLevel</c>). Not declaring it leaves the server's setting alone.</summary>
    public ValidatorBuilder Level(ValidationLevel level)
    {
        DeclaredLevel = level;
        return this;
    }

    /// <summary>What happens to a failing write (<c>validationAction</c>). Not declaring it leaves the server's setting alone.</summary>
    public ValidatorBuilder Action(ValidationAction action)
    {
        DeclaredAction = action;
        return this;
    }
}
