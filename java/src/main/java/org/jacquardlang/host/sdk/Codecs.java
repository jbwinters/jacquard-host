package org.jacquardlang.host.sdk;

import java.lang.reflect.Constructor;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.lang.reflect.ParameterizedType;
import java.lang.reflect.RecordComponent;
import java.lang.reflect.Type;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Optional;

import org.jacquardlang.host.protocol.Hash;
import org.jacquardlang.host.protocol.Value;

/** The codec implementations behind {@link Codec}'s constants and factories. */
final class Codecs {
    private Codecs() {}

    // Builtin types.

    static final class IntCodec implements Codec<Long> {
        @Override
        public Value encode(Encoder encoder, Long value) throws JacquardException {
            return encoder.integer(value);
        }

        @Override
        public Long decode(Decoder decoder, Value value) throws JacquardException {
            if (value instanceof Value.Int number) {
                return number.value();
            }
            throw decoder.mapping("expected Int");
        }
    }

    static final class Int32Codec implements Codec<Integer> {
        @Override
        public Value encode(Encoder encoder, Integer value) throws JacquardException {
            return encoder.integer(value);
        }

        @Override
        public Integer decode(Decoder decoder, Value value) throws JacquardException {
            if (value instanceof Value.Int number && number.value() >= Integer.MIN_VALUE && number.value() <= Integer.MAX_VALUE) {
                return (int) number.value();
            }
            throw decoder.mapping("expected Int that fits int");
        }
    }

    static final class RealCodec implements Codec<Double> {
        @Override
        public Value encode(Encoder encoder, Double value) throws JacquardException {
            return encoder.real(value);
        }

        @Override
        public Double decode(Decoder decoder, Value value) throws JacquardException {
            if (value instanceof Value.Real real) {
                return real.toDouble();
            }
            throw decoder.mapping("expected Real");
        }
    }

    static final class TextCodec implements Codec<String> {
        @Override
        public Value encode(Encoder encoder, String value) throws JacquardException {
            return encoder.text(value);
        }

        @Override
        public String decode(Decoder decoder, Value value) throws JacquardException {
            if (value instanceof Value.Text text) {
                return text.value();
            }
            throw decoder.mapping("expected Text");
        }
    }

    static final class BoolCodec implements Codec<Boolean> {
        @Override
        public Value encode(Encoder encoder, Boolean value) throws JacquardException {
            return encoder.bool(value);
        }

        @Override
        public Boolean decode(Decoder decoder, Value value) throws JacquardException {
            if (decoder.data().isNullary(value, "true")) {
                return true;
            }
            if (decoder.data().isNullary(value, "false")) {
                return false;
            }
            throw decoder.mapping("expected Bool");
        }
    }

    static final class HashCodec implements Codec<Hash> {
        @Override
        public Value encode(Encoder encoder, Hash value) throws JacquardException {
            return encoder.hash(value);
        }

        @Override
        public Hash decode(Decoder decoder, Value value) throws JacquardException {
            if (value instanceof Value.HashValue hash) {
                return hash.hash();
            }
            throw decoder.mapping("expected Hash");
        }
    }

    static final class Unit implements Codec<Void> {
        @Override
        public Value encode(Encoder encoder, Void value) throws JacquardException {
            return encoder.tuple(List.of());
        }

        @Override
        public Void decode(Decoder decoder, Value value) throws JacquardException {
            if (value instanceof Value.Tuple tuple && tuple.items().isEmpty()) {
                return null;
            }
            throw decoder.mapping("expected unit");
        }
    }

    static final class ValueCodec implements Codec<Value> {
        @Override
        public Value encode(Encoder encoder, Value value) throws JacquardException {
            return encoder.raw(value);
        }

        @Override
        public Value decode(Decoder decoder, Value value) {
            return value;
        }
    }

    static final class ListCodec<T> implements Codec<List<T>> {
        private final Codec<T> item;

        ListCodec(Codec<T> item) {
            this.item = java.util.Objects.requireNonNull(item, "item");
        }

        @Override
        public Value encode(Encoder encoder, List<T> value) throws JacquardException {
            return encoder.list(item, value);
        }

        @Override
        public List<T> decode(Decoder decoder, Value value) throws JacquardException {
            List<T> out = new ArrayList<>();
            for (Value element : decoder.data().listItems(value)) {
                out.add(item.decode(decoder, element));
            }
            return out;
        }
    }

