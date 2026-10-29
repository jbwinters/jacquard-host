using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace JacquardLang.Host.Protocol;

/// <summary>
/// Strict UTF-8 JSON for the carrier. Decoding refuses invalid UTF-8,
/// duplicate keys, lone surrogates, non-integer numbers, integers outside
/// Core's 63-bit range, trailing data, and nesting deeper than the selected
/// limit. Encoding is canonical: object keys sorted by code point, no
/// whitespace, and the same string escaping as JavaScript's
/// <c>JSON.stringify</c>.
/// </summary>
/// <remarks>
/// The parser is written by hand rather than on <c>System.Text.Json</c>'s
/// reader, so that every rule above, and the order in which they are checked,
/// is visibly the same as in the other adapters. Integers are parsed from
/// their decimal text as a <see cref="long"/>, never through a
/// <see cref="double"/> or a <see cref="decimal"/>.
/// </remarks>
public static class JsonCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly byte[] Hex = "0123456789abcdef"u8.ToArray();

    /// <summary>Decodes one JSON value under the strict rules.</summary>
    /// <exception cref="ProtocolException">E1601 for malformed input, E1602 for nesting deeper than <paramref name="maxDepth"/>.</exception>
    public static Json Decode(ReadOnlySpan<byte> payload, int maxDepth)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException)
        {
            throw ProtocolException.Malformed("payload is not valid UTF-8");
        }

        var decoder = new Decoder(text, maxDepth);
        Json value = decoder.Value(1);
        decoder.Whitespace();
        if (decoder.Position != text.Length)
        {
            throw ProtocolException.Malformed("payload is not one JSON value");
        }

        return value;
    }

    /// <summary>Decodes one JSON object.</summary>
    /// <exception cref="ProtocolException">As <see cref="Decode"/>, and E1601 for any other value.</exception>
    public static JsonObject DecodeObject(ReadOnlySpan<byte> payload, int maxDepth) =>
        Decode(payload, maxDepth) as JsonObject ?? throw ProtocolException.Malformed("expected a JSON object");

    private sealed class Decoder
    {
        private readonly string text;
        private readonly int maxDepth;

        internal Decoder(string text, int maxDepth)
        {
            this.text = text;
            this.maxDepth = maxDepth;
        }

        internal int Position { get; private set; }

        private int Peek() => Position < text.Length ? text[Position] : -1;

        private bool StartsWith(string literal) => string.CompareOrdinal(text, Position, literal, 0, literal.Length) == 0;

        internal void Whitespace()
        {
            while (Position < text.Length)
            {
                char c = text[Position];
                if (c != ' ' && c != '\t' && c != '\r' && c != '\n')
                {
                    return;
                }

                Position++;
            }
        }

        internal Json Value(int depth)
        {
            Whitespace();
            int next = Peek();
            if (next == '"')
            {
                return Json.Of(Quoted());
            }

            if (next == '{' || next == '[')
            {
                if (depth > maxDepth)
                {
                    throw ProtocolException.Exceeded("JSON nesting exceeds max_json_depth");
                }

                return Container((char)next, depth);
            }

            if (StartsWith("true"))
            {
                Position += 4;
                return Json.Of(true);
            }

            if (StartsWith("false"))
            {
                Position += 5;
                return Json.Of(false);
            }

            if (StartsWith("null"))
            {
                Position += 4;
                return Json.Null;
            }

            int start = Position;
            if (Peek() == '-')
            {
                Position++;
            }

            int first = Peek();
            if (first == '0')
            {
                Position++;
            }
            else if (first >= '1' && first <= '9')
            {
                while (Peek() >= '0' && Peek() <= '9')
                {
                    Position++;
                }
            }
            else
            {
                throw ProtocolException.Malformed("invalid JSON scalar");
            }

            if (Position - start > 20)
            {
                throw ProtocolException.Malformed("invalid JSON scalar");
            }

            // The token is an optional '-' and ASCII digits, so this parses
            // exactly; a fraction or exponent is left unread and refused by
            // the caller as trailing data.
            if (!long.TryParse(text.AsSpan(start, Position - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number)
                || number < Json.IntMin || number > Json.IntMax)
            {
                throw ProtocolException.Malformed("JSON integer is outside the 63-bit range");
            }

            return Json.Of(number);
        }

        private Json Container(char open, int depth)
        {
            Position++;
            Whitespace();
            char close = open == '{' ? '}' : ']';
            var items = new List<Json>();
            var fields = new Dictionary<string, Json>(StringComparer.Ordinal);
            if (Peek() == close)
            {
                Position++;
                return Finish(open, items, fields);
            }

            while (Position < text.Length)
            {
                if (open == '[')
                {
                    items.Add(Value(depth + 1));
                }
                else
                {
                    if (Peek() != '"')
                    {
                        throw ProtocolException.Malformed("object key must be a string");
                    }

                    string key = Quoted();
                    Whitespace();
                    if (fields.ContainsKey(key))
                    {
                        throw ProtocolException.Malformed("duplicate JSON field name");
                    }

                    if (Peek() != ':')
                    {
                        throw ProtocolException.Malformed("expected a colon");
                    }

                    Position++;
                    fields.Add(key, Value(depth + 1));
                }

                Whitespace();
                if (Peek() == close)
                {
                    Position++;
                    return Finish(open, items, fields);
                }

                if (Peek() != ',')
                {
                    throw ProtocolException.Malformed("expected a comma");
                }

                Position++;
                Whitespace();
            }

            throw ProtocolException.Malformed("unterminated JSON container");
        }

        private static Json Finish(char open, List<Json> items, Dictionary<string, Json> fields) =>
            open == '{' ? Json.Object(fields) : Json.Of(items);

        private int Hex4()
        {
            if (Position + 4 > text.Length)
            {
                return -1;
            }

            int value = 0;
            for (int i = 0; i < 4; i++)
            {
                char c = text[Position + i];
                int digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
                if (digit < 0)
                {
                    return -1;
                }

                value = (value * 16) + digit;
            }

            Position += 4;
            return value;
        }

        private string Quoted()
        {
            Position++;
            var output = new StringBuilder();
            while (true)
            {
                if (Position >= text.Length)
                {
                    throw ProtocolException.Malformed("unterminated JSON string");
                }

                char c = text[Position];
                if (c == '"')
                {
                    Position++;
                    return output.ToString();
                }

                if (c < 0x20)
                {
                    throw ProtocolException.Malformed("invalid JSON string");
                }

                if (c != '\\')
                {
                    // The payload was validated as UTF-8, so surrogates arrive paired.
                    output.Append(c);
                    Position++;
                    continue;
                }

                Position++;
                if (Position >= text.Length)
                {
                    throw ProtocolException.Malformed("unterminated JSON string");
                }

                char escape = text[Position++];
                switch (escape)
                {
                    case '"':
                        output.Append('"');
                        break;
                    case '\\':
                        output.Append('\\');
                        break;
                    case '/':
                        output.Append('/');
                        break;
                    case 'b':
                        output.Append('\b');
                        break;
                    case 'f':
                        output.Append('\f');
                        break;
                    case 'n':
                        output.Append('\n');
                        break;
                    case 'r':
                        output.Append('\r');
                        break;
                    case 't':
                        output.Append('\t');
                        break;
                    case 'u':
                        Unicode(output);
                        break;
                    default:
                        throw ProtocolException.Malformed("invalid JSON string");
                }
            }
        }

        private void Unicode(StringBuilder output)
        {
            int unit = Hex4();
            if (unit < 0)
            {
                throw ProtocolException.Malformed("invalid JSON string");
            }

            if (unit >= 0xd800 && unit < 0xdc00)
            {
                if (!StartsWith("\\u"))
                {
                    throw ProtocolException.Malformed("a JSON string contains a lone surrogate");
                }

                Position += 2;
                int low = Hex4();
                if (low < 0)
                {
                    throw ProtocolException.Malformed("invalid JSON string");
                }

                if (low < 0xdc00 || low >= 0xe000)
                {
                    throw ProtocolException.Malformed("a JSON string contains a lone surrogate");
                }

                output.Append((char)unit).Append((char)low);
            }
            else if (unit >= 0xdc00 && unit < 0xe000)
            {
                throw ProtocolException.Malformed("a JSON string contains a lone surrogate");
            }
            else
            {
                output.Append((char)unit);
            }
        }
    }

    /// <summary>Encodes a JSON value canonically, without a size bound.</summary>
    /// <exception cref="ProtocolException">E1601 for an integer outside the 63-bit range or a lone surrogate, E1602 for nesting deeper than <paramref name="maxDepth"/>.</exception>
    public static string Stringify(Json value, int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sink = new Sink(Array.MaxLength, 0);
        Write(sink, value, maxDepth, 1);
        return Encoding.UTF8.GetString(sink.Payload);
    }

    /// <summary>
    /// Encodes one carrier frame: a big-endian u32 length and the payload,
    /// refusing payloads outside <c>1..=</c><paramref name="maxBytes"/>.
    /// The encoder counts UTF-8 bytes as it writes them and stops as soon as
    /// the count passes <paramref name="maxBytes"/>, so a value whose encoding
    /// is far larger than a frame costs at most about one frame of memory and
    /// time. A <paramref name="frame"/> that is not a <see cref="JsonObject"/>
    /// is bounded by its field count and key lengths before it is copied or
    /// sorted.
    /// </summary>
    /// <exception cref="ProtocolException">E1602 when the payload exceeds <paramref name="maxBytes"/> or nesting exceeds <paramref name="maxDepth"/>; E1601 as for <see cref="Stringify"/>.</exception>
    public static byte[] FrameBytes(IReadOnlyDictionary<string, Json> frame, int maxBytes, int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(frame);
        JsonObject obj = Bounded(frame, maxBytes);
        var sink = new Sink(maxBytes, 4);
        Write(sink, obj, maxDepth, 1);
        ReadOnlySpan<byte> payload = sink.Payload;
        if (payload.Length == 0 || payload.Length > maxBytes)
        {
            throw ProtocolException.Exceeded("payload exceeds max_frame_bytes");
        }

        byte[] bytes = sink.Frame();
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)payload.Length);
        return bytes;
    }

    /// <summary>
    /// The frame as a sorted object. A caller's own map is refused before it
    /// is copied when its fields cannot fit: each field encodes to at least
    /// its key's length in chars plus four bytes (two quotes, a colon, and a
    /// one-byte value), and every field but one needs a comma.
    /// </summary>
    internal static JsonObject Bounded(IReadOnlyDictionary<string, Json> frame, int maxBytes)
    {
        if (frame is JsonObject sorted)
        {
            return sorted;
        }

        long total = 2;
        if (total + (frame.Count * 5L) - 1 > maxBytes)
        {
            throw ProtocolException.Exceeded("payload exceeds max_frame_bytes");
        }

        var fields = new List<KeyValuePair<string, Json>>(frame.Count);
        foreach (KeyValuePair<string, Json> field in frame)
        {
            total += (field.Key?.Length ?? 0) + 5L;
            if (total - 1 > maxBytes)
            {
                throw ProtocolException.Exceeded("payload exceeds max_frame_bytes");
            }

            fields.Add(field);
        }

        return Json.Object(fields);
    }

    /// <summary>Whether <paramref name="text"/> is a sequence of Unicode scalars (no lone surrogate).</summary>
    public static bool IsScalar(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The UTF-8 length of a scalar string.</summary>
    public static long Utf8Length(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        long length = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c < 0x80)
            {
                length += 1;
            }
            else if (c < 0x800)
            {
                length += 2;
            }
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                length += 4;
                i++;
            }
            else
            {
                length += 3;
            }
        }

        return length;
    }

    private static void Write(Sink output, Json value, int maxDepth, int depth)
    {
        switch (value)
        {
            case JsonNull:
                output.Ascii("null");
                break;
            case JsonBool flag:
                output.Ascii(flag.Value ? "true" : "false");
                break;
            case JsonInt number:
                if (number.Value < Json.IntMin || number.Value > Json.IntMax)
                {
                    throw ProtocolException.Malformed("JSON integer is outside the 63-bit range");
                }

                output.Ascii(number.Value.ToString(CultureInfo.InvariantCulture));
                break;
            case JsonString text:
                Quote(output, text.Value);
                break;
            case JsonArray array:
                if (depth > maxDepth)
                {
                    throw ProtocolException.Exceeded("JSON nesting exceeds max_json_depth");
                }

                output.Byte((byte)'[');
                for (int i = 0; i < array.Items.Count; i++)
                {
                    if (i > 0)
                    {
                        output.Byte((byte)',');
                    }

                    Write(output, array.Items[i], maxDepth, depth + 1);
                }

                output.Byte((byte)']');
                break;
            case JsonObject obj:
                if (depth > maxDepth)
                {
                    throw ProtocolException.Exceeded("JSON nesting exceeds max_json_depth");
                }

                output.Byte((byte)'{');
                bool first = true;
                foreach (KeyValuePair<string, Json> field in obj)
                {
                    if (!first)
                    {
                        output.Byte((byte)',');
                    }

                    first = false;
                    Quote(output, field.Key);
                    output.Byte((byte)':');
                    Write(output, field.Value, maxDepth, depth + 1);
                }

                output.Byte((byte)'}');
                break;
            default:
                throw new ArgumentException("unknown JSON value", nameof(value));
        }
    }

    private static void Quote(Sink output, string text)
    {
        // Each char is at least one UTF-8 byte, so a text that cannot fit is
        // refused before it is scanned.
        if (output.Length + (long)text.Length + 2 > output.Limit)
        {
            throw ProtocolException.Exceeded("payload exceeds max_frame_bytes");
        }

        if (!IsScalar(text))
        {
            throw ProtocolException.Malformed("a JSON string contains a lone surrogate");
        }

        output.Byte((byte)'"');
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            switch (c)
            {
                case '"':
                    output.Ascii("\\\"");
                    break;
                case '\\':
                    output.Ascii("\\\\");
                    break;
                case '\b':
                    output.Ascii("\\b");
                    break;
                case '\f':
                    output.Ascii("\\f");
                    break;
                case '\n':
                    output.Ascii("\\n");
                    break;
                case '\r':
                    output.Ascii("\\r");
                    break;
                case '\t':
                    output.Ascii("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        output.Ascii("\\u00");
                        output.Byte(Hex[c >> 4]);
                        output.Byte(Hex[c & 0xf]);
                    }
                    else if (c < 0x80)
                    {
                        output.Byte((byte)c);
                    }
                    else if (c < 0x800)
                    {
                        output.Byte((byte)(0xc0 | (c >> 6)));
                        output.Byte((byte)(0x80 | (c & 0x3f)));
                    }
                    else if (char.IsHighSurrogate(c))
                    {
                        // IsScalar guarantees the low half follows.
                        int point = char.ConvertToUtf32(c, text[++i]);
                        output.Byte((byte)(0xf0 | (point >> 18)));
                        output.Byte((byte)(0x80 | ((point >> 12) & 0x3f)));
                        output.Byte((byte)(0x80 | ((point >> 6) & 0x3f)));
                        output.Byte((byte)(0x80 | (point & 0x3f)));
                    }
                    else
                    {
                        output.Byte((byte)(0xe0 | (c >> 12)));
                        output.Byte((byte)(0x80 | ((c >> 6) & 0x3f)));
                        output.Byte((byte)(0x80 | (c & 0x3f)));
                    }

                    break;
            }
        }

        output.Byte((byte)'"');
    }

    /// <summary>
    /// The encoding so far, as UTF-8 bytes after a reserved prefix. Every
    /// byte is counted as it is appended, and the encoding stops once it
    /// passes <see cref="Limit"/>; the buffer never grows past the limit.
    /// </summary>
    private sealed class Sink
    {
        private readonly int prefix;
        private byte[] buffer;
        private int used;

        internal Sink(int limit, int prefix)
        {
            Limit = limit;
            this.prefix = prefix;
            buffer = new byte[prefix + Math.Min(limit, 256)];
            used = prefix;
        }

        internal int Limit { get; }

        internal int Length => used - prefix;

        internal ReadOnlySpan<byte> Payload => buffer.AsSpan(prefix, used - prefix);

        /// <summary>The prefix and payload, exactly sized.</summary>
        internal byte[] Frame() => buffer.AsSpan(0, used).ToArray();

        internal void Byte(byte b)
        {
            if (used - prefix >= Limit)
            {
                throw ProtocolException.Exceeded("payload exceeds max_frame_bytes");
            }

            if (used == buffer.Length)
            {
                long grown = Math.Min((long)buffer.Length * 2, (long)prefix + Limit);
                Array.Resize(ref buffer, (int)Math.Min(grown, Array.MaxLength));
            }

            buffer[used++] = b;
        }

        internal void Ascii(string text)
        {
            foreach (char c in text)
            {
                Byte((byte)c);
            }
        }
    }
}
