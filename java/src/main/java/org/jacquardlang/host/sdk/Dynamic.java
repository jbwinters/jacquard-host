package org.jacquardlang.host.sdk;

import java.util.Arrays;
import java.util.Objects;

import org.jacquardlang.host.protocol.Hash;

/**
 * A native value with no application type: Jacquard data by public name.
 * Decode any result into it with {@link Codec#DYNAMIC} or
 * {@link Invocation#value()}.
 */
public sealed interface Dynamic
        permits Dynamic.Int, Dynamic.Real, Dynamic.Text, Dynamic.Bool, Dynamic.HashValue, Dynamic.Tuple, Dynamic.List, Dynamic.Option,
        Dynamic.Variant {
    /** Unit, the empty tuple. */
    Tuple UNIT = new Tuple(java.util.List.of());

    /** A Jacquard {@code Int}. */
    record Int(long value) implements Dynamic {}

    /** A Jacquard {@code Real}; equality compares the exact bits. */
    record Real(double value) implements Dynamic {
        @Override
        public boolean equals(Object other) {
            return other instanceof Real real && Double.doubleToRawLongBits(real.value) == Double.doubleToRawLongBits(value);
        }

        @Override
        public int hashCode() {
            return Long.hashCode(Double.doubleToRawLongBits(value));
        }
    }

    /** A Jacquard {@code Text}. */
    record Text(String value) implements Dynamic {
        public Text {
            Objects.requireNonNull(value, "value");
        }
    }

    /** A Jacquard {@code Bool}. */
    record Bool(boolean value) implements Dynamic {}

    /** A Jacquard {@code Hash}. */
    record HashValue(Hash value) implements Dynamic {
        public HashValue {
            Objects.requireNonNull(value, "value");
        }
    }

    /** A positional product; the empty tuple is unit. */
    record Tuple(java.util.List<Dynamic> items) implements Dynamic {
        public Tuple {
            items = java.util.List.copyOf(items);
        }
    }

    /** A Jacquard {@code List}. */
    record List(java.util.List<Dynamic> items) implements Dynamic {
        public List {
            items = java.util.List.copyOf(items);
        }
    }

    /** A Jacquard {@code Option}: {@code value} is null for {@code None}. */
    record Option(Dynamic value) implements Dynamic {}

    /**
     * A constructor by public name (store spelling such as {@code stock}, or
     * display spelling such as {@code Stock}) and its positional fields.
     */
    record Variant(String name, java.util.List<Dynamic> fields) implements Dynamic {
        public Variant {
            Objects.requireNonNull(name, "name");
            fields = java.util.List.copyOf(fields);
        }
    }

    /** An {@code Int}. */
    static Dynamic of(long value) {
        return new Int(value);
    }

    /** A {@code Real}. */
    static Dynamic of(double value) {
        return new Real(value);
    }

    /** A {@code Text}. */
    static Dynamic of(String value) {
        return new Text(value);
    }

    /** A {@code Bool}. */
    static Dynamic of(boolean value) {
        return new Bool(value);
    }

    /** A {@code List}. */
    static Dynamic list(Dynamic... items) {
        return new List(Arrays.asList(items));
    }

    /** A tuple. */
    static Dynamic tuple(Dynamic... items) {
        return new Tuple(Arrays.asList(items));
    }

    /** {@code Some(value)}. */
    static Dynamic some(Dynamic value) {
        return new Option(Objects.requireNonNull(value, "value"));
    }

    /** {@code None}. */
    static Dynamic none() {
        return new Option(null);
    }

    /** A constructor value by public name; {@link Data#make} also checks that the constructor exists. */
    static Dynamic variant(String name, Dynamic... fields) {
        return new Variant(name, Arrays.asList(fields));
    }
}
