using System;
using System.Collections.Generic;
using System.Threading;
using JacquardLang.Host.Protocol;

namespace JacquardLang.Host.Sdk;

/// <summary>The codec implementations behind <see cref="Codec"/>'s properties and factories.</summary>
internal static class Builtins
{
    /// <summary>One field of a record codec.</summary>
    internal interface IField<in TRecord>
    {
        string Name { get; }

        Value Encode(Encoder encoder, TRecord record);

        object? Decode(Decoder decoder, Value value);
    }

    /// <summary>A union, whose cases a flattening walk enters.</summary>
    internal interface IUnion
    {
        IReadOnlyList<ICase> Cases { get; }
    }

    /// <summary>A lazy codec, which a walk resolves to what it stands for.</summary>
    internal interface ILazy
    {
        object Target { get; }
    }

    internal static IField<TRecord>[] Fields<TRecord>(params IField<TRecord>[] fields) => fields;

    /// <summary>
    /// What <paramref name="codec"/> stands for once every lazy codec on the
    /// way is made, in a loop (so a chain of any length cannot exhaust the
    /// stack) that refuses a lazy codec standing for itself.
    /// </summary>
    internal static object Resolve(object codec)
    {
        HashSet<object>? seen = null;
        while (codec is ILazy lazy)
        {
            seen ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
            if (!seen.Add(codec))
            {
                throw new InvalidOperationException("a lazy codec stands for itself");
            }

            codec = lazy.Target;
        }

        return codec;
    }

    // Built-in types.
    internal sealed class IntCodec : Codec<long>
    {
        public override Value Encode(Encoder encoder, long value) => encoder.Integer(value);

        public override long Decode(Decoder decoder, Value value) => value is IntValue number ? number.Value : throw decoder.Mapping("expected Int");
    }

    internal sealed class Int32Codec : Codec<int>
    {
        public override Value Encode(Encoder encoder, int value) => encoder.Integer(value);

        public override int Decode(Decoder decoder, Value value) =>
            value is IntValue number && number.Value >= int.MinValue && number.Value <= int.MaxValue
                ? (int)number.Value
                : throw decoder.Mapping("expected Int that fits int");
    }

    internal sealed class RealCodec : Codec<double>
    {
        public override Value Encode(Encoder encoder, double value) => encoder.Real(value);

        public override double Decode(Decoder decoder, Value value) => value is RealValue real ? real.ToDouble() : throw decoder.Mapping("expected Real");
    }

    internal sealed class TextCodec : Codec<string>
    {
        public override Value Encode(Encoder encoder, string value) => encoder.Text(value);

        public override string Decode(Decoder decoder, Value value) => value is TextValue text ? text.Value : throw decoder.Mapping("expected Text");
    }

    internal sealed class BoolCodec : Codec<bool>
    {
        public override Value Encode(Encoder encoder, bool value) => encoder.Bool(value);

        public override bool Decode(Decoder decoder, Value value) =>
            decoder.Data.IsNullary(value, "true") || (decoder.Data.IsNullary(value, "false") ? false : throw decoder.Mapping("expected Bool"));
    }

    internal sealed class HashCodec : Codec<Hash>
    {
        public override Value Encode(Encoder encoder, Hash value) => encoder.Hash(value);

        public override Hash Decode(Decoder decoder, Value value) => value is HashValue hash ? hash.Hash : throw decoder.Mapping("expected Hash");
    }

    internal sealed class UnitCodec : Codec<Unit>
    {
        public override Value Encode(Encoder encoder, Unit value) => encoder.Tuple(Array.Empty<Value>());

        public override Unit Decode(Decoder decoder, Value value) =>
            value is TupleValue { Items.Count: 0 } ? default : throw decoder.Mapping("expected unit");
    }

    internal sealed class ValueCodec : Codec<Value>
    {
        public override Value Encode(Encoder encoder, Value value) => encoder.Raw(value);

        public override Value Decode(Decoder decoder, Value value) => value;
    }

    internal sealed class CustomCodec<T> : Codec<T>
    {
        private readonly Func<Encoder, T, Value> encode;
        private readonly Func<Decoder, Value, T> decode;

