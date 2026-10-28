package org.jacquardlang.host.json;

import java.util.Collections;
import java.util.Comparator;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.SortedMap;
import java.util.TreeMap;

/**
 * A decoded JSON value. Values are immutable; objects keep their keys in
 * code-point order, the order the canonical encoding uses.
 */
public sealed interface Json permits Json.Null, Json.Bool, Json.Int, Json.Str, Json.Arr, Json.Obj {
    /** The smallest Core {@code Int} and JSON integer: -2^62. */
    long INT_MIN = -(1L << 62);
    /** The largest Core {@code Int} and JSON integer: 2^62 - 1. */
    long INT_MAX = (1L << 62) - 1;

    /** JSON {@code null}. */
    Null NULL = new Null();

    /** Orders strings by Unicode code point, as Rust, Go, and Python do. */
    Comparator<String> CODE_POINT_ORDER = (a, b) -> {
        int i = 0;
        int j = 0;
        while (i < a.length() && j < b.length()) {
            int x = a.codePointAt(i);
            int y = b.codePointAt(j);
            if (x != y) {
                return Integer.compare(x, y);
            }
            i += Character.charCount(x);
            j += Character.charCount(y);
        }
        return Boolean.compare(i < a.length(), j < b.length());
    };

    /** JSON {@code null}. */
    record Null() implements Json {
        @Override
        public String toString() {
            return "null";
        }
    }

    /** A JSON boolean. */
    record Bool(boolean value) implements Json {}

    /** A JSON integer within Core's 63-bit range. */
    record Int(long value) implements Json {}

    /** A JSON string. */
    record Str(String value) implements Json {
        public Str {
            if (value == null) {
                throw new NullPointerException("value");
            }
        }
    }

    /** A JSON array. */
    record Arr(List<Json> items) implements Json {
        public Arr {
            items = List.copyOf(items);
        }
    }

    /** A JSON object, keys in code-point order. */
    record Obj(Map<String, Json> fields) implements Json {
        public Obj {
            fields = sorted(fields);
        }
    }

    /** A string value. */
    static Json of(String value) {
        return new Str(value);
    }

    /** An integer value. */
    static Json of(long value) {
        return new Int(value);
    }

    /** A boolean value. */
    static Json of(boolean value) {
        return new Bool(value);
    }

    /** An array value. */
    static Json of(List<Json> items) {
        return new Arr(items);
    }

    /** An object value. */
    static Json of(Map<String, Json> fields) {
        return new Obj(fields);
    }

    /**
     * Builds an immutable object from alternating keys and values, such as
     * {@code object("kind", Json.of("int"), "value", Json.of("1"))}.
     */
    static Map<String, Json> object(Object... pairs) {
        if (pairs.length % 2 != 0) {
            throw new IllegalArgumentException("object needs key/value pairs");
        }
        TreeMap<String, Json> fields = new TreeMap<>(CODE_POINT_ORDER);
        for (int i = 0; i < pairs.length; i += 2) {
            fields.put((String) pairs[i], (Json) pairs[i + 1]);
        }
        return Collections.unmodifiableSortedMap(fields);
    }

    /** An immutable copy of {@code fields} in code-point order. */
    static SortedMap<String, Json> sorted(Map<String, Json> fields) {
        TreeMap<String, Json> copy = new TreeMap<>(CODE_POINT_ORDER);
        for (Map.Entry<String, Json> entry : fields.entrySet()) {
            copy.put(Objects.requireNonNull(entry.getKey()), Objects.requireNonNull(entry.getValue()));
        }
        return Collections.unmodifiableSortedMap(copy);
    }

    /** The object's fields, or {@code null} if this is not an object. */
    default Map<String, Json> asObject() {
        return this instanceof Obj object ? object.fields() : null;
    }

    /** The string, or {@code null} if this is not a string. */
    default String asString() {
        return this instanceof Str text ? text.value() : null;
    }

    /** The items, or {@code null} if this is not an array. */
    default List<Json> asArray() {
        return this instanceof Arr array ? array.items() : null;
    }

    /** The integer, or {@code null} if this is not an integer. */
    default Long asInt() {
        return this instanceof Int number ? number.value() : null;
    }
}
