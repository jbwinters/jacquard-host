using System;
using System.Collections.Generic;
using System.Linq;

namespace JacquardLang.Host.Protocol;

/// <summary>
/// One v0 boundary value: <see cref="IntValue"/>, <see cref="TextValue"/>,
/// <see cref="RealValue"/>, <see cref="HashValue"/>, <see cref="TupleValue"/>,
/// or <see cref="ConstructorValue"/>. Values compare structurally.
/// </summary>
public abstract record Value
{
    private protected Value()
    {
    }

    /// <summary>The empty tuple, Jacquard's unit.</summary>
    public static TupleValue Unit { get; } = new(Array.Empty<Value>());

    internal static bool SameItems(Value[] a, Value[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (!a[i].Equals(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    internal static int HashItems(Value[] items)
    {
        HashCode hash = default;
        hash.Add(items.Length);
        foreach (Value item in items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }

    internal static Value[] Copy(IEnumerable<Value> items, string name)
    {
        ArgumentNullException.ThrowIfNull(items, name);
        Value[] copy = items.ToArray();
        foreach (Value item in copy)
        {
            ArgumentNullException.ThrowIfNull(item, name);
        }

        return copy;
    }
}

/// <summary>
/// Core's signed 63-bit integer, held exactly in a <see cref="long"/>. A
/// <see cref="long"/> outside <c>-2^62..2^62-1</c> can be held but has no
/// encoding (E1604). Money crosses the boundary as an integer count of minor
/// units, never as a <see cref="RealValue"/>.
/// </summary>
/// <param name="Value">The integer.</param>
public sealed record IntValue(long Value) : Value;

/// <summary>A Unicode scalar string; a lone surrogate has no encoding (E1601).</summary>
public sealed record TextValue : Value
{
    /// <summary>A text value.</summary>
    public TextValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    /// <summary>The string.</summary>
    public string Value { get; }

    /// <inheritdoc/>
    public bool Equals(TextValue? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
}

/// <summary>Binary64 bits, preserving NaN payloads and signed zero.</summary>
/// <param name="Bits">The IEEE 754 binary64 bits.</param>
public sealed record RealValue(ulong Bits) : Value
{
    /// <summary>The <see cref="RealValue"/> with the exact bits of <paramref name="value"/>.</summary>
    public static RealValue Of(double value) => new((ulong)BitConverter.DoubleToInt64Bits(value));

    /// <summary>The bits as a <see cref="double"/>.</summary>
    public double ToDouble() => BitConverter.Int64BitsToDouble((long)Bits);
}

/// <summary>An explicit <c>Hash</c>.</summary>
public sealed record HashValue : Value
{
    /// <summary>A hash value.</summary>
    public HashValue(Hash hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        Hash = hash;
    }

    /// <summary>The hash.</summary>
    public Hash Hash { get; }
}

/// <summary>A positional product; the empty tuple is unit.</summary>
public sealed record TupleValue : Value
{
    private readonly Value[] items;

    /// <summary>A tuple holding a copy of <paramref name="items"/>.</summary>
    public TupleValue(IEnumerable<Value> items)
    {
        this.items = Copy(items, nameof(items));
    }

    /// <summary>A tuple of <paramref name="items"/>.</summary>
    public TupleValue(params Value[] items)
        : this((IEnumerable<Value>)items)
    {
    }

    /// <summary>The items.</summary>
    public IReadOnlyList<Value> Items => items;

    /// <inheritdoc/>
    public bool Equals(TupleValue? other) => other is not null && SameItems(items, other.items);

    /// <inheritdoc/>
    public override int GetHashCode() => HashItems(items);
}

/// <summary>A constructor identity and its positional arguments.</summary>
public sealed record ConstructorValue : Value
{
    private readonly Value[] arguments;

    /// <summary>A constructor value holding a copy of <paramref name="arguments"/>.</summary>
    public ConstructorValue(Hash identity, IEnumerable<Value> arguments)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Identity = identity;
        this.arguments = Copy(arguments, nameof(arguments));
    }

    /// <summary>The constructor identity.</summary>
    public Hash Identity { get; }

    /// <summary>The arguments.</summary>
    public IReadOnlyList<Value> Arguments => arguments;

    /// <inheritdoc/>
    public bool Equals(ConstructorValue? other) => other is not null && Identity.Equals(other.Identity) && SameItems(arguments, other.arguments);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Identity, HashItems(arguments));
}
