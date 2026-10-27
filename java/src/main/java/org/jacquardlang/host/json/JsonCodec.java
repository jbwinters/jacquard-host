package org.jacquardlang.host.json;

import java.nio.ByteBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;

import org.jacquardlang.host.ProtocolException;

/**
 * Strict UTF-8 JSON for the carrier: duplicate keys, lone surrogates,
 * non-integer numbers, integers outside Core's 63-bit range, trailing data,
 * and nesting deeper than the selected limit are refused. Encoding is
 * canonical: object keys sorted by code point, no whitespace, and the same
 * string escaping as JavaScript's {@code JSON.stringify}.
 */
public final class JsonCodec {
    private JsonCodec() {}

    /** Decodes one JSON value under the strict rules. */
    public static Json decode(byte[] payload, int maxDepth) throws ProtocolException {
        String text;
        try {
            text = StandardCharsets.UTF_8.newDecoder()
                    .onMalformedInput(CodingErrorAction.REPORT)
                    .onUnmappableCharacter(CodingErrorAction.REPORT)
                    .decode(ByteBuffer.wrap(payload))
                    .toString();
        } catch (CharacterCodingException error) {
            throw ProtocolException.malformed("payload is not valid UTF-8");
        }
        Decoder decoder = new Decoder(text, maxDepth);
        Json value = decoder.value(1);
        decoder.whitespace();
        if (decoder.pos != text.length()) {
            throw ProtocolException.malformed("payload is not one JSON value");
        }
        return value;
    }

    /** Decodes one JSON object. */
    public static Map<String, Json> decodeObject(byte[] payload, int maxDepth) throws ProtocolException {
        if (decode(payload, maxDepth) instanceof Json.Obj object) {
            return object.fields();
        }
        throw ProtocolException.malformed("expected a JSON object");
    }

    private static final class Decoder {
        private final String text;
        private final int maxDepth;
        private int pos;

        Decoder(String text, int maxDepth) {
            this.text = text;
            this.maxDepth = maxDepth;
        }

        private int peek() {
            return pos < text.length() ? text.charAt(pos) : -1;
        }

        void whitespace() {
            while (pos < text.length()) {
                char c = text.charAt(pos);
                if (c != ' ' && c != '\t' && c != '\r' && c != '\n') {
                    return;
                }
                pos++;
            }
        }

        Json value(int depth) throws ProtocolException {
            whitespace();
            int next = peek();
            if (next == '"') {
                return new Json.Str(quoted());
            }
            if (next == '{' || next == '[') {
                if (depth > maxDepth) {
                    throw ProtocolException.exceeded("JSON nesting exceeds max_json_depth");
                }
                return container((char) next, depth);
            }
            if (text.startsWith("true", pos)) {
                pos += 4;
                return new Json.Bool(true);
            }
            if (text.startsWith("false", pos)) {
                pos += 5;
                return new Json.Bool(false);
            }
            if (text.startsWith("null", pos)) {
                pos += 4;
                return Json.NULL;
            }
            int start = pos;
            if (peek() == '-') {
                pos++;
            }
            int first = peek();
            if (first == '0') {
                pos++;
            } else if (first >= '1' && first <= '9') {
                while (peek() >= '0' && peek() <= '9') {
                    pos++;
                }
            } else {
                throw ProtocolException.malformed("invalid JSON scalar");
            }
            String token = text.substring(start, pos);
            if (token.length() > 20) {
                throw ProtocolException.malformed("invalid JSON scalar");
            }
            long number;
            try {
                number = Long.parseLong(token);
            } catch (NumberFormatException error) {
                throw ProtocolException.malformed("JSON integer is outside the 63-bit range");
            }
            if (number < Json.INT_MIN || number > Json.INT_MAX) {
                throw ProtocolException.malformed("JSON integer is outside the 63-bit range");
            }
            return new Json.Int(number);
        }

