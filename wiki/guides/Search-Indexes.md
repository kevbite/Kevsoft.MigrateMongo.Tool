A [collection definition](./Managing-Collections.md) can also manage a collection's
[Atlas Search](https://www.mongodb.com/docs/atlas/atlas-search/) and
[Vector Search](https://www.mongodb.com/docs/atlas/atlas-vector-search/) indexes, alongside its regular
[indexes](./Indexes.md).

These need MongoDB Atlas, or a local Atlas deployment (`mongodb/mongodb-atlas-local`). A plain server doesn't run the
search process, so `up` stops with an error if a definition declares one there.

## Declaring them

```csharp
[ManagedCollection("products")]
public sealed class Products : ICollectionDefinition<BsonDocument>
{
    public void Define(CollectionBuilder<BsonDocument> collection)
    {
        collection.SearchIndexes
            .Add("by_text", """{ "mappings": { "dynamic": true } }""")
            .AddVector("by_embedding", """
                { "fields": [ { "type": "vector", "path": "embedding", "numDimensions": 1536, "similarity": "cosine" } ] }
                """)
            .Ignore("adhoc_*");
    }
}
```

| Method | Declares |
|---|---|
| `Add(name, definition)` | An Atlas Search index. |
| `AddVector(name, definition)` | A Vector Search index. |
| `Ignore(pattern)` | Leaves matching names alone (`*` matches anything). |

The `definition` is the index's JSON `definition` document, as the Atlas API takes it: a raw JSON string, a
`BsonDocument`, or an object. A search index is identified by its **name**, unique per collection.

## What `up` does

| Change | What `up` does |
|---|---|
| Declared, not on the server | Creates it. |
| Definition changed | Updates it in place with `updateSearchIndex`, so it stays queryable while it rebuilds. |
| `type` changed (search ↔ vectorSearch) | Drops it and creates it again: `updateSearchIndex` can't change the type. |
| On the server, not declared | Depends on `searchIndexes.prune`: `report` (default) leaves it and lists it as `keep`, `drop` removes it. |

A search index needs its collection to exist, so a definition that declares one on a collection that doesn't exist yet
creates the collection first.

## How definitions are compared

The server fills a definition in with defaults: `dynamic: false` and an empty `fields` on a mapping, each field's
default options for its type (e.g. a `string` field's `store`, `norms` and `indexOptions`, a `number` field's
`representation`), and it leaves out ones such as `storedSource: false`. Only those default values are allowed for, so an
unchanged definition stays in sync whether or not it spells them out. Any other difference is a change, e.g. a field or
an `analyzer` removed. The types a field is indexed as can be listed in any order.

## Undeclared and ignored search indexes

- [`searchIndexes.prune`](../reference/Configuration.md#searchindexes) is `report` by default.
  `collection.SearchIndexes.Prune = SearchIndexPrune.Drop` overrides it for one collection.
- A declared search index never changes an ignored one: if the server's has the same name but a different type or
  definition, the collection is blocked until one of them changes. One that's exactly the same is fine, and one that
  isn't on the server yet is created.
- Search indexes are only managed when a definition declares at least one. A collection that declares none never has
  its search indexes listed or pruned, so you can adopt them gradually.

## Waiting for them to build

Building a search index is asynchronous: by default `up` issues the change and returns without waiting. Set
[`searchIndexes.waitForReady: true`](../reference/Configuration.md#searchindexes) to wait for each created or updated
index to become queryable, and each dropped one to be gone, up to `searchIndexes.waitTimeout` seconds, before the run
returns. That's useful in a test or a deploy step that seeds data straight after. It also waits for a declared index
that's already in line but still building, e.g. after an earlier run that didn't wait or timed out.

## Trying it locally

```
docker run -d -p 27017:27017 mongodb/mongodb-atlas-local
```

The [Getting Started](../getting-started/Getting-Started.md) tutorial uses this image, so search indexes work out of the
box.

## See also

- [Indexes](./Indexes.md)
- [Configuration: searchIndexes](../reference/Configuration.md#searchindexes)
