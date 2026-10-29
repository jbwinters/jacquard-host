using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace JacquardLang.Host.Protocol;

/// <summary>
/// A decoded JSON value: <see cref="JsonNull"/>, <see cref="JsonBool"/>,
/// <see cref="JsonInt"/>, <see cref="JsonString"/>, <see cref="JsonArray"/>, or
/// <see cref="JsonObject"/>. Values are immutable and compare structurally;
/// objects keep their keys in code-point order, the order the canonical
/// encoding uses.
/// </summary>
public abstract class Json : IEquatable<Json>
{
    /// <summary>The smallest Core <c>Int</c> and JSON integer: -2^62.</summary>
    public const long IntMin = -(1L << 62);

    /// <summary>The largest Core <c>Int</c> and JSON integer: 2^62 - 1.</summary>
    public const long IntMax = (1L << 62) - 1;

    private protected Json()
    {
    }

    /// <summary>JSON <c>null</c>.</summary>
    public static JsonNull Null { get; } = new();

    /// <summary>Orders strings by Unicode code point, as Rust, Go, and Python do.</summary>
    public static IComparer<string> CodePointOrder { get; } = new CodePointComparer();

    /// <summary>A string value.</summary>
    public static JsonString Of(string value) => new(value);

    /// <summary>An integer value; the encoder refuses one outside the 63-bit range.</summary>
    public static JsonInt Of(long value) => new(value);

    /// <summary>A boolean value.</summary>
    public static JsonBool Of(bool value) => value ? JsonBool.True : JsonBool.False;

    /// <summary>An array value holding a copy of <paramref name="items"/>.</summary>
    public static JsonArray Of(IEnumerable<Json> items) => new(items);

    /// <summary>An object from key/value pairs, such as <c>Json.Object(("kind", Json.Of("int")))</c>.</summary>
    /// <exception cref="ArgumentException">A key repeats.</exception>
    [SuppressMessage("Naming", "CA1720", Justification = "JSON's own name for the value.")]
    public static JsonObject Object(params (string Key, Json Value)[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return new JsonObject(fields.Select(field => new KeyValuePair<string, Json>(field.Key, field.Value)));
    }

    /// <summary>An object holding a sorted copy of <paramref name="fields"/>.</summary>
    /// <exception cref="ArgumentException">A key repeats.</exception>
    [SuppressMessage("Naming", "CA1720", Justification = "JSON's own name for the value.")]
    public static JsonObject Object(IEnumerable<KeyValuePair<string, Json>> fields) => new(fields);

    /// <summary>The object, or null if this is not an object.</summary>
    public JsonObject? AsObject() => this as JsonObject;

    /// <summary>The string, or null if this is not a string.</summary>
    public string? AsString() => (this as JsonString)?.Value;

    /// <summary>The items, or null if this is not an array.</summary>
    public IReadOnlyList<Json>? AsArray() => (this as JsonArray)?.Items;

    /// <summary>The integer, or null if this is not an integer.</summary>
    public long? AsInt() => (this as JsonInt)?.Value;

    /// <inheritdoc/>
    public abstract bool Equals(Json? other);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as Json);

    /// <inheritdoc/>
    public abstract override int GetHashCode();

    /// <summary>The canonical encoding, or a placeholder for a value that has none.</summary>
    public override string ToString()
    {
        try
        {
            return JsonCodec.Stringify(this, 256);
        }
        catch (ProtocolException error)
        {
            return "<unencodable: " + error.Message + ">";
        }
    }

    private sealed class CodePointComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            int i = 0;
            int j = 0;
            while (i < x.Length && j < y.Length)
            {
                int a = CodePoint(x, i, out int aLength);
                int b = CodePoint(y, j, out int bLength);
                if (a != b)
                {
                    return a.CompareTo(b);
                }

                i += aLength;
                j += bLength;
            }

            return (i < x.Length).CompareTo(j < y.Length);
        }

        /// <summary>The code point at <paramref name="index"/>; a lone surrogate is its own unit.</summary>
        private static int CodePoint(string text, int index, out int length)
        {
            char c = text[index];
            if (char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                length = 2;
                return char.ConvertToUtf32(c, text[index + 1]);
            }

            length = 1;
            return c;
        }
    }
}

/// <summary>JSON <c>null</c>.</summary>
public sealed class JsonNull : Json
{
    internal JsonNull()
    {
    }

    /// <inheritdoc/>
    public override bool Equals(Json? other) => other is JsonNull;

    /// <inheritdoc/>
    public override int GetHashCode() => 0;
}

/// <summary>A JSON boolean.</summary>
public sealed class JsonBool : Json
{
    internal static readonly JsonBool True = new(true);
    internal static readonly JsonBool False = new(false);

    private JsonBool(bool value)
    {
        Value = value;
    }

    /// <summary>The boolean.</summary>
    public bool Value { get; }

    /// <inheritdoc/>
    public override bool Equals(Json? other) => other is JsonBool flag && flag.Value == Value;