        internal CustomCodec(Func<Encoder, T, Value> encode, Func<Decoder, Value, T> decode)
        {
            this.encode = encode;
            this.decode = decode;
        }

        public override Value Encode(Encoder encoder, T value) => encode(encoder, value);

        public override T Decode(Decoder decoder, Value value) => decode(decoder, value);
    }

    internal sealed class ListCodec<T> : Codec<IReadOnlyList<T>>
    {
        private readonly Codec<T> item;

        internal ListCodec(Codec<T> item)
        {
            this.item = item;
        }

        public override Value Encode(Encoder encoder, IReadOnlyList<T> value) => encoder.List(item, value, 0, value.Count);

        public override IReadOnlyList<T> Decode(Decoder decoder, Value value)
        {
            List<Value> items = decoder.Data.ListItems(value);
            var output = new T[items.Count];
            for (int i = 0; i < output.Length; i++)
            {
                output[i] = item.Decode(decoder, items[i]);
            }

            return output;
        }
    }

    /// <summary>The value inside <c>Some</c>, null for <c>None</c>, or a mapping failure for anything else.</summary>
    private static Value? Some(Decoder decoder, Value value)
    {
        if (decoder.Data.IsNullary(value, "none"))
        {
            return null;
        }

        if (value is ConstructorValue some && some.Arguments.Count == 1 && decoder.Data.Label(some.Identity) == "some")
        {
            return some.Arguments[0];
        }

        throw decoder.Mapping("expected Option");
    }

    internal sealed class OptionCodec<T> : Codec<Option<T>>
    {
        private readonly Codec<T> inner;

        internal OptionCodec(Codec<T> inner)
        {
            this.inner = inner;
        }

        public override Value Encode(Encoder encoder, Option<T> value) =>
            encoder.Option(inner, value.HasValue, value.HasValue ? value.Value : default!);

        public override Option<T> Decode(Decoder decoder, Value value) =>
            Some(decoder, value) is { } some ? Option<T>.Some(inner.Decode(decoder, some)) : Option<T>.None;
    }

    internal sealed class NullableCodec<T> : Codec<T?>
        where T : class
    {
        private readonly Codec<T> inner;

        internal NullableCodec(Codec<T> inner)
        {
            this.inner = inner;
        }

        internal override bool AcceptsNull => true;

        public override Value Encode(Encoder encoder, T? value) => encoder.Option(inner, value is not null, value!);

        public override T? Decode(Decoder decoder, Value value)
        {
            if (Some(decoder, value) is not { } some)
            {
                return null;
            }

            return inner.Decode(decoder, some) ?? throw decoder.Mapping("Some of a value that decodes to null has no nullable form; use Codecs.Option");
        }
    }

    internal sealed class NullableValueCodec<T> : Codec<T?>
        where T : struct
    {
        private readonly Codec<T> inner;

        internal NullableValueCodec(Codec<T> inner)
        {
            this.inner = inner;
        }

        internal override bool AcceptsNull => true;

        public override Value Encode(Encoder encoder, T? value) => encoder.Option(inner, value.HasValue, value.GetValueOrDefault());

        public override T? Decode(Decoder decoder, Value value) => Some(decoder, value) is { } some ? inner.Decode(decoder, some) : null;
    }

    private static TupleValue Shape(Decoder decoder, Value value, int count) =>
        value is TupleValue tuple && tuple.Items.Count == count ? tuple : throw decoder.Mapping("tuple shape mismatch");

    internal sealed class TupleCodec<T1, T2> : Codec<(T1, T2)>
    {
        private readonly Codec<T1> first;
        private readonly Codec<T2> second;

        internal TupleCodec(Codec<T1> first, Codec<T2> second)
        {
            this.first = first;
            this.second = second;
        }

        public override Value Encode(Encoder encoder, (T1, T2) value)
        {
            encoder.CheckCount(2, "tuple");
            return encoder.Tuple(new[] { encoder.Item(0, first, value.Item1), encoder.Item(1, second, value.Item2) });
        }

