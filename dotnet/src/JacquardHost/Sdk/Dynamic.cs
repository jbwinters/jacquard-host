using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Threading;
using JacquardLang.Host.Protocol;

namespace JacquardLang.Host.Sdk;

/// <summary>
/// A native value with no application type: Jacquard data by public name.
/// Decode any result into it with <see cref="Codecs.Dynamic"/> or
/// <see cref="Invocation.ToDynamic"/>. Equality is structural (a
/// <see cref="Real"/> compares its exact bits), and both it and
/// <see cref="ToString"/> walk a value with an explicit stack, so a deep
/// value cannot exhaust the call stack; equality compares each pair of
/// shared sub-values once.
/// </summary>
public abstract class Dynamic : IEquatable<Dynamic>
{
    private const int RenderLimit = 4096;

    /// <summary>How many places hold this value as a child; more than one means it is shared.</summary>
    private int uses;

    private protected Dynamic()
    {
    }

    /// <summary>Test seam: how many pair sets <see cref="Equals(Dynamic?)"/> made on this thread.</summary>
    [ThreadStatic]
    internal static int PairSetsMade;

    /// <summary>Whether this value is a child in more than one place, so a walk can reach it twice.</summary>
    private bool Shared => Volatile.Read(ref uses) > 1;

    /// <summary>Counts each of <paramref name="children"/> as held once more.</summary>
    private protected static void Adopt(IReadOnlyList<Dynamic> children)
    {
        foreach (Dynamic child in children)
        {
            Interlocked.Increment(ref child.uses);
        }
    }

    /// <summary>Unit, the empty tuple.</summary>
    public static Tuple Unit { get; } = new(Array.Empty<Dynamic>());

    /// <summary>The child values, in order: items, fields, or an option's value.</summary>
    private protected virtual IReadOnlyList<Dynamic> Children => Array.Empty<Dynamic>();

    /// <summary>An <c>Int</c>.</summary>
    public static Dynamic Of(long value) => new Int(value);

    /// <summary>An <c>Int</c>.</summary>
    public static Dynamic Of(int value) => new Int(value);

    /// <summary>An <c>Int</c>; a <see cref="uint"/> widens exactly.</summary>
    public static Dynamic Of(uint value) => new Int(value);

    /// <summary>An <c>Int</c>.</summary>
    public static Dynamic Of(short value) => new Int(value);

    /// <summary>An <c>Int</c>.</summary>
    public static Dynamic Of(ushort value) => new Int(value);

    /// <summary>An <c>Int</c>.</summary>
    public static Dynamic Of(sbyte value) => new Int(value);

    /// <summary>An <c>Int</c>.</summary>
    public static Dynamic Of(byte value) => new Int(value);

    /// <summary>A <c>Real</c>.</summary>
    public static Dynamic Of(double value) => new Real(value);

    /// <summary>A <c>Text</c>.</summary>
    public static Dynamic Of(string value) => new Text(value);

    /// <summary>A <c>Bool</c>.</summary>
    public static Dynamic Of(bool value) => new Bool(value);

    /// <summary>Refused: the type has no Jacquard type, and using this does not compile (see the obsolete message).</summary>
    /// <exception cref="ArgumentException">Always.</exception>
    [Obsolete(NoConversion.Unsigned64, error: true)]
    public static Dynamic Of(ulong value) => throw new ArgumentException(NoConversion.Unsigned64, nameof(value));

    /// <summary>Refused: the type has no Jacquard type, and using this does not compile (see the obsolete message).</summary>
    /// <exception cref="ArgumentException">Always.</exception>
    [Obsolete(NoConversion.UnsignedNative, error: true)]
    public static Dynamic Of(nuint value) => throw new ArgumentException(NoConversion.UnsignedNative, nameof(value));

    /// <summary>Refused: the type has no Jacquard type, and using this does not compile (see the obsolete message).</summary>
    /// <exception cref="ArgumentException">Always.</exception>
    [Obsolete(NoConversion.Character, error: true)]
    public static Dynamic Of(char value) => throw new ArgumentException(NoConversion.Character, nameof(value));

    /// <summary>Refused: the type has no Jacquard type, and using this does not compile (see the obsolete message).</summary>
    /// <exception cref="ArgumentException">Always.</exception>
    [Obsolete(NoConversion.Single, error: true)]
    public static Dynamic Of(float value) => throw new ArgumentException(NoConversion.Single, nameof(value));

