package org.jacquardlang.host.sdk;

import java.util.Objects;

/**
 * A function argument with the codec that encodes it, made by
 * {@link Codec#arg}. Pass one where a value's runtime class does not say how
 * to encode it, such as an empty list of records or a record as a tuple.
 *
 * @param codec encodes the value
 * @param value the value
 */
public record Argument<T>(Codec<T> codec, T value) {
    public Argument {
        Objects.requireNonNull(codec, "codec");
    }
}