        public override (T1, T2) Decode(Decoder decoder, Value value)
        {
            TupleValue tuple = Shape(decoder, value, 2);
            return (first.Decode(decoder, tuple.Items[0]), second.Decode(decoder, tuple.Items[1]));
        }
    }

    internal sealed class TupleCodec<T1, T2, T3> : Codec<(T1, T2, T3)>
    {
        private readonly Codec<T1> first;
        private readonly Codec<T2> second;
        private readonly Codec<T3> third;

        internal TupleCodec(Codec<T1> first, Codec<T2> second, Codec<T3> third)
        {
            this.first = first;
            this.second = second;
            this.third = third;
        }

        public override Value Encode(Encoder encoder, (T1, T2, T3) value)
        {
            encoder.CheckCount(3, "tuple");
            return encoder.Tuple(new[]
            {
                encoder.Item(0, first, value.Item1), encoder.Item(1, second, value.Item2), encoder.Item(2, third, value.Item3),
            });
        }

        public override (T1, T2, T3) Decode(Decoder decoder, Value value)
        {
            TupleValue tuple = Shape(decoder, value, 3);
            return (first.Decode(decoder, tuple.Items[0]), second.Decode(decoder, tuple.Items[1]), third.Decode(decoder, tuple.Items[2]));
        }
    }

    internal sealed class TupleCodec<T1, T2, T3, T4> : Codec<(T1, T2, T3, T4)>
    {
        private readonly Codec<T1> first;
        private readonly Codec<T2> second;
        private readonly Codec<T3> third;
        private readonly Codec<T4> fourth;

        internal TupleCodec(Codec<T1> first, Codec<T2> second, Codec<T3> third, Codec<T4> fourth)
        {
            this.first = first;
            this.second = second;
            this.third = third;
            this.fourth = fourth;
        }

        public override Value Encode(Encoder encoder, (T1, T2, T3, T4) value)
        {
            encoder.CheckCount(4, "tuple");
            return encoder.Tuple(new[]
            {
                encoder.Item(0, first, value.Item1),
                encoder.Item(1, second, value.Item2),
                encoder.Item(2, third, value.Item3),
                encoder.Item(3, fourth, value.Item4),
            });
        }

        public override (T1, T2, T3, T4) Decode(Decoder decoder, Value value)
        {
            TupleValue tuple = Shape(decoder, value, 4);
            return (
                first.Decode(decoder, tuple.Items[0]),
                second.Decode(decoder, tuple.Items[1]),
                third.Decode(decoder, tuple.Items[2]),
                fourth.Decode(decoder, tuple.Items[3]));
        }
    }

    internal sealed class DynamicCodec : Codec<Dynamic>
    {
        public override Value Encode(Encoder encoder, Dynamic value)
        {
            switch (value)
            {
                case Dynamic.Int number:
                    return encoder.Integer(number.Value);
                case Dynamic.Real real:
                    return encoder.Real(real.Value);
                case Dynamic.Text text:
                    return encoder.Text(text.Value);
                case Dynamic.Bool flag:
                    return encoder.Bool(flag.Value);
                case Dynamic.HashValue hash:
                    return encoder.Hash(hash.Value);
                case Dynamic.Tuple tuple:
                {
                    encoder.CheckCount(tuple.Items.Count, "tuple");
                    var items = new Value[tuple.Items.Count];
                    for (int i = 0; i < items.Length; i++)
                    {
                        items[i] = encoder.Item(i, this, tuple.Items[i]);
                    }

                    return encoder.Tuple(items);
                }

                case Dynamic.List list:
                    return encoder.List(this, list.Items, 0, list.Items.Count);
                case Dynamic.Option option:
                    return encoder.Option(this, option.Value is not null, option.Value!);
                case Dynamic.Variant variant:
                {
                    encoder.CheckCount(variant.Fields.Count, "constructor");
                    var fields = new Value[variant.Fields.Count];
                    for (int i = 0; i < fields.Length; i++)
                    {
                        fields[i] = encoder.Field(i.ToString(System.Globalization.CultureInfo.InvariantCulture), this, variant.Fields[i]);
                    }

                    return encoder.Constructor(variant.Name, fields);
                }

                default:
                    throw encoder.Invalid("unknown Dynamic kind " + value.GetType().Name);
            }
        }

