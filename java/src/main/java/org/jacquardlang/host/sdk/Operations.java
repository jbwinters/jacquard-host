package org.jacquardlang.host.sdk;

import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.TreeMap;

import org.jacquardlang.host.HandlerContext;
import org.jacquardlang.host.HandlerException;
import org.jacquardlang.host.protocol.Value;

/**
 * One effect's service: operation names mapped to Java handlers.
 *
 * <p>Names are store spellings such as {@code "lookup-stock"}, or display
 * spellings; {@code _} is accepted for {@code -}. Each handler declares a
 * codec per parameter and one for its result, and may take a leading
 * {@link HandlerContext} that reports cancellation:
 *
 * <pre>{@code
 * Operations inventory = new Operations()
 *         .on("lookup-stock", Codec.TEXT, Codec.INT, sku -> sku.equals("sensor") ? 20L : 0L)
 *         .on("reserve", Codec.TEXT, Codec.INT, Codec.UNIT, (context, sku, units) -> {
 *             if (context.isCancelled()) {
 *                 throw HandlerException.refused("cancelled");
 *             }
 *             return null;
 *         });
 * }</pre>
 *
 * <p>A request whose arguments do not decode into the parameters, or whose
 * argument count differs, is reported as an outside failure and the handler
 * does not run. Nor does it run once the invocation is cancelled, the caller
 * interrupted, or the lifetime passed while the arguments were decoded. A
 * {@link HandlerException} is reported as that failure; any other exception
 * is reported as completion unknown, without its message. A result with no
 * encoding within the selected limits is reported as an outside failure,
 * after the handler returned.
 */
public final class Operations {
    /** A typed handler with its codecs applied. */
    @FunctionalInterface
    interface Erased {
        Value handle(Data data, HandlerContext context, List<Value> arguments) throws Exception;
    }

    @FunctionalInterface
    private interface Invoker<R> {
        R invoke(HandlerContext context, Object[] values) throws Exception;
    }

    /**
     * Test seam: runs on the handler thread after the arguments are decoded
     * and before the stop check that precedes the handler. Null outside tests.
     */
    static volatile Runnable afterDecodeHook;

    private final Map<String, Erased> handlers = new TreeMap<>();

    /** An empty service. */
    public Operations() {}

    /** The handlers by store-spelled operation name. */
    synchronized Map<String, Erased> handlers() {
        return Map.copyOf(handlers);
    }

    private synchronized <R> Operations bind(String operation, List<Codec<?>> parameters, Codec<R> result, Invoker<R> invoker) {
        Objects.requireNonNull(operation, "operation");
        Objects.requireNonNull(result, "result");
        for (Codec<?> parameter : parameters) {
            Objects.requireNonNull(parameter, "parameter codec");
        }
        handlers.put(Identities.normal(operation), (data, context, arguments) -> {
            if (arguments.size() != parameters.size()) {
                throw HandlerException.outsideFailed("handler parameter count differs from the request");
            }
            Decoder decoder = new Decoder(data);
            Object[] values = new Object[parameters.size()];
            for (int i = 0; i < values.length; i++) {
                try {
                    values[i] = parameters.get(i).decode(decoder, arguments.get(i));
                } catch (JacquardException | RuntimeException error) {
                    throw HandlerException.outsideFailed("handler argument cannot be mapped");
                }
            }
            Runnable hook = afterDecodeHook;
            if (hook != null) {
                hook.run();
            }
            // Decoding takes time; no outside action begins once the call stopped.
            if (context.isCancelled()) {
                throw HandlerException.outsideFailed("handler was not started");
            }
            R value = invoker.invoke(context, values);
            try {
                return Encoder.root(data, "result").encode(result, value);
            } catch (JacquardException | RuntimeException error) {
                throw HandlerException.outsideFailed("handler result cannot be represented");
            }
        });
        return this;
    }

    /** A handler with 0 decoded parameters. */
    @FunctionalInterface
    public interface Handler0<R> {
        R apply() throws Exception;
    }

