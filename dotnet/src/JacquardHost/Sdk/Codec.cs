using System;
using System.Collections.Generic;
using JacquardLang.Host.Protocol;

namespace JacquardLang.Host.Sdk;

/// <summary>
/// Maps one .NET type to Jacquard values and back. Codecs are explicit,
/// immutable, and thread-safe; they hold no program state, and encode and
/// decode against the <see cref="ProgramData"/> of the program at hand. Build
/// them with the factories on <see cref="Codec"/>, or derive from this class
/// for a custom mapping (use the <see cref="Encoder"/>'s methods so the
/// encoding is bounded as it goes, and report a value that does not decode
/// with <see cref="Decoder.Mapping"/>).
/// </summary>
/// <typeparam name="T">The .NET type.</typeparam>
public abstract class Codec<T>
{
    /// <summary>Encodes <paramref name="value"/>; <paramref name="encoder"/> names its location for errors and bounds it by the selected limits.</summary>
    /// <exception cref="JacquardException">An invalid argument.</exception>
    public abstract Value Encode(Encoder encoder, T value);

    /// <summary>Decodes <paramref name="value"/>.</summary>
    /// <exception cref="JacquardException">A binding failure when the value does not map to <typeparamref name="T"/>.</exception>
    public abstract T Decode(Decoder decoder, Value value);

    /// <summary><paramref name="value"/> as a function argument encoded with this codec.</summary>
    public Argument Arg(T value) => Argument.Of(this, value);

    /// <summary>Whether null is a value of this codec (unit and nullable codecs); otherwise a null is refused before the codec runs.</summary>
    internal virtual bool AcceptsNull => false;
}

/// <summary>
/// A codec that can be a case of a union (<see cref="Codecs.Union{TUnion}"/>):
/// a record, an enumeration, a union, or a lazy codec standing for one.
/// </summary>
public interface IUnionCase
{
}

/// <summary>What a union asks of each of its cases.</summary>
internal interface ICase : IUnionCase
{
    /// <summary>The .NET type this case encodes.</summary>
    Type CaseType { get; }

    /// <summary>Whether <paramref name="value"/> is built by one of this case's constructors.</summary>
    bool Decodes(Decoder decoder, Value value);

    Value EncodeCase(Encoder encoder, object value);

    object? DecodeCase(Decoder decoder, Value value);
}

/// <summary>
/// One field of a record: its name (for error locations), its codec, and how
/// to read it from a record. Made by <see cref="Codecs.Field{TRecord, TField}"/>.
/// </summary>
/// <typeparam name="TRecord">The record type.</typeparam>
/// <typeparam name="TField">The field type.</typeparam>
public sealed class Field<TRecord, TField> : Builtins.IField<TRecord>
{
    private readonly Func<TRecord, TField> read;

    internal Field(string name, Codec<TField> codec, Func<TRecord, TField> read)
    {
        Name = name;
        Codec = codec;
        this.read = read;
    }

    /// <summary>The field's name, used in error locations.</summary>
    public string Name { get; }

    /// <summary>The field's codec.</summary>
    public Codec<TField> Codec { get; }

    /// <summary>Encodes the field of <paramref name="record"/>, reporting a reader that throws as an invalid argument with its exception kept.</summary>
    internal Value Encode(Encoder encoder, TRecord record)
    {
        TField value;
        try
        {
            value = read(record);
        }
        catch (Exception error) when (Caller.Wraps(error))
        {
            throw encoder.Invalid("cannot read " + typeof(TRecord).Name + "." + Identities.Excerpt(Name) + ": " + Identities.Describe(error), error);
        }

        return encoder.Field(Name, Codec, value);
    }

    string Builtins.IField<TRecord>.Name => Name;

    Value Builtins.IField<TRecord>.Encode(Encoder encoder, TRecord record) => Encode(encoder, record);

    object? Builtins.IField<TRecord>.Decode(Decoder decoder, Value value) => Codec.Decode(decoder, value);
}

/// <summary>
/// The codec factories: built-in types, lists, options, tuples, records,
/// enumerations, unions, lazy (recursive) codecs, raw protocol values, and
/// <see cref="Sdk.Dynamic"/>. Records are mapped by explicit field readers and
/// a constructor function rather than by reflection, so mapping works under
/// trimming and ahead-of-time compilation.
/// </summary>
public static class Codecs
{
    /// <summary><c>Int</c> as a <see cref="long"/>, Core's full 63-bit range; a <see cref="long"/> outside it is refused.</summary>
    public static Codec<long> Int { get; } = new Builtins.IntCodec();

