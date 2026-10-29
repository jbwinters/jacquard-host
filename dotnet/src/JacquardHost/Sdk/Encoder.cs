using System;
using System.Collections.Generic;
using System.Globalization;
using JacquardLang.Host.Protocol;

namespace JacquardLang.Host.Sdk;

/// <summary>
/// Encodes one value at a location. Every encoding is bounded by the
/// selected limits as it goes, so no value, however it is built (one long
/// text referenced many times, say), costs more than about one frame of work
/// and memory: the JSON depth Core's wire form will need,
/// <c>max_collection_items</c> for each list, tuple, and constructor,
/// <c>max_value_nodes</c> values, and <c>max_text_bytes</c> for each text (in
/// UTF-8 bytes) and <c>max_frame_bytes</c> for all of them together. A text
/// longer in chars than either is refused before it is scanned. Nesting is
/// checked before each nested encoder is made, so no value or codec nesting
/// can recurse past the depth limit.
/// </summary>
/// <remarks>
/// A custom codec may return a <see cref="Value"/> it built itself rather
/// than through an encoder. Such a value is not unchecked: before its type is
/// checked, and so before any worker starts, a function call measures every
/// argument value again under one shared budget (nodes, JSON depth,
/// collection items, and text bytes, alone and together), whatever codec
/// built it; the invoke frame is then measured as it is rendered, and the
/// host measures a handler's result the same way before sending it. A value
/// returned by <see cref="ProgramData.Encode{T}"/> is not measured again.
/// </remarks>
public sealed class Encoder
{
    private readonly int depth;
    private readonly Budget budget;

    private Encoder(ProgramData data, string path, int depth, Budget budget)
    {
        Data = data;
        Path = path;
        this.depth = depth;
        this.budget = budget;
    }

    /// <summary>The program's data.</summary>
    public ProgramData Data { get; }

    /// <summary>The location being encoded, such as <c>triage.batch.argument[1][0].service</c>.</summary>
    public string Path { get; }

    /// <summary>What one encoding has used, shared by every location in it.</summary>
    internal sealed class Budget
    {
        internal int Nodes { get; set; }

        internal long TextBytes { get; set; }
    }

    /// <summary>A root encoder at <paramref name="path"/>, sharing <paramref name="budget"/> (as one frame's arguments do) or with a fresh one.</summary>
    internal static Encoder Root(ProgramData data, string path, Budget? budget = null)
    {
        var encoder = new Encoder(data, path, 3, budget ?? new Budget());
        encoder.CheckDepth(encoder.depth);
        return encoder;
    }

    /// <summary>An invalid-argument failure at this location.</summary>
    public JacquardException Invalid(string message) => JacquardException.InvalidAt(Path, message);

    /// <summary>An invalid-argument failure at this location, caused by <paramref name="cause"/>.</summary>
    internal JacquardException Invalid(string message, Exception cause) => JacquardException.InvalidAt(Path, message, cause);

    private void CheckDepth(long needed)
    {
        if (needed > Data.Limits.MaxJsonDepth)
        {
            throw Invalid("value exceeds the v0 JSON depth; use smaller independent batches");
        }
    }

    private Encoder Child(string segment, int extra)
    {
        // Checked before the child exists, so nesting stops at the depth limit.
        CheckDepth((long)depth + extra);
        return new Encoder(Data, Path + segment, depth + extra, budget);
    }

    /// <summary>Charges one value node.</summary>
    private Value Node(Value value)
    {
        if (budget.Nodes >= Data.Limits.MaxValueNodes)
        {
            throw Invalid("value exceeds max_value_nodes; use smaller independent batches");
        }

        budget.Nodes++;
        return value;
    }

    /// <summary>Encodes <paramref name="value"/> with <paramref name="codec"/> here.</summary>
    internal Value Encode<T>(Codec<T> codec, T value)
    {
        ArgumentNullException.ThrowIfNull(codec);
        if (value is null && !codec.AcceptsNull)
        {
            throw Invalid("null has no Jacquard value; use Option<T>.None (or a nullable codec) for None, and Unit.Value for unit");
        }

        return codec.Encode(this, value);
    }