    /** A handler with a leading {@link HandlerContext} and 0 decoded parameters. */
    @FunctionalInterface
    public interface ContextHandler0<R> {
        R apply(HandlerContext context) throws Exception;
    }

    /** A handler with 1 decoded parameter. */
    @FunctionalInterface
    public interface Handler1<A, R> {
        R apply(A a) throws Exception;
    }

    /** A handler with a leading {@link HandlerContext} and 1 decoded parameter. */
    @FunctionalInterface
    public interface ContextHandler1<A, R> {
        R apply(HandlerContext context, A a) throws Exception;
    }

    /** A handler with 2 decoded parameters. */
    @FunctionalInterface
    public interface Handler2<A, B, R> {
        R apply(A a, B b) throws Exception;
    }

    /** A handler with a leading {@link HandlerContext} and 2 decoded parameters. */
    @FunctionalInterface
    public interface ContextHandler2<A, B, R> {
        R apply(HandlerContext context, A a, B b) throws Exception;
    }

    /** A handler with 3 decoded parameters. */
    @FunctionalInterface
    public interface Handler3<A, B, C, R> {
        R apply(A a, B b, C c) throws Exception;
    }

    /** A handler with a leading {@link HandlerContext} and 3 decoded parameters. */
    @FunctionalInterface
    public interface ContextHandler3<A, B, C, R> {
        R apply(HandlerContext context, A a, B b, C c) throws Exception;
    }

    /** A handler with 4 decoded parameters. */
    @FunctionalInterface
    public interface Handler4<A, B, C, D, R> {
        R apply(A a, B b, C c, D d) throws Exception;
    }

    /** A handler with a leading {@link HandlerContext} and 4 decoded parameters. */
    @FunctionalInterface
    public interface ContextHandler4<A, B, C, D, R> {
        R apply(HandlerContext context, A a, B b, C c, D d) throws Exception;
    }

    /** A handler with 5 decoded parameters. */
    @FunctionalInterface
    public interface Handler5<A, B, C, D, E, R> {
        R apply(A a, B b, C c, D d, E e) throws Exception;
    }

    /** A handler with a leading {@link HandlerContext} and 5 decoded parameters. */
    @FunctionalInterface
    public interface ContextHandler5<A, B, C, D, E, R> {
        R apply(HandlerContext context, A a, B b, C c, D d, E e) throws Exception;
    }

    /** A handler with 6 decoded parameters. */
    @FunctionalInterface
    public interface Handler6<A, B, C, D, E, F, R> {
        R apply(A a, B b, C c, D d, E e, F f) throws Exception;
    }

    /** A handler with a leading {@link HandlerContext} and 6 decoded parameters. */
    @FunctionalInterface
    public interface ContextHandler6<A, B, C, D, E, F, R> {
        R apply(HandlerContext context, A a, B b, C c, D d, E e, F f) throws Exception;
    }

    /**
     * Serves {@code operation} with {@code handler}, replacing any earlier
     * handler for it.
     *
     * @param result encodes the handler's result ({@link Codec#UNIT} for unit)
     */
    @SuppressWarnings("unchecked")
    public <R> Operations on(String operation, Codec<R> result, Handler0<R> handler) {
        Objects.requireNonNull(handler, "handler");
        return bind(operation, List.of(), result, (context, values) -> handler.apply());
    }

    /** {@link #on(String, Codec, Handler0)} for a handler that takes the {@link HandlerContext} first. */
    @SuppressWarnings("unchecked")
    public <R> Operations on(String operation, Codec<R> result, ContextHandler0<R> handler) {
        Objects.requireNonNull(handler, "handler");
        return bind(operation, List.of(), result, (context, values) -> handler.apply(context));
    }

    /**
     * Serves {@code operation} with {@code handler}, replacing any earlier
     * handler for it.
     *
     * @param a decodes parameter 0
     * @param result encodes the handler's result ({@link Codec#UNIT} for unit)
     */
    @SuppressWarnings("unchecked")
    public <A, R> Operations on(String operation, Codec<A> a, Codec<R> result, Handler1<A, R> handler) {
        Objects.requireNonNull(handler, "handler");
        return bind(operation, List.of(a), result, (context, values) -> handler.apply((A) values[0]));
    }