    /// <summary><c>Int</c> as an <see cref="int"/>; a result that does not fit an <see cref="int"/> does not decode.</summary>
    public static Codec<int> Int32 { get; } = new Builtins.Int32Codec();

    /// <summary><c>Real</c> as the exact binary64 bits of a <see cref="double"/>, including NaN payloads and signed zero.</summary>
    public static Codec<double> Real { get; } = new Builtins.RealCodec();

    /// <summary><c>Text</c> as a <see cref="string"/>; a lone surrogate is refused.</summary>
    public static Codec<string> Text { get; } = new Builtins.TextCodec();

    /// <summary><c>Bool</c> as a <see cref="bool"/>.</summary>
    public static Codec<bool> Bool { get; } = new Builtins.BoolCodec();

    /// <summary><c>Hash</c> as a protocol <see cref="Protocol.Hash"/>.</summary>
    public static Codec<Hash> Hash { get; } = new Builtins.HashCodec();

    /// <summary>Unit, the empty tuple, as <see cref="Sdk.Unit"/>.</summary>
    public static Codec<Unit> Unit { get; } = new Builtins.UnitCodec();

    /// <summary>A raw protocol <see cref="Protocol.Value"/>, passed through unchanged (within the same bounds).</summary>
    public static Codec<Value> Value { get; } = new Builtins.ValueCodec();

    /// <summary>Any value by public name, as <see cref="Sdk.Dynamic"/>.</summary>
    public static Codec<Dynamic> Dynamic { get; } = new Builtins.DynamicCodec();

    /// <summary><c>List T</c> as a read-only list; decoding returns a new array.</summary>
    public static Codec<IReadOnlyList<T>> List<T>(Codec<T> item) => new Builtins.ListCodec<T>(item ?? throw new ArgumentNullException(nameof(item)));

    /// <summary><c>Option T</c> as an <see cref="Option{T}"/>, which nests.</summary>
    public static Codec<Option<T>> Option<T>(Codec<T> inner) => new Builtins.OptionCodec<T>(inner ?? throw new ArgumentNullException(nameof(inner)));

    /// <summary><c>Option T</c> as a nullable reference: null is <c>None</c>. Nested options need <see cref="Option{T}"/>.</summary>
    public static Codec<T?> Nullable<T>(Codec<T> inner)
        where T : class => new Builtins.NullableCodec<T>(inner ?? throw new ArgumentNullException(nameof(inner)));

    /// <summary><c>Option T</c> as a nullable value type: null is <c>None</c>.</summary>
    public static Codec<T?> NullableValue<T>(Codec<T> inner)
        where T : struct => new Builtins.NullableValueCodec<T>(inner ?? throw new ArgumentNullException(nameof(inner)));

    /// <summary>A two-item tuple.</summary>
    public static Codec<(T1, T2)> Tuple<T1, T2>(Codec<T1> first, Codec<T2> second) =>
        new Builtins.TupleCodec<T1, T2>(first ?? throw new ArgumentNullException(nameof(first)), second ?? throw new ArgumentNullException(nameof(second)));

    /// <summary>A three-item tuple.</summary>
    public static Codec<(T1, T2, T3)> Tuple<T1, T2, T3>(Codec<T1> first, Codec<T2> second, Codec<T3> third) =>
        new Builtins.TupleCodec<T1, T2, T3>(
            first ?? throw new ArgumentNullException(nameof(first)),
            second ?? throw new ArgumentNullException(nameof(second)),
            third ?? throw new ArgumentNullException(nameof(third)));

    /// <summary>A four-item tuple.</summary>
    public static Codec<(T1, T2, T3, T4)> Tuple<T1, T2, T3, T4>(Codec<T1> first, Codec<T2> second, Codec<T3> third, Codec<T4> fourth) =>
        new Builtins.TupleCodec<T1, T2, T3, T4>(
            first ?? throw new ArgumentNullException(nameof(first)),
            second ?? throw new ArgumentNullException(nameof(second)),
            third ?? throw new ArgumentNullException(nameof(third)),
            fourth ?? throw new ArgumentNullException(nameof(fourth)));

    /// <summary>A record field: its name (for error locations), its codec, and how to read it.</summary>
    public static Field<TRecord, TField> Field<TRecord, TField>(string name, Codec<TField> codec, Func<TRecord, TField> read) =>
        new(name ?? throw new ArgumentNullException(nameof(name)), codec ?? throw new ArgumentNullException(nameof(codec)), read ?? throw new ArgumentNullException(nameof(read)));