    /// <summary>Encodes a named field of a record or variant one level deeper.</summary>
    /// <exception cref="JacquardException">An invalid argument.</exception>
    public Value Field<T>(string name, Codec<T> codec, T value)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Child("." + Identities.Excerpt(name), 2).Encode(codec, value);
    }

    /// <summary>Encodes a positional item of a tuple one level deeper.</summary>
    /// <exception cref="JacquardException">An invalid argument.</exception>
    public Value Item<T>(int index, Codec<T> codec, T value) =>
        Child("[" + index.ToString(CultureInfo.InvariantCulture) + "]", 2).Encode(codec, value);

    /// <summary>
    /// A constructor value by public name, such as <c>"Job"</c> or <c>"job"</c>,
    /// of fields already encoded (through <see cref="Field{T}"/>). Bool,
    /// List, and Option constructors are refused: use <see cref="Codecs.Bool"/>,
    /// <see cref="Codecs.List{T}"/>, and <see cref="Codecs.Option{T}"/>.
    /// </summary>
    /// <exception cref="JacquardException">An invalid argument: an unknown constructor, or too many fields.</exception>
    public Value Constructor(string name, IReadOnlyList<Value> fields)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(fields);

        // Counted before the fields are copied into the value.
        CheckCount(fields.Count, "constructor");
        if (ProgramData.Builtins.Contains(Identities.DisplayToStore(name)))
        {
            throw Invalid("use the native form of builtin constructor " + Identities.Excerpt(name));
        }

        return Builtin(name, fields);
    }

    private Value Builtin(string name, IReadOnlyList<Value> fields)
    {
        Hash identity = Data.ConstructorId(name) ?? throw Invalid("unknown constructor " + Identities.Excerpt(name));
        return Node(new ConstructorValue(identity, fields));
    }

    /// <summary>A tuple of already encoded items.</summary>
    internal Value Tuple(IReadOnlyList<Value> items) => Node(new TupleValue(items));

    /// <summary>Refuses more than <c>max_collection_items</c> items, before any is encoded.</summary>
    internal void CheckCount(int count, string what)
    {
        if (count > Data.Limits.MaxCollectionItems)
        {
            throw Invalid(what + " exceeds max_collection_items");
        }
    }

    internal Value Bool(bool value) => Builtin(value ? "true" : "false", Array.Empty<Value>());

    internal Value Integer(long value)
    {
        if (value < Json.IntMin || value > Json.IntMax)
        {
            throw Invalid("integer is outside Core's 63-bit Int range");
        }

        return Node(new IntValue(value));
    }

    internal Value Real(double value) => Node(RealValue.Of(value));

    internal Value Text(string text)
    {
        ChargeText(text);
        return Node(new TextValue(text));
    }

    /// <summary>Charges a text by its UTF-8 bytes, refusing before it is scanned when its length in chars already exceeds a limit.</summary>
    private void ChargeText(string text)
    {
        int limit = Data.Limits.MaxTextBytes;

        // A text's UTF-8 length is at least its length in chars, so these
        // refuse before it is scanned.
        if (text.Length > limit)
        {
            throw Invalid("text exceeds max_text_bytes");
        }

        if (budget.TextBytes + text.Length > Data.Limits.MaxFrameBytes)
        {
            throw Invalid("value exceeds max_frame_bytes; use smaller independent batches");
        }

        if (!JsonCodec.IsScalar(text))
        {
            throw Invalid("text contains a lone surrogate, which has no Jacquard encoding");
        }

        long length = JsonCodec.Utf8Length(text);
        if (length > limit)
        {
            throw Invalid("text exceeds max_text_bytes");
        }

        budget.TextBytes += length;
        if (budget.TextBytes > Data.Limits.MaxFrameBytes)
        {
            throw Invalid("value exceeds max_frame_bytes; use smaller independent batches");
        }
    }

    internal Value Hash(Hash hash) => Node(new HashValue(hash));

    /// <summary>
    /// A List of <paramref name="count"/> items of <paramref name="items"/>
    /// from <paramref name="start"/>: one <c>cons</c> per item, nested on the
    /// wire. The count is checked before any item is read.
    /// </summary>
    internal Value List<T>(Codec<T> codec, IReadOnlyList<T> items, int start, int count)
    {
        CheckCount(count, "list");

        // A linked list nests one constructor per element on the wire.
        CheckDepth((long)depth + (2L * count));
        var encoded = new Value[count];
        for (int index = count - 1; index >= 0; index--)
        {
            encoded[index] = Child("[" + index.ToString(CultureInfo.InvariantCulture) + "]", 2 * (index + 1)).Encode(codec, items[start + index]);
        }

        Value result = Builtin("nil", Array.Empty<Value>());
        for (int index = count - 1; index >= 0; index--)
        {
            result = Builtin("cons", new[] { encoded[index], result });
        }

        return result;
    }

    /// <summary><c>None</c>, or <c>Some</c> of <paramref name="value"/> one level deeper.</summary>
    internal Value Option<T>(Codec<T> codec, bool present, T value)
    {
        if (!present)
        {
            return Builtin("none", Array.Empty<Value>());
        }

        Value inner = Child(string.Empty, 2).Encode(codec, value);
        return Builtin("some", new[] { inner });
    }

    /// <summary>
    /// Charges a raw boundary value passed through unchanged, walking it
    /// within the same bounds; a value shared many times is counted each time
    /// it occurs. The walk keeps an explicit stack, and the depth is checked
    /// before each level is entered.
    /// </summary>
    internal Value Raw(Value value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var stack = new Stack<(Value Value, int Level)>();
        stack.Push((value, 0));
        while (stack.Count > 0)
        {
            (Value current, int level) = stack.Pop();
            if (current is null)
            {
                throw Invalid("null is not a boundary value");
            }

            if ((long)depth + (2L * level) > Data.Limits.MaxJsonDepth)
            {
                throw Invalid("value exceeds the v0 JSON depth; use smaller independent batches");
            }

            Node(current);
            switch (current)
            {
                case TextValue text:
                    ChargeText(text.Value);
                    break;
                case TupleValue tuple:
                    CheckCount(tuple.Items.Count, "tuple");
                    Pending(stack.Count, tuple.Items.Count);
                    foreach (Value item in tuple.Items)
                    {
                        stack.Push((item, level + 1));
                    }

                    break;
                case ConstructorValue constructor:
                    CheckCount(constructor.Arguments.Count, "constructor");
                    Pending(stack.Count, constructor.Arguments.Count);
                    foreach (Value argument in constructor.Arguments)
                    {
                        stack.Push((argument, level + 1));
                    }

                    break;
                case IntValue number when number.Value < Json.IntMin || number.Value > Json.IntMax:
                    throw Invalid("integer is outside Core's 63-bit Int range");
                default:
                    // Real and Hash are one node each.
                    break;
            }
        }

        return value;
    }

    /// <summary>
    /// Refuses before <paramref name="children"/> more values are stacked when
    /// the values already charged and stacked could not all be charged, so
    /// the walk's stack never holds more than <c>max_value_nodes</c> values.
    /// </summary>
    private void Pending(int stacked, int children)
    {
        if ((long)budget.Nodes + stacked + children > Data.Limits.MaxValueNodes)
        {
            throw Invalid("value exceeds max_value_nodes; use smaller independent batches");
        }
    }
}