        /// <summary>Decodes a value Core sent, or one measured within the depth limit, so the recursion is bounded by that depth.</summary>
        public override Dynamic Decode(Decoder decoder, Value value)
        {
            switch (value)
            {
                case IntValue number:
                    return new Dynamic.Int(number.Value);
                case RealValue real:
                    return new Dynamic.Real(real.ToDouble());
                case TextValue text:
                    return new Dynamic.Text(text.Value);
                case HashValue hash:
                    return new Dynamic.HashValue(hash.Hash);
                case TupleValue tuple:
                {
                    var items = new Dynamic[tuple.Items.Count];
                    for (int i = 0; i < items.Length; i++)
                    {
                        items[i] = Decode(decoder, tuple.Items[i]);
                    }

                    return new Dynamic.Tuple(items);
                }

                case ConstructorValue constructor:
                {
                    string label = decoder.Data.Label(constructor.Identity) ?? throw decoder.Mapping("unmapped constructor " + constructor.Identity.Hex);
                    switch (label)
                    {
                        case "true" or "false":
                            return new Dynamic.Bool(Codecs.Bool.Decode(decoder, value));
                        case "cons" or "nil":
                        {
                            List<Value> items = decoder.Data.ListItems(value);
                            var decoded = new Dynamic[items.Count];
                            for (int i = 0; i < decoded.Length; i++)
                            {
                                decoded[i] = Decode(decoder, items[i]);
                            }

                            return new Dynamic.List(decoded);
                        }

                        case "some" or "none":
                            return new Dynamic.Option(Some(decoder, value) is { } inner ? Decode(decoder, inner) : null);
                        default:
                        {
                            var fields = new Dynamic[constructor.Arguments.Count];
                            for (int i = 0; i < fields.Length; i++)
                            {
                                fields[i] = Decode(decoder, constructor.Arguments[i]);
                            }

                            return new Dynamic.Variant(label, fields);
                        }
                    }
                }

                default:
                    throw decoder.Mapping("unknown value kind " + value.GetType().Name);
            }
        }
    }
}

/// <summary>A record (any .NET type) as one Jacquard constructor; made by <see cref="Codecs.Record{TRecord}"/> and its overloads.</summary>
/// <typeparam name="TRecord">The record type.</typeparam>
public sealed class RecordCodec<TRecord> : Codec<TRecord>, ICase
{
    private readonly Builtins.IField<TRecord>[] fields;
    private readonly Func<object?[], TRecord> make;

    internal RecordCodec(string constructor, Builtins.IField<TRecord>[] fields, Func<object?[], TRecord> make)
    {
        Constructor = constructor ?? throw new ArgumentNullException(nameof(constructor));
        foreach (Builtins.IField<TRecord> field in fields)
        {
            ArgumentNullException.ThrowIfNull(field, nameof(fields));
        }

        this.fields = fields;
        this.make = make;
    }

    /// <summary>The constructor's public name.</summary>
    public string Constructor { get; }

    Type ICase.CaseType => typeof(TRecord);

    bool ICase.Decodes(Decoder decoder, Value value) => decoder.Is(value, Constructor);

    Value ICase.EncodeCase(Encoder encoder, object value) => Encode(encoder, (TRecord)value);

    object? ICase.DecodeCase(Decoder decoder, Value value) => Decode(decoder, value);

    /// <inheritdoc/>
    public override Value Encode(Encoder encoder, TRecord value)
    {
        ArgumentNullException.ThrowIfNull(encoder);

        // Counted before any field is read.
        encoder.CheckCount(fields.Length, "constructor");
        var encoded = new Value[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            encoded[i] = fields[i].Encode(encoder, value);
        }

        return encoder.Constructor(Constructor, encoded);
    }

    /// <inheritdoc/>
    public override TRecord Decode(Decoder decoder, Value value)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        IReadOnlyList<Value> values = decoder.Fields(value, Constructor, fields.Length);
        object?[] decoded = new object?[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            decoded[i] = fields[i].Decode(decoder, values[i]);
        }