    /** {@link #on(String, Codec, Codec, Handler1)} for a handler that takes the {@link HandlerContext} first. */
    @SuppressWarnings("unchecked")
    public <A, R> Operations on(String operation, Codec<A> a, Codec<R> result, ContextHandler1<A, R> handler) {
        Objects.requireNonNull(handler, "handler");
        return bind(operation, List.of(a), result, (context, values) -> handler.apply(context, (A) values[0]));
    }

    /**
     * Serves {@code operation} with {@code handler}, replacing any earlier
     * handler for it.
     *
     * @param a decodes parameter 0
     * @param b decodes parameter 1
     * @param result encodes the handler's result ({@link Codec#UNIT} for unit)
     */
    @SuppressWarnings("unchecked")
    public <A, B, R> Operations on(String operation, Codec<A> a, Codec<B> b, Codec<R> result, Handler2<A, B, R> handler) {
        Objects.requireNonNull(handler, "handler");
        return bind(operation, List.of(a, b), result, (context, values) -> handler.apply((A) values[0], (B) values[1]));
    }

    /** {@link #on(String, Codec, Codec, Codec, Handler2)} for a handler that takes the {@link HandlerContext} first. */
    @SuppressWarnings("unchecked")
    public <A, B, R> Operations on(String operation, Codec<A> a, Codec<B> b, Codec<R> result, ContextHandler2<A, B, R> handler) {
        Objects.requireNonNull(handler, "handler");
        return bind(operation, List.of(a, b), result, (context, values) -> handler.apply(context, (A) values[0], (B) values[1]));
    }

    /**
     * Serves {@code operation} with {@code handler}, replacing any earlier
     * handler for it.
     *
     * @param a decodes parameter 0
     * @param b decodes parameter 1
     * @param c decodes parameter 2
     * @param result encodes the handler's result ({@link Codec#UNIT} for unit)
     */
    @SuppressWarnings("unchecked")
    public <A, B, C, R> Operations on(String operation, Codec<A> a, Codec<B> b, Codec<C> c, Codec<R> result, Handler3<A, B, C, R> handler) {
        Objects.requireNonNull(handler, "handler");
        return bind(operation, List.of(a, b, c), result, (context, values) -> handler.apply((A) values[0], (B) values[1], (C) values[2]));
    }

    /** {@link #on(String, Codec, Codec, Codec, Codec, Handler3)} for a handler that takes the {@link HandlerContext} first. */
    @SuppressWarnings("unchecked")
    public <A, B, C, R> Operations on(String operation, Codec<A> a, Codec<B> b, Codec<C> c, Codec<R> result, ContextHandler3<A, B, C, R> handler) {
        Objects.requireNonNull(handler, "handler");
        return bind(operation, List.of(a, b, c), result, (context, values) -> handler.apply(context, (A) values[0], (B) values[1], (C) values[2]));
    }

    /**
     * Serves {@code operation} with {@code handler}, replacing any earlier
     * handler for it.
     *
     * @param a decodes parameter 0
     * @param b decodes parameter 1
     * @param c decodes parameter 2
     * @param d decodes parameter 3
     * @param result encodes the handler's result ({@link Codec#UNIT} for unit)
     */
    @SuppressWarnings("unchecked")
    public <A, B, C, D, R> Operations on(String operation, Codec<A> a, Codec<B> b, Codec<C> c, Codec<D> d, Codec<R> result, Handler4<A, B, C, D, R> handler) {
        Objects.requireNonNull(handler, "handler");
        return bind(operation, List.of(a, b, c, d), result, (context, values) -> handler.apply((A) values[0], (B) values[1], (C) values[2], (D) values[3]));
    }