/// <summary>Decodes boundary values with the program's data.</summary>
public sealed class Decoder
{
    internal Decoder(ProgramData data)
    {
        Data = data;
    }

    /// <summary>The program's data.</summary>
    public ProgramData Data { get; }

    /// <summary>Decodes a nested value.</summary>
    /// <exception cref="JacquardException">A binding failure when the value does not map.</exception>
    public T Decode<T>(Codec<T> codec, Value value)
    {
        ArgumentNullException.ThrowIfNull(codec);
        return codec.Decode(this, value);
    }

    /// <summary>Whether <paramref name="value"/> is built by the named constructor.</summary>
    public bool Is(Value value, string constructor)
    {
        ArgumentNullException.ThrowIfNull(constructor);
        return Data.ConstructorId(constructor) is { } expected && value is ConstructorValue built && built.Identity.Equals(expected);
    }

    /// <summary>The fields of a value built by the named constructor with <paramref name="count"/> fields.</summary>
    /// <exception cref="JacquardException">A mapping failure for another constructor or count.</exception>
    public IReadOnlyList<Value> Fields(Value value, string constructor, int count)
    {
        if (Is(value, constructor) && value is ConstructorValue built && built.Arguments.Count == count)
        {
            return built.Arguments;
        }

        throw Mapping(Identities.Excerpt(constructor) + " result has the wrong constructor or field count");
    }

    /// <summary>A mapping failure for a value that is none of a type's constructors.</summary>
    public JacquardException Mismatch(string typeName) => Mapping("unknown " + Identities.Excerpt(typeName) + " case");

    /// <summary>A mapping failure: a boundary value that does not decode into the requested type.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Part of the decoder a custom codec is given, beside its other failures.")]
    public JacquardException Mapping(string? message) => JacquardException.Binding(message ?? "null");
}
