using System;
using System.Collections;
using System.Collections.Generic;

namespace Mapwright.Generator;

/// <summary>
/// Value-equatable array wrapper: the incremental pipeline caches on model equality, and
/// plain arrays compare by reference, which would defeat caching entirely.
/// </summary>
internal readonly struct EquatableArray<T>(T[] items) : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly T[]? _items = items;

    public T[] Items => _items ?? [];

    public int Length => Items.Length;

    public bool Equals(EquatableArray<T> other)
    {
        var left = Items;
        var right = other.Items;
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (!left[i].Equals(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = 17;
            foreach (var item in Items)
            {
                hash = hash * 31 + item.GetHashCode();
            }

            return hash;
        }
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal static class EquatableArray
{
    public static EquatableArray<T> From<T>(IEnumerable<T> items) where T : IEquatable<T> =>
        new(ToArray(items));

    private static T[] ToArray<T>(IEnumerable<T> items)
    {
        var list = new List<T>(items);
        return list.ToArray();
    }
}