    /// <summary>
    /// A record (any .NET type) as one Jacquard constructor with no fields,
    /// such as <c>UnknownSku</c>. The constructor is named by public name:
    /// display spelling (<c>UnknownSku</c>) or store spelling (<c>unknown-sku</c>).
    /// </summary>
    public static RecordCodec<TRecord> Record<TRecord>(string constructor, Func<TRecord> make)
    {
        ArgumentNullException.ThrowIfNull(make);
        return new(constructor, Array.Empty<Builtins.IField<TRecord>>(), _ => make());
    }

    /// <summary>A record as one constructor with one field; each field's codec decodes before <paramref name="make"/> runs.</summary>
    public static RecordCodec<TRecord> Record<TRecord, T1>(
        string constructor,
        Field<TRecord, T1> f1,
        Func<T1, TRecord> make)
    {
        ArgumentNullException.ThrowIfNull(make);
        return new(constructor, Builtins.Fields<TRecord>(f1), a => make((T1)a[0]!));
    }

    /// <summary>A record as one constructor with two fields, in order; each field's codec decodes before <paramref name="make"/> runs.</summary>
    public static RecordCodec<TRecord> Record<TRecord, T1, T2>(
        string constructor,
        Field<TRecord, T1> f1,
        Field<TRecord, T2> f2,
        Func<T1, T2, TRecord> make)
    {
        ArgumentNullException.ThrowIfNull(make);
        return new(constructor, Builtins.Fields<TRecord>(f1, f2), a => make((T1)a[0]!, (T2)a[1]!));
    }

    /// <summary>A record as one constructor with three fields, in order; each field's codec decodes before <paramref name="make"/> runs.</summary>
    public static RecordCodec<TRecord> Record<TRecord, T1, T2, T3>(
        string constructor,
        Field<TRecord, T1> f1,
        Field<TRecord, T2> f2,
        Field<TRecord, T3> f3,
        Func<T1, T2, T3, TRecord> make)
    {
        ArgumentNullException.ThrowIfNull(make);
        return new(constructor, Builtins.Fields<TRecord>(f1, f2, f3), a => make((T1)a[0]!, (T2)a[1]!, (T3)a[2]!));
    }

    /// <summary>A record as one constructor with four fields, in order; each field's codec decodes before <paramref name="make"/> runs.</summary>
    public static RecordCodec<TRecord> Record<TRecord, T1, T2, T3, T4>(
        string constructor,
        Field<TRecord, T1> f1,
        Field<TRecord, T2> f2,
        Field<TRecord, T3> f3,
        Field<TRecord, T4> f4,
        Func<T1, T2, T3, T4, TRecord> make)
    {
        ArgumentNullException.ThrowIfNull(make);
        return new(constructor, Builtins.Fields<TRecord>(f1, f2, f3, f4), a => make((T1)a[0]!, (T2)a[1]!, (T3)a[2]!, (T4)a[3]!));
    }

    /// <summary>A record as one constructor with five fields, in order; each field's codec decodes before <paramref name="make"/> runs.</summary>
    public static RecordCodec<TRecord> Record<TRecord, T1, T2, T3, T4, T5>(
        string constructor,
        Field<TRecord, T1> f1,
        Field<TRecord, T2> f2,
        Field<TRecord, T3> f3,
        Field<TRecord, T4> f4,
        Field<TRecord, T5> f5,
        Func<T1, T2, T3, T4, T5, TRecord> make)
    {
        ArgumentNullException.ThrowIfNull(make);
        return new(constructor, Builtins.Fields<TRecord>(f1, f2, f3, f4, f5), a => make((T1)a[0]!, (T2)a[1]!, (T3)a[2]!, (T4)a[3]!, (T5)a[4]!));
    }

    /// <summary>A record as one constructor with six fields, in order; each field's codec decodes before <paramref name="make"/> runs.</summary>
    public static RecordCodec<TRecord> Record<TRecord, T1, T2, T3, T4, T5, T6>(
        string constructor,
        Field<TRecord, T1> f1,
        Field<TRecord, T2> f2,
        Field<TRecord, T3> f3,
        Field<TRecord, T4> f4,
        Field<TRecord, T5> f5,
        Field<TRecord, T6> f6,
        Func<T1, T2, T3, T4, T5, T6, TRecord> make)
    {
        ArgumentNullException.ThrowIfNull(make);
        return new(constructor, Builtins.Fields<TRecord>(f1, f2, f3, f4, f5, f6), a => make((T1)a[0]!, (T2)a[1]!, (T3)a[2]!, (T4)a[3]!, (T5)a[4]!, (T6)a[5]!));
    }