        try
        {
            return make(decoded);
        }
        catch (Exception error) when (Caller.Wraps(error))
        {
            throw JacquardException.Binding(typeof(TRecord).Name + " refused the decoded fields: " + Identities.Describe(error), error);
        }
    }
}

/// <summary>An enumeration as nullary constructors; made by <see cref="Codecs.Enum{TEnum}()"/>.</summary>
/// <typeparam name="TEnum">The enumeration.</typeparam>
public sealed class EnumCodec<TEnum> : Codec<TEnum>, ICase
    where TEnum : struct, Enum
{
    private readonly List<(TEnum Member, string Name)> members = new();

    internal EnumCodec(IReadOnlyDictionary<TEnum, string>? explicitNames)
    {
        var seen = new HashSet<TEnum>();
        foreach (TEnum member in Enum.GetValues<TEnum>())
        {
            if (!seen.Add(member))
            {
                continue;
            }

            string? name = explicitNames is null ? Enum.GetName(member) : explicitNames.TryGetValue(member, out string? given) ? given : null;
            if (name is null)
            {
                throw new ArgumentException(typeof(TEnum).Name + "." + member + " has no constructor name", nameof(explicitNames));
            }

            members.Add((member, name));
        }
    }

    Type ICase.CaseType => typeof(TEnum);

    bool ICase.Decodes(Decoder decoder, Value value)
    {
        foreach ((TEnum _, string name) in members)
        {
            if (decoder.Is(value, name))
            {
                return true;
            }
        }

        return false;
    }

    Value ICase.EncodeCase(Encoder encoder, object value) => Encode(encoder, (TEnum)value);

    object? ICase.DecodeCase(Decoder decoder, Value value) => Decode(decoder, value);

    /// <inheritdoc/>
    public override Value Encode(Encoder encoder, TEnum value)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        foreach ((TEnum member, string name) in members)
        {
            if (EqualityComparer<TEnum>.Default.Equals(member, value))
            {
                return encoder.Constructor(name, Array.Empty<Value>());
            }
        }

        throw encoder.Invalid(typeof(TEnum).Name + " has no constructor for " + value);
    }

    /// <inheritdoc/>
    public override TEnum Decode(Decoder decoder, Value value)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        foreach ((TEnum member, string name) in members)
        {
            if (decoder.Is(value, name))
            {
                decoder.Fields(value, name, 0);
                return member;
            }
        }

        throw decoder.Mismatch(typeof(TEnum).Name);
    }
}

/// <summary>The constructors of one data type as a .NET type hierarchy; made by <see cref="Codecs.Union{TUnion}"/>.</summary>
/// <typeparam name="TUnion">The common .NET type of the cases.</typeparam>
public sealed class UnionCodec<TUnion> : Codec<TUnion>, ICase, Builtins.IUnion
{
    private readonly ICase[] cases;

    /// <summary>
    /// The distinct non-union cases, nested unions flattened in order, so
    /// decoding asks each once. Computed on first use, when every lazy case of
    /// a recursive hierarchy can be made, by one iterative walk over the whole
    /// case graph; published once complete, so a race computes the same list
    /// twice.
    /// </summary>
    private volatile ICase[]? leaves;

    internal UnionCodec(IUnionCase[] given)
    {
        cases = new ICase[given.Length];
        for (int i = 0; i < given.Length; i++)
        {
            if (given[i] is not ICase known)
            {
                throw new ArgumentException("a union case must be a record, enumeration, union, or lazy codec", nameof(given));
            }

            // A lazy case is checked when it is made, on first use.
            if (known is not Builtins.ILazy && !typeof(TUnion).IsAssignableFrom(known.CaseType))
            {
                throw new ArgumentException(known.CaseType.Name + " is not a " + typeof(TUnion).Name, nameof(given));
            }

            cases[i] = known;
        }
    }

    IReadOnlyList<ICase> Builtins.IUnion.Cases => cases;

    Type ICase.CaseType => typeof(TUnion);

    /// <summary>Test seam: how many distinct cases this union decodes through.</summary>
    internal int LeafCount => Leaves().Length;