    static final class OptionalCodec<T> implements Codec<Optional<T>> {
        private final Codec<T> inner;

        OptionalCodec(Codec<T> inner) {
            this.inner = java.util.Objects.requireNonNull(inner, "inner");
        }

        @Override
        public Value encode(Encoder encoder, Optional<T> value) throws JacquardException {
            return encoder.option(inner, value.orElse(null));
        }

        @Override
        public Optional<T> decode(Decoder decoder, Value value) throws JacquardException {
            Data data = decoder.data();
            if (data.isNullary(value, "none")) {
                return Optional.empty();
            }
            if (value instanceof Value.Constructor some && some.arguments().size() == 1 && "some".equals(data.label(some.identity()))) {
                T decoded = inner.decode(decoder, some.arguments().get(0));
                if (decoded == null) {
                    throw decoder.mapping("Some of unit has no Optional form; decode it with Codec.DYNAMIC");
                }
                return Optional.of(decoded);
            }
            throw decoder.mapping("expected Option");
        }
    }

    static final class TupleCodec implements Codec<List<Object>> {
        private final List<Codec<?>> items;

        TupleCodec(List<Codec<?>> items) {
            this.items = List.copyOf(items);
        }

        @Override
        public Value encode(Encoder encoder, List<Object> value) throws JacquardException {
            if (value.size() != items.size()) {
                throw encoder.invalid("expected a " + items.size() + "-item tuple, found " + value.size() + " items");
            }
            encoder.checkCount(value.size(), "tuple");
            List<Value> encoded = new ArrayList<>();
            for (int i = 0; i < items.size(); i++) {
                encoded.add(item(encoder, i, items.get(i), value.get(i)));
            }
            return encoder.tuple(encoded);
        }

        @Override
        public List<Object> decode(Decoder decoder, Value value) throws JacquardException {
            if (!(value instanceof Value.Tuple tuple) || tuple.items().size() != items.size()) {
                throw decoder.mapping("tuple shape mismatch");
            }
            List<Object> out = new ArrayList<>();
            for (int i = 0; i < items.size(); i++) {
                out.add(items.get(i).decode(decoder, tuple.items().get(i)));
            }
            return out;
        }
    }

    @SuppressWarnings("unchecked")
    private static <T> Value item(Encoder encoder, int index, Codec<T> codec, Object value) throws JacquardException {
        return encoder.item(index, codec, (T) value);
    }

    @SuppressWarnings("unchecked")
    private static <T> Value field(Encoder encoder, String name, Codec<T> codec, Object value) throws JacquardException {
        return encoder.field(name, codec, (T) value);
    }

    // Dynamic values.

    static final class DynamicCodec implements Codec<Dynamic> {
        @Override
        public Value encode(Encoder encoder, Dynamic value) throws JacquardException {
            return switch (value) {
                case Dynamic.Int number -> encoder.integer(number.value());
                case Dynamic.Real real -> encoder.real(real.value());
                case Dynamic.Text text -> encoder.text(text.value());
                case Dynamic.Bool bool -> encoder.bool(bool.value());
                case Dynamic.HashValue hash -> encoder.hash(hash.value());
                case Dynamic.Tuple tuple -> {
                    encoder.checkCount(tuple.items().size(), "tuple");
                    List<Value> items = new ArrayList<>();
                    for (int i = 0; i < tuple.items().size(); i++) {
                        items.add(encoder.item(i, this, tuple.items().get(i)));
                    }
                    yield encoder.tuple(items);
                }
                case Dynamic.List list -> encoder.list(this, list.items());
                case Dynamic.Option option -> encoder.option(this, option.value());
                case Dynamic.Variant variant -> {
                    encoder.checkCount(variant.fields().size(), "constructor");
                    List<Value> fields = new ArrayList<>();
                    for (int i = 0; i < variant.fields().size(); i++) {
                        fields.add(encoder.field(String.valueOf(i), this, variant.fields().get(i)));
                    }
                    yield encoder.constructor(variant.name(), fields);
                }
            };
        }