        private Json container(char open, int depth) throws ProtocolException {
            pos++;
            whitespace();
            char close = open == '{' ? '}' : ']';
            List<Json> items = new ArrayList<>();
            TreeMap<String, Json> fields = new TreeMap<>(Json.CODE_POINT_ORDER);
            if (peek() == close) {
                pos++;
                return finish(open, items, fields);
            }
            while (pos < text.length()) {
                if (open == '[') {
                    items.add(value(depth + 1));
                } else {
                    if (peek() != '"') {
                        throw ProtocolException.malformed("object key must be a string");
                    }
                    String key = quoted();
                    whitespace();
                    if (fields.containsKey(key)) {
                        throw ProtocolException.malformed("duplicate JSON field name");
                    }
                    if (peek() != ':') {
                        throw ProtocolException.malformed("expected a colon");
                    }
                    pos++;
                    fields.put(key, value(depth + 1));
                }
                whitespace();
                if (peek() == close) {
                    pos++;
                    return finish(open, items, fields);
                }
                if (peek() != ',') {
                    throw ProtocolException.malformed("expected a comma");
                }
                pos++;
                whitespace();
            }
            throw ProtocolException.malformed("unterminated JSON container");
        }

        private static Json finish(char open, List<Json> items, TreeMap<String, Json> fields) {
            return open == '{' ? new Json.Obj(fields) : new Json.Arr(items);
        }

        private int hex4() {
            if (pos + 4 > text.length()) {
                return -1;
            }
            int value = 0;
            for (int i = 0; i < 4; i++) {
                int digit = Character.digit(text.charAt(pos + i), 16);
                // Character.digit also accepts non-ASCII digits; JSON does not.
                if (digit < 0 || text.charAt(pos + i) > 'f') {
                    return -1;
                }
                value = value * 16 + digit;
            }
            pos += 4;
            return value;
        }

        private String quoted() throws ProtocolException {
            pos++;
            StringBuilder out = new StringBuilder();
            while (true) {
                if (pos >= text.length()) {
                    throw ProtocolException.malformed("unterminated JSON string");
                }
                char c = text.charAt(pos);
                if (c == '"') {
                    pos++;
                    return out.toString();
                }
                if (c < 0x20) {
                    throw ProtocolException.malformed("invalid JSON string");
                }
                if (c != '\\') {
                    // The payload was validated as UTF-8, so surrogates arrive paired.
                    out.append(c);
                    pos++;
                    continue;
                }
                pos++;
                if (pos >= text.length()) {
                    throw ProtocolException.malformed("unterminated JSON string");
                }
                char escape = text.charAt(pos++);
                switch (escape) {
                    case '"' -> out.append('"');
                    case '\\' -> out.append('\\');
                    case '/' -> out.append('/');
                    case 'b' -> out.append('\b');
                    case 'f' -> out.append('\f');
                    case 'n' -> out.append('\n');
                    case 'r' -> out.append('\r');
                    case 't' -> out.append('\t');
                    case 'u' -> {
                        int unit = hex4();
                        if (unit < 0) {
                            throw ProtocolException.malformed("invalid JSON string");
                        }
                        if (unit >= 0xd800 && unit < 0xdc00) {
                            if (!text.startsWith("\\u", pos)) {
                                throw ProtocolException.malformed("a JSON string contains a lone surrogate");
                            }
                            pos += 2;
                            int low = hex4();
                            if (low < 0) {
                                throw ProtocolException.malformed("invalid JSON string");
                            }
                            if (low < 0xdc00 || low >= 0xe000) {
                                throw ProtocolException.malformed("a JSON string contains a lone surrogate");
                            }
                            out.append((char) unit).append((char) low);
                        } else if (unit >= 0xdc00 && unit < 0xe000) {
                            throw ProtocolException.malformed("a JSON string contains a lone surrogate");
                        } else {
                            out.append((char) unit);
                        }
                    }
                    default -> throw ProtocolException.malformed("invalid JSON string");
                }
            }
        }
    }

    /** Encodes a JSON value canonically. */
    public static String stringify(Json value, int maxDepth) throws ProtocolException {
        Sink out = new Sink(Long.MAX_VALUE);
        write(out, value, maxDepth, 1);
        return out.text.toString();
    }

    /**
     * The encoding so far and its UTF-8 length. Every append is counted, and
     * the encoding stops once it passes {@code limit}, so a value whose
     * encoding is far larger than a frame (one long text referenced many
     * times, say) costs at most about one frame of memory and time.
     */
    private static final class Sink {
        final StringBuilder text = new StringBuilder();
        final long limit;
        long bytes;

        Sink(long limit) {
            this.limit = limit;
        }

        /** Appends ASCII. */
        Sink append(String ascii) throws ProtocolException {
            text.append(ascii);
            return count(ascii.length());
        }

        Sink append(char c) throws ProtocolException {
            text.append(c);
            // A surrogate pair is four UTF-8 bytes: two for each half.
            return count(c < 0x80 ? 1 : c < 0x800 || Character.isSurrogate(c) ? 2 : 3);
        }

