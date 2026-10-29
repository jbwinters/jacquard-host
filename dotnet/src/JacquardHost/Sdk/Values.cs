using System;
using System.Collections.Generic;
using JacquardLang.Host.Protocol;

namespace JacquardLang.Host.Sdk;

/// <summary>Jacquard's unit, the empty tuple, as a .NET value.</summary>
public readonly record struct Unit
{
    /// <summary>The unit value.</summary>
    public static Unit Value => default;

    /// <inheritdoc/>
    public override string ToString() => "()";
}

/// <summary>
/// A Jacquard <c>Option</c>: <see cref="None"/> or <c>Some</c> of a value.
/// Unlike a nullable type, options nest: <c>Option (Option Int)</c> is
/// <c>Option&lt;Option&lt;long&gt;&gt;</c>, and <c>Some(None)</c> stays distinct
/// from <c>None</c>.
/// </summary>
/// <typeparam name="T">The value's type.</typeparam>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000", Justification = "Option<T>.None and Option<T>.Some name the two cases, as Nullable and ImmutableArray do.")]
public readonly struct Option<T> : IEquatable<Option<T>>
{
    private readonly T value;

    private Option(T value)
    {
        this.value = value;
        HasValue = true;
    }

    /// <summary>The empty option.</summary>
    public static Option<T> None => default;

    /// <summary>Whether this is <c>Some</c>.</summary>
    public bool HasValue { get; }

    /// <summary>The value of <c>Some</c>.</summary>
    /// <exception cref="InvalidOperationException">The option is <c>None</c>.</exception>
    public T Value => HasValue ? value : throw new InvalidOperationException("the option is None");

    /// <summary><c>Some(value)</c>.</summary>
    public static Option<T> Some(T value) => new(value);

    /// <summary>Whether two options are both <c>None</c>, or both <c>Some</c> of equal values.</summary>
    public static bool operator ==(Option<T> left, Option<T> right) => left.Equals(right);

    /// <summary>Whether two options differ.</summary>
    public static bool operator !=(Option<T> left, Option<T> right) => !left.Equals(right);

    /// <summary>The value of <c>Some</c>, or <paramref name="fallback"/> for <c>None</c>.</summary>
    public T GetValueOrDefault(T fallback) => HasValue ? value : fallback;

    /// <inheritdoc/>
    public bool Equals(Option<T> other) => HasValue == other.HasValue && (!HasValue || EqualityComparer<T>.Default.Equals(value, other.value));

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Option<T> other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HasValue ? HashCode.Combine(true, value) : 0;

    /// <inheritdoc/>
    public override string ToString() => HasValue ? "Some(" + value + ")" : "None";
}

/// <summary>Builds <see cref="Option{T}"/> values with the type inferred.</summary>
public static class Option
{
    /// <summary><c>Some(value)</c>.</summary>
    public static Option<T> Some<T>(T value) => Option<T>.Some(value);
}

/// <summary>
/// Why each .NET numeric type without a Jacquard type is refused. Without
/// these refusals C# would convert a <see cref="ulong"/> or
/// <see cref="nuint"/> silently through <see cref="double"/>, a
/// <see cref="char"/> to an <c>Int</c>, and a <see cref="float"/> to a
/// <see cref="double"/> with a different decimal text. The conversions to
/// <see cref="Argument"/> and the <see cref="Dynamic.Of(long)"/> overloads
/// for these types are marked obsolete as errors, so using one does not
/// compile; reached anyway (by reflection), the argument is refused when it
/// is encoded and the <c>Of</c> overload throws.
/// </summary>
internal static class NoConversion
{
    internal const string Unsigned64 = "ulong has no Jacquard type and is not converted: Int is a signed 63-bit integer; pass a range-checked long";
    internal const string UnsignedNative = "nuint has no Jacquard type and is not converted: Int is a signed 63-bit integer; pass a range-checked long";
    internal const string Character = "char has no Jacquard type and is not converted: pass a string for Text, or a long for its code";
    internal const string Single = "float is not converted: Real is a double; pass (double)value";
    internal const string HalfFloat = "Half is not converted: Real is a double; pass (double)value";
    internal const string Decimal = "decimal has no Jacquard type and is never converted: pass money as an integer count of minor units (long); Real is a double";
    internal const string Signed128 = "Int128 has no Jacquard type and is not converted: Int is a signed 63-bit integer; pass a range-checked long";
    internal const string Unsigned128 = "UInt128 has no Jacquard type and is not converted: Int is a signed 63-bit integer; pass a range-checked long";
    internal const string Big = "BigInteger has no Jacquard type and is not converted: Int is a signed 63-bit integer; pass a range-checked long";
}

