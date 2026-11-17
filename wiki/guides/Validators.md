A [collection definition](./Managing-Collections.md) can declare a
[`$jsonSchema` validator](https://www.mongodb.com/docs/manual/core/schema-validation/), as a JSON string or a
`BsonDocument` (the schema itself, without the `$jsonSchema` wrapper).

## Example

```csharp
[ManagedCollection("customers")]
public sealed class Customers : ICollectionDefinition<BsonDocument>
{
    public void Define(CollectionBuilder<BsonDocument> collection) =>
        collection.Validator.JsonSchema("""
            {
              "bsonType": "object",
              "required": ["email", "tier"],
              "properties": {
                "email": { "bsonType": "string" },
                "tier":  { "enum": ["Standard", "Gold", "Platinum"] }
              }
            }
            """)
            .Level(ValidationLevel.Moderate)
            .Action(ValidationAction.Warn);
}
```

## Level and action

| `ValidationLevel` | Checks |
|---|---|
| `Strict` (MongoDB's default) | Every insert and update. |
| `Moderate` | Inserts, and updates to documents that are already valid. |
| `Constraint` (9.0+) | Every insert and update, and guarantees every document is valid. |
| `Off` | Nothing. |

| `ValidationAction` | On a failing write |
|---|---|
| `Error` (MongoDB's default) | Rejects it. |
| `Warn` | Allows it and logs the failure to the `mongod` log. |
| `ErrorAndLog` (8.1+) | Rejects it and logs it. |

Not declaring `Level` or `Action` leaves the server's setting alone.

## How it's applied

The schema is created with the collection and changed in place with `collMod`.

- Field names are written as stored, not rendered through class maps.
- MongoDB's `$jsonSchema` is draft 4 plus `bsonType`, without `$ref`, `$schema`, `default`, `definitions`, `format`,
  `id` or `type: integer`.

## Existing documents

MongoDB doesn't check existing documents when a validator changes, only later writes. So before adding or changing one,
`plan` and `up` count the documents it would reject and show a few `_id`s.

| [`validators.onInvalidDocuments`](../reference/Configuration.md#validators) | |
|---|---|
| `warn` (default) | It's applied anyway, with a warning. |
| `fail` | The run stops with exit code `4`. `--allow-invalid-documents customers` approves one collection. |

`validators.countInvalidDocuments: false` skips the count on very large collections.

### Rolling out a stricter rule

1. Deploy it with `ValidationAction.Warn`.
2. Fix the data in a [migration](./Writing-Migrations.md).
3. Switch to `ValidationAction.Error`.

## Undeclared validators

A validator on the server that the definition doesn't declare is left alone and reported, unless
`validators.prune: remove`, which removes it and records it first.

## See also

- [Managing Collections](./Managing-Collections.md)
- [Configuration: validators](../reference/Configuration.md#validators)