        @Override
        public Dynamic decode(Decoder decoder, Value value) throws JacquardException {
            return switch (value) {
                case Value.Int number -> new Dynamic.Int(number.value());
                case Value.Real real -> new Dynamic.Real(real.toDouble());
                case Value.Text text -> new Dynamic.Text(text.value());
                case Value.HashValue hash -> new Dynamic.HashValue(hash.hash());
                case Value.Tuple tuple -> {
                    List<Dynamic> items = new ArrayList<>();
                    for (Value item : tuple.items()) {
                        items.add(decode(decoder, item));
                    }
                    yield new Dynamic.Tuple(items);
                }
                case Value.Constructor constructor -> {
                    String label = decoder.data().label(constructor.identity());
                    if (label == null) {
                        throw decoder.mapping("unmapped constructor " + constructor.identity());
                    }
                    yield switch (label) {
                        case "true", "false" -> new Dynamic.Bool(Codec.BOOL.decode(decoder, value));
                        case "cons", "nil" -> new Dynamic.List(Codec.list(this).decode(decoder, value));
                        case "some", "none" -> {
                            Optional<Dynamic> inner = Codec.optional(this).decode(decoder, value);
                            yield new Dynamic.Option(inner.orElse(null));
                        }
                        default -> {
                            List<Dynamic> fields = new ArrayList<>();
                            for (Value field : constructor.arguments()) {
                                fields.add(decode(decoder, field));
                            }
                            yield new Dynamic.Variant(label, fields);
                        }
                    };
                }
            };
        }
    }

    // Application types.

    /** A codec for some of a data type's constructors, usable as a union case. */
    interface Cases<T> extends Codec<T> {
        /** The Java type this case encodes. */
        Class<?> javaType();

        /** Whether {@code value} is built by one of this case's constructors. */
        boolean decodes(Decoder decoder, Value value);
    }

    private static final class RecordCodec<R extends Record> implements Cases<R> {
        private final Class<R> type;
        private final String constructor;
        private final RecordComponent[] components;
        private final Method[] accessors;
        private final Constructor<R> canonical;
        private final List<Codec<?>> fields;

        RecordCodec(Class<R> type, String constructor, RecordComponent[] components, Method[] accessors, Constructor<R> canonical,
                List<Codec<?>> fields) {
            this.type = type;
            this.constructor = constructor;
            this.components = components;
            this.accessors = accessors;
            this.canonical = canonical;
            this.fields = fields;
        }

        @Override
        public Class<?> javaType() {
            return type;
        }

        @Override
        public boolean decodes(Decoder decoder, Value value) {
            return decoder.is(value, constructor);
        }

        @Override
        public Value encode(Encoder encoder, R value) throws JacquardException {
            // Counted before any component is read, as for a tuple.
            encoder.checkCount(components.length, "constructor");
            List<Value> encoded = new ArrayList<>(components.length);
            for (int i = 0; i < components.length; i++) {
                encoded.add(field(encoder, components[i].getName(), fields.get(i), component(encoder, value, i)));
            }
            return encoder.constructor(constructor, encoded);
        }

        private Object component(Encoder encoder, R value, int index) throws JacquardException {
            try {
                return accessors[index].invoke(value);
            } catch (IllegalAccessException | InvocationTargetException error) {
                throw encoder.invalid("cannot read " + type.getSimpleName() + "." + components[index].getName());
            }
        }

        @Override
        public R decode(Decoder decoder, Value value) throws JacquardException {
            List<Value> values = decoder.fields(value, constructor, components.length);
            Object[] arguments = new Object[components.length];
            for (int i = 0; i < components.length; i++) {
                arguments[i] = fields.get(i).decode(decoder, values.get(i));
            }
            return construct(decoder, type, canonical, arguments);
        }
    }

    private static <R> R construct(Decoder decoder, Class<R> type, Constructor<R> canonical, Object[] arguments) throws JacquardException {
        try {
            return canonical.newInstance(arguments);
        } catch (InvocationTargetException error) {
            throw decoder.mapping(type.getSimpleName() + " refused the decoded fields: " + error.getCause());
        } catch (ReflectiveOperationException | IllegalArgumentException error) {
            throw decoder.mapping("cannot construct " + type.getSimpleName() + ": " + error);
        }
    }

    /** A record's components, accessors, and canonical constructor, made accessible. */
    private record Shape<R>(RecordComponent[] components, Method[] accessors, Constructor<R> canonical) {
        static <R extends Record> Shape<R> of(Class<R> type) {
            RecordComponent[] components = type.getRecordComponents();
            if (components == null) {
                throw new IllegalArgumentException(type.getName() + " is not a record");
            }
            Method[] accessors = new Method[components.length];
            Class<?>[] types = new Class<?>[components.length];
            for (int i = 0; i < components.length; i++) {
                accessors[i] = components[i].getAccessor();
                types[i] = components[i].getType();
            }
            try {
                Constructor<R> canonical = type.getDeclaredConstructor(types);
                canonical.setAccessible(true);
                for (Method accessor : accessors) {
                    accessor.setAccessible(true);
                }
                return new Shape<>(components, accessors, canonical);
            } catch (NoSuchMethodException | RuntimeException error) {
                throw new IllegalArgumentException("record " + type.getName() + " is not accessible: " + error.getMessage(), error);
            }
        }
    }