    /// <summary>Refused: the type has no Jacquard type, and using this does not compile (see the obsolete message).</summary>
    /// <exception cref="ArgumentException">Always.</exception>
    [Obsolete(NoConversion.HalfFloat, error: true)]
    public static Dynamic Of(Half value) => throw new ArgumentException(NoConversion.HalfFloat, nameof(value));

    /// <summary>Refused: the type has no Jacquard type, and using this does not compile (see the obsolete message).</summary>
    /// <exception cref="ArgumentException">Always.</exception>
    [Obsolete(NoConversion.Decimal, error: true)]
    public static Dynamic Of(decimal value) => throw new ArgumentException(NoConversion.Decimal, nameof(value));

    /// <summary>Refused: the type has no Jacquard type, and using this does not compile (see the obsolete message).</summary>
    /// <exception cref="ArgumentException">Always.</exception>
    [Obsolete(NoConversion.Signed128, error: true)]
    public static Dynamic Of(Int128 value) => throw new ArgumentException(NoConversion.Signed128, nameof(value));

    /// <summary>Refused: the type has no Jacquard type, and using this does not compile (see the obsolete message).</summary>
    /// <exception cref="ArgumentException">Always.</exception>
    [Obsolete(NoConversion.Unsigned128, error: true)]
    public static Dynamic Of(UInt128 value) => throw new ArgumentException(NoConversion.Unsigned128, nameof(value));

    /// <summary>Refused: the type has no Jacquard type, and using this does not compile (see the obsolete message).</summary>
    /// <exception cref="ArgumentException">Always.</exception>
    [Obsolete(NoConversion.Big, error: true)]
    public static Dynamic Of(System.Numerics.BigInteger value) => throw new ArgumentException(NoConversion.Big, nameof(value));

    /// <summary>A <c>List</c>.</summary>
    /// <exception cref="ArgumentException">More items than the v0 hard limit.</exception>
    public static Dynamic ListOf(params Dynamic[] items) => new List(items);

    /// <summary>A tuple.</summary>
    /// <exception cref="ArgumentException">More items than the v0 hard limit.</exception>
    public static Dynamic TupleOf(params Dynamic[] items) => new Tuple(items);