    /// <inheritdoc/>
    public override int GetHashCode() => Value ? 1 : 2;
}

/// <summary>A JSON integer. Decoding admits only Core's 63-bit range.</summary>
public sealed class JsonInt : Json
{
    internal JsonInt(long value)
    {
        Value = value;
    }

    /// <summary>The integer.</summary>
    public long Value { get; }

    /// <inheritdoc/>
    public override bool Equals(Json? other) => other is JsonInt number && number.Value == Value;

    /// <inheritdoc/>
    public override int GetHashCode() => Value.GetHashCode();
}

/// <summary>A JSON string.</summary>
public sealed class JsonString : Json
{
    internal JsonString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    /// <summary>The string.</summary>
    public string Value { get; }

    /// <inheritdoc/>
    public override bool Equals(Json? other) => other is JsonString text && string.Equals(text.Value, Value, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
}

/// <summary>A JSON array.</summary>
public sealed class JsonArray : Json
{
    private readonly Json[] items;

    internal JsonArray(IEnumerable<Json> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        this.items = items.ToArray();
        foreach (Json item in this.items)
        {
            ArgumentNullException.ThrowIfNull(item, nameof(items));
        }
    }

    /// <summary>The items.</summary>
    public IReadOnlyList<Json> Items => items;

    /// <inheritdoc/>
    public override bool Equals(Json? other)
    {
        if (other is not JsonArray array || array.items.Length != items.Length)
        {
            return false;
        }

        for (int i = 0; i < items.Length; i++)
        {
            if (!items[i].Equals(array.items[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(items.Length);
        foreach (Json item in items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}

/// <summary>A JSON object; it enumerates its fields in code-point order of their keys.</summary>
public sealed class JsonObject : Json, IReadOnlyDictionary<string, Json>
{
    private readonly string[] keys;
    private readonly Json[] values;
    private readonly Dictionary<string, int> index;

    internal JsonObject(IEnumerable<KeyValuePair<string, Json>> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var keyList = new List<string>();
        var valueList = new List<Json>();
        index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, Json> field in fields)
        {
            ArgumentNullException.ThrowIfNull(field.Key, nameof(fields));
            ArgumentNullException.ThrowIfNull(field.Value, nameof(fields));
            if (!index.TryAdd(field.Key, keyList.Count))
            {
                throw new ArgumentException("duplicate JSON field name", nameof(fields));
            }

            keyList.Add(field.Key);
            valueList.Add(field.Value);
        }

        keys = keyList.ToArray();
        values = valueList.ToArray();
        Array.Sort(keys, values, CodePointOrder);
        for (int i = 0; i < keys.Length; i++)
        {
            index[keys[i]] = i;
        }
    }

    /// <summary>The empty object.</summary>
    public static JsonObject Empty { get; } = new(Array.Empty<KeyValuePair<string, Json>>());

    /// <inheritdoc/>
    public int Count => keys.Length;

    /// <inheritdoc/>
    public IEnumerable<string> Keys => keys;

    /// <inheritdoc/>
    public IEnumerable<Json> Values => values;

    /// <inheritdoc/>
    public Json this[string key] => values[index[key]];

    /// <summary>The value for <paramref name="key"/>, or null.</summary>
    public Json? Get(string key) => index.TryGetValue(key, out int at) ? values[at] : null;

    /// <summary>A copy with <paramref name="key"/> set to <paramref name="value"/>.</summary>
    public JsonObject With(string key, Json value)
    {
        var copy = new Dictionary<string, Json>(this, StringComparer.Ordinal)
        {
            [key] = value,
        };
        return new JsonObject(copy);
    }

    /// <summary>A copy without <paramref name="key"/>.</summary>
    public JsonObject Without(string key) => new(this.Where(field => !string.Equals(field.Key, key, StringComparison.Ordinal)));

    /// <inheritdoc/>
    public bool ContainsKey(string key) => index.ContainsKey(key);

    /// <inheritdoc/>
    public bool TryGetValue(string key, out Json value)
    {
        if (index.TryGetValue(key, out int at))
        {
            value = values[at];
            return true;
        }

        value = Null;
        return false;
    }

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, Json>> GetEnumerator()
    {
        for (int i = 0; i < keys.Length; i++)
        {
            yield return new KeyValuePair<string, Json>(keys[i], values[i]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc/>
    public override bool Equals(Json? other)
    {
        if (other is not JsonObject obj || obj.keys.Length != keys.Length)
        {
            return false;
        }

        for (int i = 0; i < keys.Length; i++)
        {
            if (!string.Equals(keys[i], obj.keys[i], StringComparison.Ordinal) || !values[i].Equals(obj.values[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(keys.Length);
        for (int i = 0; i < keys.Length; i++)
        {
            hash.Add(keys[i], StringComparer.Ordinal);
            hash.Add(values[i]);
        }

        return hash.ToHashCode();
    }
}