    static <R extends Record> Codec<R> record(Class<R> type, String constructor, List<Codec<?>> explicit) {
        return record(type, constructor, explicit, new HashMap<>());
    }

    private static <R extends Record> RecordCodec<R> record(Class<R> type, String constructor, List<Codec<?>> explicit,
            Map<Class<?>, Lazy> seen) {
        java.util.Objects.requireNonNull(constructor, "constructor");
        Shape<R> shape = Shape.of(type);
        List<Codec<?>> fields;
        if (explicit != null) {
            if (explicit.size() != shape.components().length) {
                throw new IllegalArgumentException(type.getSimpleName() + " has " + shape.components().length + " components, but "
                        + explicit.size() + " codecs were given");
            }
            fields = List.copyOf(explicit);
        } else {
            fields = new ArrayList<>();
            for (RecordComponent component : shape.components()) {
                fields.add(derive(component.getGenericType(), seen));
            }
        }
        return new RecordCodec<>(type, constructor, shape.components(), shape.accessors(), shape.canonical(), fields);
    }

    private static final class RecordTupleCodec<R extends Record> implements Codec<R> {
        private final Class<R> type;
        private final Shape<R> shape;
        private final List<Codec<?>> items;

        RecordTupleCodec(Class<R> type, Shape<R> shape, List<Codec<?>> items) {
            this.type = type;
            this.shape = shape;
            this.items = items;
        }

        @Override
        public Value encode(Encoder encoder, R value) throws JacquardException {
            encoder.checkCount(items.size(), "tuple");
            List<Value> encoded = new ArrayList<>();
            for (int i = 0; i < items.size(); i++) {
                Object component;
                try {
                    component = shape.accessors()[i].invoke(value);
                } catch (IllegalAccessException | InvocationTargetException error) {
                    throw encoder.invalid("cannot read " + type.getSimpleName() + "." + shape.components()[i].getName());
                }
                encoded.add(item(encoder, i, items.get(i), component));
            }
            return encoder.tuple(encoded);
        }

        @Override
        public R decode(Decoder decoder, Value value) throws JacquardException {
            if (!(value instanceof Value.Tuple tuple) || tuple.items().size() != items.size()) {
                throw decoder.mapping("tuple shape mismatch");
            }
            Object[] arguments = new Object[items.size()];
            for (int i = 0; i < items.size(); i++) {
                arguments[i] = items.get(i).decode(decoder, tuple.items().get(i));
            }
            return construct(decoder, type, shape.canonical(), arguments);
        }
    }

    static <R extends Record> Codec<R> recordTuple(Class<R> type) {
        Shape<R> shape = Shape.of(type);
        Map<Class<?>, Lazy> seen = new HashMap<>();
        List<Codec<?>> items = new ArrayList<>();
        for (RecordComponent component : shape.components()) {
            items.add(derive(component.getGenericType(), seen));
        }
        return new RecordTupleCodec<>(type, shape, items);
    }

    private static final class EnumCodec<E extends Enum<E>> implements Cases<E> {
        private final Class<E> type;
        private final Map<E, String> names;

        EnumCodec(Class<E> type, Map<E, String> names) {
            this.type = type;
            this.names = names;
        }

        @Override
        public Class<?> javaType() {
            return type;
        }

        @Override
        public boolean decodes(Decoder decoder, Value value) {
            for (String name : names.values()) {
                if (decoder.is(value, name)) {
                    return true;
                }
            }
            return false;
        }

        @Override
        public Value encode(Encoder encoder, E value) throws JacquardException {
            return encoder.constructor(names.get(value), List.of());
        }

        @Override
        public E decode(Decoder decoder, Value value) throws JacquardException {
            for (Map.Entry<E, String> entry : names.entrySet()) {
                if (decoder.is(value, entry.getValue())) {
                    decoder.fields(value, entry.getValue(), 0);
                    return entry.getKey();
                }
            }
            throw decoder.mismatch(type.getSimpleName());
        }
    }