        private Sink count(int length) throws ProtocolException {
            bytes += length;
            if (bytes > limit) {
                throw ProtocolException.exceeded("payload exceeds max_frame_bytes");
            }
            return this;
        }
    }

    private static final char[] HEX = "0123456789abcdef".toCharArray();

    private static void write(Sink out, Json value, int maxDepth, int depth) throws ProtocolException {
        switch (value) {
            case Json.Null ignored -> out.append("null");
            case Json.Bool flag -> out.append(flag.value() ? "true" : "false");
            case Json.Int number -> {
                if (number.value() < Json.INT_MIN || number.value() > Json.INT_MAX) {
                    throw ProtocolException.malformed("JSON integer is outside the 63-bit range");
                }
                out.append(Long.toString(number.value()));
            }
            case Json.Str text -> quote(out, text.value());
            case Json.Arr array -> {
                if (depth > maxDepth) {
                    throw ProtocolException.exceeded("JSON nesting exceeds max_json_depth");
                }
                out.append('[');
                boolean first = true;
                for (Json item : array.items()) {
                    if (!first) {
                        out.append(',');
                    }
                    first = false;
                    write(out, item, maxDepth, depth + 1);
                }
                out.append(']');
            }
            case Json.Obj object -> {
                if (depth > maxDepth) {
                    throw ProtocolException.exceeded("JSON nesting exceeds max_json_depth");
                }
                out.append('{');
                boolean first = true;
                for (Map.Entry<String, Json> entry : object.fields().entrySet()) {
                    if (!first) {
                        out.append(',');
                    }
                    first = false;
                    quote(out, entry.getKey());
                    out.append(':');
                    write(out, entry.getValue(), maxDepth, depth + 1);
                }
                out.append('}');
            }
        }
    }

    /** Whether {@code text} is a sequence of Unicode scalars (no lone surrogate). */
    public static boolean isScalar(String text) {
        for (int i = 0; i < text.length(); i++) {
            char c = text.charAt(i);
            if (Character.isHighSurrogate(c)) {
                if (i + 1 >= text.length() || !Character.isLowSurrogate(text.charAt(i + 1))) {
                    return false;
                }
                i++;
            } else if (Character.isLowSurrogate(c)) {
                return false;
            }
        }
        return true;
    }

    /** The UTF-8 length of a scalar string. */
    public static int utf8Length(String text) {
        int length = 0;
        for (int i = 0; i < text.length(); i++) {
            char c = text.charAt(i);
            if (c < 0x80) {
                length += 1;
            } else if (c < 0x800) {
                length += 2;
            } else if (Character.isHighSurrogate(c) && i + 1 < text.length() && Character.isLowSurrogate(text.charAt(i + 1))) {
                length += 4;
                i++;
            } else {
                length += 3;
            }
        }
        return length;
    }

    private static void quote(Sink out, String text) throws ProtocolException {
        if (!isScalar(text)) {
            throw ProtocolException.malformed("a JSON string contains a lone surrogate");
        }
        out.append('"');
        for (int i = 0; i < text.length(); i++) {
            char c = text.charAt(i);
            switch (c) {
                case '"' -> out.append("\\\"");
                case '\\' -> out.append("\\\\");
                case '\b' -> out.append("\\b");
                case '\f' -> out.append("\\f");
                case '\n' -> out.append("\\n");
                case '\r' -> out.append("\\r");
                case '\t' -> out.append("\\t");
                default -> {
                    if (c < 0x20) {
                        out.append("\\u00").append(HEX[c >> 4]).append(HEX[c & 0xf]);
                    } else {
                        out.append(c);
                    }
                }
            }
        }
        out.append('"');
    }

    /**
     * Encodes one carrier frame: a big-endian u32 length and the payload,
     * refusing payloads outside {@code 1..=maxBytes}.
     */
    public static byte[] frameBytes(Map<String, Json> frame, int maxBytes, int maxDepth) throws ProtocolException {
        Sink out = new Sink(maxBytes);
        write(out, new Json.Obj(frame), maxDepth, 1);
        byte[] payload = out.text.toString().getBytes(StandardCharsets.UTF_8);
        if (payload.length == 0 || payload.length > maxBytes) {
            throw ProtocolException.exceeded("payload exceeds max_frame_bytes");
        }
        return ByteBuffer.allocate(4 + payload.length).putInt(payload.length).put(payload).array();
    }
}