    /// <summary>A record as one constructor with seven fields, in order; each field's codec decodes before <paramref name="make"/> runs.</summary>
    public static RecordCodec<TRecord> Record<TRecord, T1, T2, T3, T4, T5, T6, T7>(
        string constructor,
        Field<TRecord, T1> f1,
        Field<TRecord, T2> f2,
        Field<TRecord, T3> f3,
        Field<TRecord, T4> f4,
        Field<TRecord, T5> f5,
        Field<TRecord, T6> f6,
        Field<TRecord, T7> f7,
        Func<T1, T2, T3, T4, T5, T6, T7, TRecord> make)
    {
        ArgumentNullException.ThrowIfNull(make);
        return new(constructor, Builtins.Fields<TRecord>(f1, f2, f3, f4, f5, f6, f7), a => make((T1)a[0]!, (T2)a[1]!, (T3)a[2]!, (T4)a[3]!, (T5)a[4]!, (T6)a[5]!, (T7)a[6]!));
    }

    /// <summary>A record as one constructor with eight fields, in order; each field's codec decodes before <paramref name="make"/> runs.</summary>
    public static RecordCodec<TRecord> Record<TRecord, T1, T2, T3, T4, T5, T6, T7, T8>(
        string constructor,
        Field<TRecord, T1> f1,
        Field<TRecord, T2> f2,
        Field<TRecord, T3> f3,
        Field<TRecord, T4> f4,
        Field<TRecord, T5> f5,
        Field<TRecord, T6> f6,
        Field<TRecord, T7> f7,
        Field<TRecord, T8> f8,
        Func<T1, T2, T3, T4, T5, T6, T7, T8, TRecord> make)
    {
        ArgumentNullException.ThrowIfNull(make);
        return new(constructor, Builtins.Fields<TRecord>(f1, f2, f3, f4, f5, f6, f7, f8), a => make((T1)a[0]!, (T2)a[1]!, (T3)a[2]!, (T4)a[3]!, (T5)a[4]!, (T6)a[5]!, (T7)a[6]!, (T8)a[7]!));
    }

    /// <summary>
    /// An enumeration as nullary constructors named by convention: a member
    /// such as <c>PageNow</c> is the constructor <c>PageNow</c> (store
    /// spelling <c>page-now</c>).
    /// </summary>
    public static EnumCodec<TEnum> Enum<TEnum>()
        where TEnum : struct, Enum => new(null);

    /// <summary>An enumeration with an explicit constructor name for every member.</summary>
    /// <exception cref="ArgumentException">A member has no name.</exception>
    public static EnumCodec<TEnum> Enum<TEnum>(IReadOnlyDictionary<TEnum, string> constructors)
        where TEnum : struct, Enum => new(constructors ?? throw new ArgumentNullException(nameof(constructors)));

    /// <summary>
    /// The constructors of one data type as a .NET type hierarchy: each case
    /// is a record, enumeration, or union codec (or a lazy one standing for
    /// one) whose .NET type derives from <typeparamref name="TUnion"/>.
    /// Encoding takes the first case, in order with nested unions flattened,
    /// whose type the value has; decoding takes the first whose constructor
    /// built the value.
    /// </summary>
    /// <exception cref="ArgumentException">A case is none of these.</exception>
    public static UnionCodec<TUnion> Union<TUnion>(params IUnionCase[] cases) => new(cases ?? throw new ArgumentNullException(nameof(cases)));

    /// <summary>
    /// A codec made on first use by <paramref name="make"/>, for recursive
    /// types: a field of a type can refer to the type's own codec through a
    /// lazy one. The codec is made once, thread-safely.
    /// </summary>
    public static LazyCodec<T> Lazy<T>(Func<Codec<T>> make) => new(make ?? throw new ArgumentNullException(nameof(make)));

    /// <summary>A custom codec from two functions.</summary>
    public static Codec<T> Create<T>(Func<Encoder, T, Value> encode, Func<Decoder, Value, T> decode) =>
        new Builtins.CustomCodec<T>(encode ?? throw new ArgumentNullException(nameof(encode)), decode ?? throw new ArgumentNullException(nameof(decode)));
}