/// <summary>
/// A function argument with the codec that encodes it. Plain values convert
/// implicitly: <see cref="long"/> and <see cref="int"/> (and the smaller
/// integer types and <see cref="uint"/>, which widen exactly) as <c>Int</c>,
/// <see cref="double"/> as <c>Real</c>, <see cref="string"/> as <c>Text</c>,
/// <see cref="bool"/>, <see cref="Protocol.Hash"/>, a protocol
/// <see cref="Protocol.Value"/>, and <see cref="Sdk.Dynamic"/>. Anything else
/// is passed as <c>codec.Arg(value)</c>, as are lists and records.
/// <see cref="ulong"/>, <see cref="nuint"/>, <see cref="char"/>,
/// <see cref="float"/>, <see cref="Half"/>, <see cref="decimal"/>,
/// <see cref="Int128"/>, <see cref="UInt128"/>, and
/// <see cref="System.Numerics.BigInteger"/> are refused at compile time
/// instead of converting silently: money crosses as an integer count of
/// minor units.
/// </summary>
public abstract class Argument
{
    private protected Argument()
    {
    }

    /// <summary>An <c>Int</c>, Core's full 63-bit range; a value outside it is refused.</summary>
    public static implicit operator Argument(long value) => Codecs.Int.Arg(value);

    /// <summary>An <c>Int</c>.</summary>
    public static implicit operator Argument(int value) => Codecs.Int.Arg(value);

    /// <summary>An <c>Int</c>; a <see cref="uint"/> widens exactly.</summary>
    public static implicit operator Argument(uint value) => Codecs.Int.Arg(value);

    /// <summary>An <c>Int</c>.</summary>
    public static implicit operator Argument(short value) => Codecs.Int.Arg(value);

    /// <summary>An <c>Int</c>.</summary>
    public static implicit operator Argument(ushort value) => Codecs.Int.Arg(value);

    /// <summary>An <c>Int</c>.</summary>
    public static implicit operator Argument(sbyte value) => Codecs.Int.Arg(value);

    /// <summary>An <c>Int</c>.</summary>
    public static implicit operator Argument(byte value) => Codecs.Int.Arg(value);

    /// <summary>A <c>Real</c>, the exact binary64 bits of the double.</summary>
    public static implicit operator Argument(double value) => Codecs.Real.Arg(value);

    /// <summary>A <c>Text</c>; a lone surrogate is refused.</summary>
    public static implicit operator Argument(string value) => Codecs.Text.Arg(value);

    /// <summary>A <c>Bool</c>.</summary>
    public static implicit operator Argument(bool value) => Codecs.Bool.Arg(value);

    /// <summary>A <c>Hash</c>.</summary>
    public static implicit operator Argument(Hash value) => Codecs.Hash.Arg(value);

    /// <summary>A protocol value, passed through within the same bounds.</summary>
    public static implicit operator Argument(Value value) => Codecs.Value.Arg(value);

    /// <summary>A value by public name.</summary>
    public static implicit operator Argument(Dynamic value) => Codecs.Dynamic.Arg(value);

    /// <summary>Refused: the type has no Jacquard type, and using this does not compile (see the obsolete message).</summary>
    [Obsolete(NoConversion.Unsigned64, error: true)]
    public static implicit operator Argument(ulong value) => new Refused(NoConversion.Unsigned64);

    /// <summary>Refused: the type has no Jacquard type, and using this does not compile (see the obsolete message).</summary>
    [Obsolete(NoConversion.UnsignedNative, error: true)]
    public static implicit operator Argument(nuint value) => new Refused(NoConversion.UnsignedNative);

    /// <summary>Refused: the type has no Jacquard type, and using this does not compile (see the obsolete message).</summary>
    [Obsolete(NoConversion.Character, error: true)]
    public static implicit operator Argument(char value) => new Refused(NoConversion.Character);