    private ICase[] Leaves()
    {
        ICase[]? known = leaves;
        if (known is null)
        {
            var flat = new List<ICase>();
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance) { this };
            var stack = new Stack<IEnumerator<ICase>>();
            stack.Push(((IEnumerable<ICase>)cases).GetEnumerator());
            while (stack.Count > 0)
            {
                IEnumerator<ICase> top = stack.Peek();
                if (!top.MoveNext())
                {
                    stack.Pop().Dispose();
                    continue;
                }

                object each = Builtins.Resolve(top.Current);
                if (!seen.Add(each))
                {
                    continue;
                }

                if (each is Builtins.IUnion nested)
                {
                    stack.Push(nested.Cases.GetEnumerator());
                }
                else if (each is ICase leaf && typeof(TUnion).IsAssignableFrom(leaf.CaseType))
                {
                    flat.Add(leaf);
                }
                else
                {
                    throw new InvalidOperationException("a case of the " + typeof(TUnion).Name + " union is not a record or enumeration of it");
                }
            }

            known = flat.ToArray();
            leaves = known;
        }

        return known;
    }

    bool ICase.Decodes(Decoder decoder, Value value)
    {
        foreach (ICase each in Leaves())
        {
            if (each.Decodes(decoder, value))
            {
                return true;
            }
        }

        return false;
    }

    Value ICase.EncodeCase(Encoder encoder, object value) => Encode(encoder, (TUnion)value);

    object? ICase.DecodeCase(Decoder decoder, Value value) => Decode(decoder, value);

    /// <summary>
    /// Encodes with the first flattened case, in the order decoding asks
    /// them, whose type <paramref name="value"/> has: no call per nested
    /// layer, and the cost is the number of distinct cases, not their nesting.
    /// </summary>
    public override Value Encode(Encoder encoder, TUnion value)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        object boxed = value!;
        foreach (ICase each in Leaves())
        {
            if (each.CaseType.IsInstanceOfType(boxed))
            {
                return each.EncodeCase(encoder, boxed);
            }
        }

        throw encoder.Invalid(typeof(TUnion).Name + " has no case for " + boxed.GetType().Name);
    }

    /// <inheritdoc/>
    public override TUnion Decode(Decoder decoder, Value value)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        foreach (ICase each in Leaves())
        {
            if (each.Decodes(decoder, value))
            {
                return (TUnion)each.DecodeCase(decoder, value)!;
            }
        }

        throw decoder.Mismatch(typeof(TUnion).Name);
    }
}

/// <summary>A codec made on first use, for recursive types; made by <see cref="Codecs.Lazy{T}"/>.</summary>
/// <typeparam name="T">The .NET type.</typeparam>
public sealed class LazyCodec<T> : Codec<T>, ICase, Builtins.ILazy
{
    private readonly Lazy<Codec<T>> target;

    internal LazyCodec(Func<Codec<T>> make)
    {
        target = new Lazy<Codec<T>>(() => make() ?? throw new InvalidOperationException("a lazy codec's factory returned null"), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    object Builtins.ILazy.Target => target.Value;

    internal override bool AcceptsNull => Resolved().AcceptsNull;

    Type ICase.CaseType => Case().CaseType;

    private Codec<T> Resolved() => (Codec<T>)Builtins.Resolve(this);

    private ICase Case() => Builtins.Resolve(this) as ICase
        ?? throw new InvalidOperationException("a lazy union case stands for a codec that is not a record, enumeration, or union");

    bool ICase.Decodes(Decoder decoder, Value value) => Case().Decodes(decoder, value);

    Value ICase.EncodeCase(Encoder encoder, object value) => Case().EncodeCase(encoder, value);

    object? ICase.DecodeCase(Decoder decoder, Value value) => Case().DecodeCase(decoder, value);

    /// <inheritdoc/>
    public override Value Encode(Encoder encoder, T value) => Resolved().Encode(encoder, value);

    /// <inheritdoc/>
    public override T Decode(Decoder decoder, Value value) => Resolved().Decode(decoder, value);
}