    /// <summary><c>Some(value)</c>.</summary>
    public static Dynamic Some(Dynamic value) => new Option(value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary><c>None</c>.</summary>
    public static Dynamic None() => new Option(null);

    /// <summary>A constructor value by public name; <see cref="ProgramData.Make"/> also checks that the constructor exists.</summary>
    /// <exception cref="ArgumentException">More fields than the v0 hard limit.</exception>
    public static Dynamic VariantOf(string name, params Dynamic[] fields) => new Variant(name, fields);

    /// <summary>
    /// A read-only copy of <paramref name="items"/>, refused with an
    /// <see cref="ArgumentException"/> when it holds more items than any v0
    /// frame admits: counted first when the collection says its count, and
    /// otherwise enumerated at most one item past the limit.
    /// </summary>
    private static ReadOnlyCollection<Dynamic> Limited(IEnumerable<Dynamic> items, string what)
    {
        ArgumentNullException.ThrowIfNull(items);
        int limit = Limits.Hard.MaxCollectionItems;
        int? known = items switch
        {
            IReadOnlyCollection<Dynamic> sized => sized.Count,
            ICollection<Dynamic> sized => sized.Count,
            _ => null,
        };
        if (known > limit)
        {
            throw new ArgumentException(what + " of " + known.Value.ToString(CultureInfo.InvariantCulture)
                + " items exceeds the v0 hard limit of " + limit.ToString(CultureInfo.InvariantCulture), nameof(items));
        }

        var copy = new List<Dynamic>(known ?? 0);
        foreach (Dynamic item in items)
        {
            if (copy.Count == limit)
            {
                throw new ArgumentException(what + " exceeds the v0 hard limit of " + limit.ToString(CultureInfo.InvariantCulture) + " items", nameof(items));
            }

            copy.Add(item ?? throw new ArgumentNullException(nameof(items), what + " holds null"));
        }

        Adopt(copy);
        return copy.AsReadOnly();
    }

    /// <summary>Whether this node, apart from its children, equals <paramref name="other"/>.</summary>
    private protected abstract bool SameNode(Dynamic other);

    /// <summary>This node's hash, apart from its children.</summary>
    private protected abstract int NodeHash();

    /// <summary>This node's text before its children, such as <c>Variant(Job</c>.</summary>
    private protected abstract string Open();

    /// <inheritdoc/>
    public bool Equals(Dynamic? other)
    {
        if (other is null)
        {
            return false;
        }

        // Each pair of nodes is compared once: values that share sub-values
        // (as decoded values and caller-built graphs may) are compared in
        // time proportional to their distinct pairs, not their paths. Only a
        // pair with a shared side can be reached twice (an unshared node has
        // one parent and one place in it), so only those pairs are kept, and
        // the set is made at the first: unshared values need none.
        var stack = new Stack<(Dynamic Left, Dynamic Right)>();
        HashSet<(Dynamic Left, Dynamic Right)>? compared = null;
        stack.Push((this, other));
        while (stack.Count > 0)
        {
            (Dynamic left, Dynamic right) = stack.Pop();
            if (ReferenceEquals(left, right))
            {
                continue;
            }

            if (left.Shared || right.Shared)
            {
                if (compared is null)
                {
                    compared = new HashSet<(Dynamic Left, Dynamic Right)>(PairComparer.Instance);
                    PairSetsMade++;
                }

                if (!compared.Add((left, right)))
                {
                    continue;
                }
            }

            if (left.GetType() != right.GetType() || !left.SameNode(right))
            {
                return false;
            }

            IReadOnlyList<Dynamic> a = left.Children;
            IReadOnlyList<Dynamic> b = right.Children;
            if (a.Count != b.Count)
            {
                return false;
            }

            for (int i = 0; i < a.Count; i++)
            {
                stack.Push((a[i], b[i]));
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Dynamic other && Equals(other);

    /// <summary>Pairs of nodes by reference.</summary>
    private sealed class PairComparer : IEqualityComparer<(Dynamic Left, Dynamic Right)>
    {
        internal static readonly PairComparer Instance = new();

        public bool Equals((Dynamic Left, Dynamic Right) x, (Dynamic Left, Dynamic Right) y) =>
            ReferenceEquals(x.Left, y.Left) && ReferenceEquals(x.Right, y.Right);

        public int GetHashCode((Dynamic Left, Dynamic Right) obj) =>
            HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Left), System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Right));
    }

    /// <summary>A hash of this node and its children's count; equal values hash alike.</summary>
    public override int GetHashCode() => HashCode.Combine(GetType(), NodeHash(), Children.Count);

    /// <summary>The value as text, cut at 4,096 chars.</summary>
    public override string ToString()
    {
        var output = new StringBuilder();
        var stack = new Stack<object>();
        stack.Push(this);
        while (stack.Count > 0 && output.Length <= RenderLimit)
        {
            object next = stack.Pop();
            if (next is string text)
            {
                output.Append(text);
                continue;
            }

            var node = (Dynamic)next;
            output.Append(node.Open());
            IReadOnlyList<Dynamic> children = node.Children;
            stack.Push(")");
            for (int i = children.Count - 1; i >= 0; i--)
            {
                stack.Push(children[i]);
                if (i > 0 || node is Variant)
                {
                    stack.Push(", ");
                }
            }
        }

        return output.Length > RenderLimit ? output.ToString(0, RenderLimit) + "..." : output.ToString();
    }

    /// <summary>A Jacquard <c>Int</c>.</summary>
    /// <param name="Value">The integer.</param>
    public sealed class Int(long Value) : Dynamic
    {
        /// <summary>The integer.</summary>
        public long Value { get; } = Value;

        private protected override bool SameNode(Dynamic other) => other is Int value && value.Value == Value;

        private protected override int NodeHash() => Value.GetHashCode();

        private protected override string Open() => "Int(" + Value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>A Jacquard <c>Real</c>; equality compares the exact bits.</summary>
    /// <param name="Value">The double.</param>
    public sealed class Real(double Value) : Dynamic
    {
        /// <summary>The double.</summary>
        public double Value { get; } = Value;

        private protected override bool SameNode(Dynamic other) =>
            other is Real value && BitConverter.DoubleToInt64Bits(value.Value) == BitConverter.DoubleToInt64Bits(Value);

        private protected override int NodeHash() => BitConverter.DoubleToInt64Bits(Value).GetHashCode();

        private protected override string Open() => "Real(" + Value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>A Jacquard <c>Text</c>.</summary>
    public sealed class Text : Dynamic
    {
        /// <summary>A text value.</summary>
        public Text(string value)
        {
            Value = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>The string.</summary>
        public string Value { get; }

        private protected override bool SameNode(Dynamic other) => other is Text value && string.Equals(value.Value, Value, StringComparison.Ordinal);

        private protected override int NodeHash() => StringComparer.Ordinal.GetHashCode(Value);

        private protected override string Open() => "Text(" + Identities.Excerpt(Value);
    }

    /// <summary>A Jacquard <c>Bool</c>.</summary>
    /// <param name="Value">The boolean.</param>
    public sealed class Bool(bool Value) : Dynamic
    {
        /// <summary>The boolean.</summary>
        public bool Value { get; } = Value;

        private protected override bool SameNode(Dynamic other) => other is Bool value && value.Value == Value;

        private protected override int NodeHash() => Value ? 1 : 2;

        private protected override string Open() => Value ? "Bool(true" : "Bool(false";
    }

    /// <summary>A Jacquard <c>Hash</c>.</summary>
    public sealed class HashValue : Dynamic
    {
        /// <summary>A hash value.</summary>
        public HashValue(Hash value)
        {
            Value = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>The hash.</summary>
        public Hash Value { get; }

        private protected override bool SameNode(Dynamic other) => other is HashValue value && value.Value.Equals(Value);

        private protected override int NodeHash() => Value.GetHashCode();

        private protected override string Open() => "Hash(" + Value.Hex;
    }

    /// <summary>A positional product; the empty tuple is unit.</summary>
    public sealed class Tuple : Dynamic
    {
        /// <summary>A tuple of a copy of <paramref name="items"/>.</summary>
        /// <exception cref="ArgumentException">More items than the v0 hard limit.</exception>
        public Tuple(IEnumerable<Dynamic> items)
        {
            Items = Limited(items, "tuple");
        }

        /// <summary>The items.</summary>
        public IReadOnlyList<Dynamic> Items { get; }

        private protected override IReadOnlyList<Dynamic> Children => Items;

        private protected override bool SameNode(Dynamic other) => true;

        private protected override int NodeHash() => 3;

        private protected override string Open() => "Tuple(";
    }

    /// <summary>A Jacquard <c>List</c>.</summary>
    public sealed class List : Dynamic
    {
        /// <summary>A list of a copy of <paramref name="items"/>.</summary>
        /// <exception cref="ArgumentException">More items than the v0 hard limit.</exception>
        public List(IEnumerable<Dynamic> items)
        {
            Items = Limited(items, "list");
        }

        /// <summary>The items.</summary>
        public IReadOnlyList<Dynamic> Items { get; }

        private protected override IReadOnlyList<Dynamic> Children => Items;

        private protected override bool SameNode(Dynamic other) => true;

        private protected override int NodeHash() => 4;

        private protected override string Open() => "List(";
    }

    /// <summary>A Jacquard <c>Option</c>: <see cref="Value"/> is null for <c>None</c>.</summary>
    public sealed class Option : Dynamic
    {
        /// <summary><c>Some(value)</c>, or <c>None</c> for null.</summary>
        public Option(Dynamic? value)
        {
            Value = value;
            if (value is not null)
            {
                Adopt(new[] { value });
            }

        }

        /// <summary>The value of <c>Some</c>, or null for <c>None</c>.</summary>
        public Dynamic? Value { get; }

        private protected override IReadOnlyList<Dynamic> Children => Value is null ? Array.Empty<Dynamic>() : new[] { Value };

        private protected override bool SameNode(Dynamic other) => other is Option option && (option.Value is null) == (Value is null);

        private protected override int NodeHash() => Value is null ? 5 : 6;

        private protected override string Open() => Value is null ? "None(" : "Some(";
    }

    /// <summary>
    /// A constructor by public name (store spelling such as <c>stock</c>, or
    /// display spelling such as <c>Stock</c>) and its positional fields.
    /// </summary>
    public sealed class Variant : Dynamic
    {
        /// <summary>A variant of a copy of <paramref name="fields"/>.</summary>
        /// <exception cref="ArgumentException">More fields than the v0 hard limit.</exception>
        public Variant(string name, IEnumerable<Dynamic> fields)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Fields = Limited(fields, "constructor");
        }

        /// <summary>The constructor's public name.</summary>
        public string Name { get; }

        /// <summary>The positional fields.</summary>
        public IReadOnlyList<Dynamic> Fields { get; }

        private protected override IReadOnlyList<Dynamic> Children => Fields;

        private protected override bool SameNode(Dynamic other) => other is Variant variant && string.Equals(variant.Name, Name, StringComparison.Ordinal);

        private protected override int NodeHash() => StringComparer.Ordinal.GetHashCode(Name);

        private protected override string Open() => "Variant(" + Identities.Excerpt(Name);
    }
}
