package org.jacquardlang.host.protocol;

import java.util.List;
import java.util.Objects;

/** One v0 boundary value. */
public sealed interface Value permits Value.Int, Value.Text, Value.Real, Value.HashValue, Value.Tuple, Value.Constructor {
    /** The empty tuple, Jacquard's unit. */
    Tuple UNIT = new Tuple(List.of());

    /**
     * Core's signed 63-bit integer. A {@code long} outside
     * {@code -2^62..2^62-1} can be held but has no encoding (E1604).
     */
    record Int(long value) implements Value {}

    /** A Unicode scalar string; a lone surrogate has no encoding (E1601). */
    record Text(String value) implements Value {
        public Text {
            Objects.requireNonNull(value, "value");
        }
    }

    /** Binary64 bits, preserving NaN payloads and signed zero. */
    record Real(long bits) implements Value {
        /** The bits as a {@code double}. */
        public double toDouble() {
            return Double.longBitsToDouble(bits);
        }
    }

    /** An explicit {@code Hash}. */
    record HashValue(Hash hash) implements Value {
        public HashValue {
            Objects.requireNonNull(hash, "hash");
        }
    }

    /** A positional product; the empty tuple is unit. */
    record Tuple(List<Value> items) implements Value {
        public Tuple {
            items = List.copyOf(items);
        }
    }

    /** A constructor identity and its positional arguments. */
    record Constructor(Hash identity, List<Value> arguments) implements Value {
        public Constructor {
            Objects.requireNonNull(identity, "identity");
            arguments = List.copyOf(arguments);
        }
    }

    /** The {@code Real} with the exact bits of a {@code double}. */
    static Real real(double value) {
        return new Real(Double.doubleToRawLongBits(value));
    }
}