    /** The constructor a constant names by convention: {@code PAGE_NOW} is {@code page-now}. */
    static String constantName(Enum<?> constant) {
        return constant.name().toLowerCase(Locale.ROOT).replace('_', '-');
    }

    static <E extends Enum<E>> Codec<E> enumeration(Class<E> type, Map<E, String> explicit) {
        LinkedHashMap<E, String> names = new LinkedHashMap<>();
        for (E constant : type.getEnumConstants()) {
            String name = explicit == null ? constantName(constant) : explicit.get(constant);
            if (name == null) {
                throw new IllegalArgumentException(type.getSimpleName() + "." + constant.name() + " has no constructor name");
            }
            names.put(constant, name);
        }
        return new EnumCodec<>(type, names);
    }

    private static final class UnionCodec<S> implements Cases<S> {
        private final Class<S> type;
        private final List<Cases<?>> cases;

        UnionCodec(Class<S> type, List<Cases<?>> cases) {
            this.type = type;
            this.cases = List.copyOf(cases);
        }

        @Override
        public Class<?> javaType() {
            return type;
        }

        @Override
        public boolean decodes(Decoder decoder, Value value) {
            for (Cases<?> each : cases) {
                if (each.decodes(decoder, value)) {
                    return true;
                }
            }
            return false;
        }

        @Override
        public Value encode(Encoder encoder, S value) throws JacquardException {
            for (Cases<?> each : cases) {
                if (each.javaType().isInstance(value)) {
                    return encodeCase(encoder, each, value);
                }
            }
            throw encoder.invalid(type.getSimpleName() + " has no case for " + value.getClass().getName());
        }

        @Override
        public S decode(Decoder decoder, Value value) throws JacquardException {
            for (Cases<?> each : cases) {
                if (each.decodes(decoder, value)) {
                    return type.cast(each.decode(decoder, value));
                }
            }
            throw decoder.mismatch(type.getSimpleName());
        }
    }

    @SuppressWarnings("unchecked")
    private static <T> Value encodeCase(Encoder encoder, Cases<T> codec, Object value) throws JacquardException {
        return codec.encode(encoder, (T) value);
    }

    static <S> Codec<S> union(Class<S> type) {
        return union(type, new HashMap<>());
    }

    private static <S> UnionCodec<S> union(Class<S> type, Map<Class<?>, Lazy> seen) {
        if (!type.isSealed()) {
            throw new IllegalArgumentException(type.getName() + " is not sealed; pass its cases explicitly");
        }
        List<Cases<?>> cases = new ArrayList<>();
        for (Class<?> permitted : type.getPermittedSubclasses()) {
            if (!(derive(permitted, seen) instanceof Cases<?> each)) {
                throw new IllegalArgumentException(permitted.getName() + " is not a record, enum, or sealed type");
            }
            cases.add(each);
        }
        return new UnionCodec<>(type, cases);
    }

    static <S> Codec<S> union(Class<S> type, List<Codec<? extends S>> explicit) {
        List<Cases<?>> cases = new ArrayList<>();
        for (Codec<? extends S> each : explicit) {
            if (!(each instanceof Cases<?> known)) {
                throw new IllegalArgumentException("a union case must be a record, enumeration, or union codec");
            }
            cases.add(known);
        }
        return new UnionCodec<>(type, cases);
    }

    // Derivation from declared types.

    /** A codec still being derived, for recursive declarations. */
    private static final class Lazy implements Cases<Object> {
        private Cases<Object> target;

        @Override
        public Class<?> javaType() {
            return target.javaType();
        }

        @Override
        public boolean decodes(Decoder decoder, Value value) {
            return target.decodes(decoder, value);
        }

        @Override
        public Value encode(Encoder encoder, Object value) throws JacquardException {
            return target.encode(encoder, value);
        }

        @Override
        public Object decode(Decoder decoder, Value value) throws JacquardException {
            return target.decode(decoder, value);
        }
    }

    private static final ClassValue<Codec<?>> DERIVED = new ClassValue<>() {
        @Override
        protected Codec<?> computeValue(Class<?> type) {
            return derive(type, new HashMap<>());
        }
    };

    /** The derived codec for a class, cached per class. */
    static Codec<?> derived(Class<?> type) {
        return DERIVED.get(type);
    }

    static Codec<?> derive(Type type) {
        return type instanceof Class<?> plain ? derived(plain) : derive(type, new HashMap<>());
    }