    /// <summary>Refused: the type has no Jacquard type, and using this does not compile (see the obsolete message).</summary>
    [Obsolete(NoConversion.Single, error: true)]
    public static implicit operator Argument(float value) => new Refused(NoConversion.Single);

    /// <summary>Refused: the type has no Jacquard type, and using this does not compile (see the obsolete message).</summary>
    [Obsolete(NoConversion.HalfFloat, error: true)]
    public static implicit operator Argument(Half value) => new Refused(NoConversion.HalfFloat);

    /// <summary>Refused: the type has no Jacquard type, and using this does not compile (see the obsolete message).</summary>
    [Obsolete(NoConversion.Decimal, error: true)]
    public static implicit operator Argument(decimal value) => new Refused(NoConversion.Decimal);

    /// <summary>Refused: the type has no Jacquard type, and using this does not compile (see the obsolete message).</summary>
    [Obsolete(NoConversion.Signed128, error: true)]
    public static implicit operator Argument(Int128 value) => new Refused(NoConversion.Signed128);

    /// <summary>Refused: the type has no Jacquard type, and using this does not compile (see the obsolete message).</summary>
    [Obsolete(NoConversion.Unsigned128, error: true)]
    public static implicit operator Argument(UInt128 value) => new Refused(NoConversion.Unsigned128);

    /// <summary>Refused: the type has no Jacquard type, and using this does not compile (see the obsolete message).</summary>
    [Obsolete(NoConversion.Big, error: true)]
    public static implicit operator Argument(System.Numerics.BigInteger value) => new Refused(NoConversion.Big);

    /// <summary><paramref name="value"/> encoded with <paramref name="codec"/>; the same as <c>codec.Arg(value)</c>.</summary>
    public static Argument Of<T>(Codec<T> codec, T value)
    {
        ArgumentNullException.ThrowIfNull(codec);
        return new Typed<T>(codec, value);
    }

    /// <summary>An <c>Int</c>.</summary>
    public static Argument FromInt64(long value) => value;

    /// <summary>An <c>Int</c>.</summary>
    public static Argument FromInt32(int value) => value;

    /// <summary>A <c>Real</c>.</summary>
    public static Argument FromDouble(double value) => value;

    /// <summary>A <c>Text</c>.</summary>
    public static Argument FromString(string value) => value;

    /// <summary>A <c>Bool</c>.</summary>
    public static Argument FromBoolean(bool value) => value;

    /// <summary>A <c>Hash</c>.</summary>
    public static Argument FromHash(Hash value) => value;

    /// <summary>A protocol value.</summary>
    public static Argument FromValue(Value value) => value;

    /// <summary>A value by public name.</summary>
    public static Argument FromDynamic(Dynamic value) => value;

    /// <summary>Encodes the argument at <paramref name="encoder"/>'s location.</summary>
    internal abstract Value Encode(Encoder encoder);

    private sealed class Typed<T> : Argument
    {
        private readonly Codec<T> codec;
        private readonly T value;

        internal Typed(Codec<T> codec, T value)
        {
            this.codec = codec;
            this.value = value;
        }

        internal override Value Encode(Encoder encoder) => encoder.Encode(codec, value);
    }

    /// <summary>A value of a type with no Jacquard type, refused when it is encoded, before any worker starts.</summary>
    private sealed class Refused : Argument
    {
        private readonly string reason;

        internal Refused(string reason)
        {
            this.reason = reason;
        }

        internal override Value Encode(Encoder encoder) => throw encoder.Invalid(reason);
    }
}

/// <summary>One batch's rows as the final List argument, encoded from the caller's list without copying it.</summary>
internal sealed class Rows<T> : Argument
{
    private readonly Codec<T> codec;
    private readonly IReadOnlyList<T> rows;
    private readonly int start;
    private readonly int count;

    internal Rows(Codec<T> codec, IReadOnlyList<T> rows, int start, int count)
    {
        this.codec = codec;
        this.rows = rows;
        this.start = start;
        this.count = count;
    }

    internal override Value Encode(Encoder encoder) => encoder.List(codec, rows, start, count);
}
