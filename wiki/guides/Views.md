A `[ManagedView]` declares a read-only [view](https://www.mongodb.com/docs/manual/core/views/) and the pipeline it runs
over another collection or view. `up` creates and updates it like a
[collection definition](./Managing-Collections.md).

## Example

```csharp
[ManagedView("active_customers", on: "customers")]
public sealed class ActiveCustomers : IViewDefinition<Customer>
{
    public void Define(ViewBuilder<Customer> view) =>
        view.Pipeline(p => p
                .Match(c => c.Active)
                .Project(c => new { c.Email, c.Tier }))
            .Collation(new Collation("en", strength: CollationStrength.Secondary));
}
```

- **`[ManagedView(name, on:)]`** names the view and the collection or view it reads from.
- **`IViewDefinition<TDocument>`** describes it on a `ViewBuilder`. Implementing it more than once is a
  [build error](../reference/Build-Errors.md) (`MM0010`).
- **`Pipeline`** takes the driver's pipeline builder, a `PipelineDefinition`, a JSON array string or a list of
  `BsonDocument` stages.
- **`Collation`** is optional.

## No `$out` or `$merge`

A view can't write, so a [`$out`](https://www.mongodb.com/docs/manual/reference/operator/aggregation/out/) or
[`$merge`](https://www.mongodb.com/docs/manual/reference/operator/aggregation/merge/) stage makes the definition invalid.
The check follows the places MongoDB runs a nested pipeline (each facet of a `$facet`, and the `pipeline` of a
`$lookup` or `$unionWith`), so one buried in a sub-pipeline is still rejected. A field or literal that merely happens
to be named `$out` or `$merge` (for example under `$literal`) is data, not a stage, and is left alone.

## What `up` does

| On the server | `up` |
|---|---|
| Missing | `create` with `viewOn`, `pipeline` and `collation`. |
| A different pipeline or source | `collMod`, in place. |
| A different collation | Drops the view and creates it again (a view's collation can't change). |
| A collection with the same name | Blocked, exit code `4`. |

Views are applied after every collection, and a view on another view after the one it reads from. Views that read from
each other in a cycle are a build error (`MM0011`).

## Pruning

With [`collections.prune: drop`](./Undeclared-Collections.md), undeclared views are dropped. Anything a declared or kept
view depends on is never pruned.

## See also

- [Managing Collections](./Managing-Collections.md)
- [Undeclared Collections](./Undeclared-Collections.md)