    @SuppressWarnings({"unchecked", "rawtypes"})
    private static Codec<?> derive(Type type, Map<Class<?>, Lazy> seen) {
        if (type instanceof ParameterizedType parameterized && parameterized.getRawType() instanceof Class<?> raw) {
            Type[] arguments = parameterized.getActualTypeArguments();
            if (raw == List.class) {
                return Codec.list(derive(arguments[0], seen));
            }
            if (raw == Optional.class) {
                return Codec.optional(derive(arguments[0], seen));
            }
            throw new IllegalArgumentException("no Jacquard mapping for " + type.getTypeName());
        }
        if (!(type instanceof Class<?> plain)) {
            throw new IllegalArgumentException("no Jacquard mapping for " + type.getTypeName() + "; use a concrete type");
        }
        if (plain == long.class || plain == Long.class) {
            return Codec.INT;
        }
        if (plain == int.class || plain == Integer.class) {
            return Codec.INT32;
        }
        if (plain == double.class || plain == Double.class) {
            return Codec.REAL;
        }
        if (plain == boolean.class || plain == Boolean.class) {
            return Codec.BOOL;
        }
        if (plain == String.class) {
            return Codec.TEXT;
        }
        if (plain == Hash.class) {
            return Codec.HASH;
        }
        if (plain == Value.class) {
            return Codec.VALUE;
        }
        if (plain == Dynamic.class) {
            return Codec.DYNAMIC;
        }
        if (plain == Void.class || plain == void.class) {
            return Codec.UNIT;
        }
        if (plain == List.class || plain == Optional.class) {
            throw new IllegalArgumentException(plain.getSimpleName() + " needs its item type; use a generic declaration or an explicit codec");
        }
        if (!plain.isRecord() && !plain.isEnum() && !plain.isSealed()) {
            throw new IllegalArgumentException("no Jacquard mapping for " + plain.getName());
        }
        Lazy pending = seen.get(plain);
        if (pending != null) {
            return pending;
        }
        Lazy lazy = new Lazy();
        seen.put(plain, lazy);
        Cases<?> codec;
        if (plain.isRecord()) {
            codec = record((Class) plain, plain.getSimpleName(), null, seen);
        } else if (plain.isEnum()) {
            codec = (Cases<?>) enumeration((Class) plain, null);
        } else {
            codec = union(plain, seen);
        }
        lazy.target = (Cases<Object>) codec;
        return codec;
    }

    // Arguments by runtime class.

    /** Encodes a function argument by its runtime class, or with the codec of an {@link Argument}. */
    static final Codec<Object> RUNTIME = new Codec<>() {
        @Override
        public Value encode(Encoder encoder, Object value) throws JacquardException {
            return switch (value) {
                case Argument<?> argument -> argument(encoder, argument);
                case Long number -> encoder.integer(number);
                case Integer number -> encoder.integer(number);
                case Short number -> encoder.integer(number);
                case Byte number -> encoder.integer(number);
                case Double number -> encoder.real(number);
                case Float number -> encoder.real(number);
                case String text -> encoder.text(text);
                case Boolean bool -> encoder.bool(bool);
                case Hash hash -> encoder.hash(hash);
                case Value raw -> encoder.raw(raw);
                case Dynamic dynamic -> Codec.DYNAMIC.encode(encoder, dynamic);
                case Optional<?> optional -> encoder.option(this, optional.orElse(null));
                case List<?> list -> encoder.list(this, list);
                case Record record -> encodeCase(encoder, cases(encoder, record.getClass()), record);
                case Enum<?> constant -> encodeCase(encoder, cases(encoder, constant.getDeclaringClass()), constant);
                default -> throw encoder.invalid("no Jacquard mapping for " + value.getClass().getName() + "; pass codec.arg(value)");
            };
        }

        @Override
        public Object decode(Decoder decoder, Value value) throws JacquardException {
            throw decoder.mapping("decoding needs an explicit codec");
        }
    };

    /** The derived codec of an argument's class, or an invalid argument naming why there is none. */
    private static Cases<?> cases(Encoder encoder, Class<?> type) throws JacquardException {
        try {
            return (Cases<?>) derived(type);
        } catch (IllegalArgumentException error) {
            throw encoder.invalid(error.getMessage() + "; pass codec.arg(value)");
        }
    }

    private static <T> Value argument(Encoder encoder, Argument<T> argument) throws JacquardException {
        return encoder.encode(argument.codec(), argument.value());
    }
}
