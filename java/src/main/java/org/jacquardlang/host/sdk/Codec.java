package org.jacquardlang.host.sdk;

import java.lang.reflect.Type;
import java.util.List;
import java.util.Map;
import java.util.Optional;

import org.jacquardlang.host.protocol.Hash;
import org.jacquardlang.host.protocol.Value;

/**
 * Maps one Java type to Jacquard values and back. Codecs are explicit,
 * immutable, and thread-safe; they hold no program state, and encode and
 * decode against the {@link Data} of the program at hand.
 *
 * <p>The constants and factories cover the builtin types, and application
 * records, enums, and sealed hierarchies by their constructors.
 * {@link #of(Class)} derives a codec from a type's declaration: a record
 * component's generic type gives its field codec, so {@code List<Job>} and
 * {@code Optional<String>} components need no further declaration.
 *
 * <p>{@code null} is never a Jacquard value: use {@link Optional} for
 * {@code Option} and {@link #UNIT}, whose Java value is {@code null}, for unit.
 *
 * @param <T> the Java type
 */
public interface Codec<T> {
    /**
     * Encodes {@code value}. {@code encoder} names its location for errors and
     * bounds it by the selected limits.
     *
     * @throws JacquardException an invalid argument
     */
    Value encode(Encoder encoder, T value) throws JacquardException;

    /**
     * Decodes {@code value}.
     *
     * @throws JacquardException a binding failure when the value does not map to {@code T}
     */
    T decode(Decoder decoder, Value value) throws JacquardException;

    /** {@code value} as a function argument encoded with this codec. */
    default Argument<T> arg(T value) {
        return new Argument<>(this, value);
    }

    /** {@code Int} as a {@code long}, Core's full 63-bit range; a {@code long} outside it is refused. */
    Codec<Long> INT = new Codecs.IntCodec();

    /** {@code Int} as an {@code int}; a result that does not fit an {@code int} does not decode. */
    Codec<Integer> INT32 = new Codecs.Int32Codec();

    /** {@code Real} as the exact binary64 bits of a {@code double}, including NaN payloads and signed zero. */
    Codec<Double> REAL = new Codecs.RealCodec();

    /** {@code Text} as a {@code String}; a lone surrogate is refused. */
    Codec<String> TEXT = new Codecs.TextCodec();

    /** {@code Bool} as a {@code boolean}. */
    Codec<Boolean> BOOL = new Codecs.BoolCodec();

    /** {@code Hash} as a protocol {@link Hash}. */
    Codec<Hash> HASH = new Codecs.HashCodec();

    /** Unit, the empty tuple; its Java value is {@code null}. */
    Codec<Void> UNIT = new Codecs.Unit();

    /** A raw protocol {@link Value}, passed through unchanged (within the same bounds). */
    Codec<Value> VALUE = new Codecs.ValueCodec();

    /** Any value by public name, as {@link Dynamic}. */
    Codec<Dynamic> DYNAMIC = new Codecs.DynamicCodec();

    /** {@code List T} as a Java {@link List}. */
    static <T> Codec<List<T>> list(Codec<T> item) {
        return new Codecs.ListCodec<>(item);
    }

    /** {@code Option T} as an {@link Optional}; {@code Optional<Optional<T>>} keeps nested options distinct. */
    static <T> Codec<Optional<T>> optional(Codec<T> inner) {
        return new Codecs.OptionalCodec<>(inner);
    }

    /**
     * A record as one constructor named by the record's simple name (display
     * spelling, such as {@code Job} or {@code TriagePolicy}). Its components,
     * in declaration order, are the constructor's positional fields; their
     * codecs are derived from their generic types as {@link #of(Type)} does.
     *
     * @throws IllegalArgumentException for a component type with no derived codec
     */
    static <R extends Record> Codec<R> record(Class<R> type) {
        return record(type, type.getSimpleName());
    }

    /** {@link #record(Class)} with an explicit constructor name. */
    static <R extends Record> Codec<R> record(Class<R> type, String constructor) {
        return Codecs.record(type, constructor, null);
    }

    /**
     * {@link #record(Class)} with an explicit constructor name and one codec
     * per component, in declaration order.
     *
     * @throws IllegalArgumentException when the codec count differs from the component count
     */
    static <R extends Record> Codec<R> record(Class<R> type, String constructor, Codec<?>... fields) {
        return Codecs.record(type, constructor, List.of(fields));
    }

    /**
     * An enum as nullary constructors named by convention: a constant such as
     * {@code PAGE_NOW} is the constructor {@code page-now} ({@code PageNow}).
     */
    static <E extends Enum<E>> Codec<E> enumeration(Class<E> type) {
        return Codecs.enumeration(type, null);
    }

    /**
     * An enum as nullary constructors with an explicit name for every
     * constant, such as {@code Map.of(Urgency.PAGE, "PageNow")}.
     *
     * @throws IllegalArgumentException when a constant has no name
     */
    static <E extends Enum<E>> Codec<E> enumeration(Class<E> type, Map<E, String> constructors) {
        return Codecs.enumeration(type, Map.copyOf(constructors));
    }

    /**
     * A sealed interface (or sealed class) as the constructors of one data
     * type: each permitted record is a constructor named by its simple name,
     * each permitted enum contributes its constants, and a permitted sealed
     * type contributes its own cases.
     *
     * @throws IllegalArgumentException for a type that is not sealed, or a permitted type that is none of these
     */
    static <S> Codec<S> union(Class<S> type) {
        return Codecs.union(type);
    }

    /**
     * The constructors of one data type from explicit case codecs, each made
     * by {@link #record}, {@link #enumeration}, or {@link #union}.
     *
     * @throws IllegalArgumentException for a codec that is none of these
     */
    static <S> Codec<S> union(Class<S> type, List<Codec<? extends S>> cases) {
        return Codecs.union(type, List.copyOf(cases));
    }

    /**
     * A record as a positional tuple of its components, rather than as a
     * constructor. Component codecs are derived as for {@link #record(Class)}.
     */
    static <R extends Record> Codec<R> tuple(Class<R> type) {
        return Codecs.recordTuple(type);
    }

    /** A positional tuple of items, as a list with one value per codec. */
    static Codec<List<Object>> tuple(Codec<?>... items) {
        return new Codecs.TupleCodec(List.of(items));
    }

    /**
     * The codec a type's declaration implies: {@code long} or {@code Long}
     * ({@link #INT}), {@code int} or {@code Integer} ({@link #INT32}),
     * {@code double} or {@code Double}, {@code boolean} or {@code Boolean},
     * {@code String}, {@link Hash}, {@link Value}, {@link Dynamic},
     * {@code Void} (unit), {@code List<T>}, {@code Optional<T>}, records
     * ({@link #record(Class)}), enums ({@link #enumeration(Class)}), and sealed
     * types ({@link #union(Class)}). Recursive declarations are supported.
     *
     * @throws IllegalArgumentException for any other type
     */
    static <T> Codec<T> of(Class<T> type) {
        @SuppressWarnings("unchecked")
        Codec<T> codec = (Codec<T>) Codecs.derived(type);
        return codec;
    }

    /** {@link #of(Class)} for a generic type, such as a {@code List<Job>} field's. */
    static Codec<?> of(Type type) {
        return Codecs.derive(type);
    }
}