    /** {@link #on(String, Codec, Codec, Codec, Codec, Codec, Handler4)} for a handler that takes the {@link HandlerContext} first. */
    @SuppressWarnings("unchecked")
    public <A, B, C, D, R> Operations on(String operation, Codec<A> a, Codec<B> b, Codec<C> c, Codec<D> d, Codec<R> result, ContextHandler4<A, B, C, D, R> handler) {
        Objects.requireNonNull(handler, "handler");
        return bind(operation, List.of(a, b, c, d), result, (context, values) -> handler.apply(context, (A) values[0], (B) values[1], (C) values[2], (D) values[3]));
    }

    /**
     * Serves {@code operation} with {@code handler}, replacing any earlier
     * handler for it.
     *
     * @param a decodes parameter 0
     * @param b decodes parameter 1
     * @param c decodes parameter 2
     * @param d decodes parameter 3
     * @param e decodes parameter 4
     * @param result encodes the handler's result ({@link Codec#UNIT} for unit)
     */
    @SuppressWarnings("unchecked")
    public <A, B, C, D, E, R> Operations on(String operation, Codec<A> a, Codec<B> b, Codec<C> c, Codec<D> d, Codec<E> e, Codec<R> result, Handler5<A, B, C, D, E, R> handler) {
        Objects.requireNonNull(handler, "handler");
        return bind(operation, List.of(a, b, c, d, e), result, (context, values) -> handler.apply((A) values[0], (B) values[1], (C) values[2], (D) values[3], (E) values[4]));
    }

    /** {@link #on(String, Codec, Codec, Codec, Codec, Codec, Codec, Handler5)} for a handler that takes the {@link HandlerContext} first. */
    @SuppressWarnings("unchecked")
    public <A, B, C, D, E, R> Operations on(String operation, Codec<A> a, Codec<B> b, Codec<C> c, Codec<D> d, Codec<E> e, Codec<R> result, ContextHandler5<A, B, C, D, E, R> handler) {
        Objects.requireNonNull(handler, "handler");
        return bind(operation, List.of(a, b, c, d, e), result, (context, values) -> handler.apply(context, (A) values[0], (B) values[1], (C) values[2], (D) values[3], (E) values[4]));
    }

    /**
     * Serves {@code operation} with {@code handler}, replacing any earlier
     * handler for it.
     *
     * @param a decodes parameter 0
     * @param b decodes parameter 1
     * @param c decodes parameter 2
     * @param d decodes parameter 3
     * @param e decodes parameter 4
     * @param f decodes parameter 5
     * @param result encodes the handler's result ({@link Codec#UNIT} for unit)
     */
    @SuppressWarnings("unchecked")
    public <A, B, C, D, E, F, R> Operations on(String operation, Codec<A> a, Codec<B> b, Codec<C> c, Codec<D> d, Codec<E> e, Codec<F> f, Codec<R> result, Handler6<A, B, C, D, E, F, R> handler) {
        Objects.requireNonNull(handler, "handler");
        return bind(operation, List.of(a, b, c, d, e, f), result, (context, values) -> handler.apply((A) values[0], (B) values[1], (C) values[2], (D) values[3], (E) values[4], (F) values[5]));
    }

    /** {@link #on(String, Codec, Codec, Codec, Codec, Codec, Codec, Codec, Handler6)} for a handler that takes the {@link HandlerContext} first. */
    @SuppressWarnings("unchecked")
    public <A, B, C, D, E, F, R> Operations on(String operation, Codec<A> a, Codec<B> b, Codec<C> c, Codec<D> d, Codec<E> e, Codec<F> f, Codec<R> result, ContextHandler6<A, B, C, D, E, F, R> handler) {
        Objects.requireNonNull(handler, "handler");
        return bind(operation, List.of(a, b, c, d, e, f), result, (context, values) -> handler.apply(context, (A) values[0], (B) values[1], (C) values[2], (D) values[3], (E) values[4], (F) values[5]));
    }

    @Override
    public synchronized String toString() {
        return "Operations" + handlers.keySet();
    }
}
