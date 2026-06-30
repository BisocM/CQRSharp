using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace CQRSharp.Generators;

/// <summary>
///     A value-equatable wrapper over an array, used so the incremental-generator pipeline can structurally compare
///     collected models and short-circuit when nothing changed. A plain array (or a <c>record</c> with an array field)
///     compares by reference, which would defeat caching.
/// </summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
    where T : IEquatable<T>
{
    public static readonly EquatableArray<T> Empty = new(Array.Empty<T>());

    private readonly T[]? _array;

    public EquatableArray(T[] array) => _array = array;

    public int Count => _array?.Length ?? 0;

    public T this[int index] => _array![index];

    public bool Equals(EquatableArray<T> other)
    {
        if (_array is null || other._array is null) return ReferenceEquals(_array, other._array);
        if (_array.Length != other._array.Length) return false;
        for (var i = 0; i < _array.Length; i++)
            if (!_array[i].Equals(other._array[i]))
                return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        if (_array is null) return 0;
        unchecked
        {
            var hash = 17;
            foreach (var item in _array)
                hash = hash * 31 + (item?.GetHashCode() ?? 0);
            return hash;
        }
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_array ?? Array.Empty<T>())).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public static implicit operator EquatableArray<T>(T[] array) => new(array);
}

internal static class EquatableArrayExtensions
{
    public static EquatableArray<T> ToEquatableArray<T>(this IEnumerable<T> source) where T : IEquatable<T>
        => new(source as T[] ?? source.ToArray());
}

/// <summary>
///     An equatable, symbol-free description of a source location. A Roslyn <see cref="Location" /> holds a reference
///     to its <see cref="SyntaxTree" />, so it must not be cached in the pipeline; this record carries only the value
///     pieces and reconstructs a <see cref="Location" /> at diagnostic-report time.
/// </summary>
internal sealed record LocationInfo(string FilePath, TextSpan TextSpan, LinePositionSpan LineSpan)
{
    public Location ToLocation() => Location.Create(FilePath, TextSpan, LineSpan);

    public static LocationInfo? CreateFrom(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        return location is null ? null : CreateFrom(location);
    }

    public static LocationInfo? CreateFrom(Location location)
        => location.SourceTree is null
            ? null
            : new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
}
