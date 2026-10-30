using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Kevsoft.MigrateMongo.SourceGenerator;

internal sealed record MigrationModel(
    string Id,
    string Name,
    string TypeName,
    string DisplayName,
    int Transaction,
    string Checksum,
    EquatableArray<string> Parameters,
    bool ImplementsInterface,
    bool IsAbstract,
    bool IsAccessible,
    bool HasConstructor,
    LocationInfo Location);

/// <summary>
/// Where a migration's diagnostics go, compared by value. A <see cref="Microsoft.CodeAnalysis.Location"/> holds its
/// syntax tree, so a model with one would differ after any edit to the file (missing the pipeline's cache) and keep
/// the old tree alive.
/// </summary>
internal sealed record LocationInfo(string FilePath, TextSpan TextSpan, LinePositionSpan LineSpan)
{
    public static LocationInfo From(Location location) =>
        new(location.SourceTree?.FilePath ?? string.Empty, location.SourceSpan, location.GetLineSpan().Span);

    public Location ToLocation() => Location.Create(FilePath, TextSpan, LineSpan);
}

/// <summary>Value-equality wrapper so the incremental pipeline can cache models.</summary>
internal readonly struct EquatableArray<T>(ImmutableArray<T> items) : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly ImmutableArray<T> _items = items.IsDefault ? ImmutableArray<T>.Empty : items;

    public int Count => _items.IsDefault ? 0 : _items.Length;

    public T this[int index] => _items[index];

    public bool Equals(EquatableArray<T> other) => (_items.IsDefault ? ImmutableArray<T>.Empty : _items)
        .SequenceEqual(other._items.IsDefault ? ImmutableArray<T>.Empty : other._items);

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 17;
        foreach (var item in this)
        {
            hash = (hash * 31) + item.GetHashCode();
        }

        return hash;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items.IsDefault ? ImmutableArray<T>.Empty : _items)).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
