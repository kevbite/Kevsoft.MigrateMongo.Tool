using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

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
    Location Location);

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
