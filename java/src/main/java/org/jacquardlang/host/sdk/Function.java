package org.jacquardlang.host.sdk;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Map;
import java.util.Objects;

import org.jacquardlang.host.Host;
import org.jacquardlang.host.OperationRegistry;
import org.jacquardlang.host.ProtocolException;
import org.jacquardlang.host.carrier.Cancel;
import org.jacquardlang.host.json.Json;
import org.jacquardlang.host.json.JsonCodec;
import org.jacquardlang.host.protocol.BoundaryType;
import org.jacquardlang.host.protocol.Hash;
import org.jacquardlang.host.protocol.Interface;
import org.jacquardlang.host.protocol.Limits;
import org.jacquardlang.host.protocol.Protocol;
import org.jacquardlang.host.protocol.Value;

/**
 * A bound checked function. It is immutable and thread-safe; each call runs
 * one fresh, serial worker.
 *
 * <p>Arguments are Java values encoded by their runtime class (see
 * {@link Codec#of(Class)}: {@code Long}, {@code Integer}, {@code String},
 * {@code Boolean}, {@code Double}, {@link org.jacquardlang.host.protocol.Hash},
 * {@link Value}, {@link Dynamic}, {@code Optional}, {@code List} of such
 * values, records, and enums), or {@link Argument}s made by
 * {@link Codec#arg}. Before any worker starts they are checked against the
 * function's checked signature, the selected limits, and the frame size.
 */
public final class Function {
    private final String name;
    private final Interface signature;
    private final Hash target;
    private final OperationRegistry registry;
    private final Host host;
    private final Data data;

    Function(String name, Interface signature, Hash target, OperationRegistry registry, Host host, Data data) {
        this.name = name;
        this.signature = signature;
        this.target = target;
        this.registry = registry;
        this.host = host;
        this.data = data;
    }

    /** The public name. */
    public String name() {
        return name;
    }

    /** The checked signature. */
    public Interface signature() {
        return signature;
    }

    Data data() {
        return data;
    }

    /** Encodes {@code args}, one frame's worth, with one shared budget. */
    private List<Value> encode(List<?> args) throws JacquardException {
        Encoder.Budget budget = new Encoder.Budget();
        List<Value> values = new ArrayList<>(args.size());
        for (int i = 0; i < args.size(); i++) {
            values.add(Encoder.root(data, name + ".argument[" + i + "]", budget).encode(Codecs.RUNTIME, args.get(i)));
        }
        return values;
    }

    /** Validates encoded arguments against the signature and the frame limits, before any worker starts. */
    private List<Value> check(List<Value> values) throws JacquardException {
        int expected = signature.parameters().size();
        if (values.size() != expected) {
            throw JacquardException.invalid(name + ": expected " + expected + " arguments, received " + values.size());
        }
        for (int i = 0; i < values.size(); i++) {
            data.conform(values.get(i), signature.parameters().get(i), name + ".argument[" + i + "]");
        }
        Limits limits = host.limits();
        try {
            Map<String, Json> frame = Protocol.invokeFrame(target, signature, values, registry.entries(), limits);
            JsonCodec.frameBytes(frame, limits.maxFrameBytes(), limits.maxJsonDepth());
        } catch (ProtocolException error) {
            throw JacquardException.invalid(name + ": " + error.getMessage() + "; reduce input size or use smaller independent batches");
        }
        return values;
    }

    private List<Value> arguments(Object[] args) throws JacquardException {
        Objects.requireNonNull(args, "args");
        return check(encode(Arrays.asList(args)));
    }

    private Invocation run(List<Value> values, Cancel cancel) {
        return new Invocation(host.invoke(target, signature, values, registry, cancel), this);
    }

    /**
     * Validates the arguments locally, then runs one worker. The exception
     * is only a local refusal; Core and carrier failures are in the
     * {@link Invocation}. Nothing is retried.
     *
     * @throws JacquardException an invalid argument; no worker started
     */
    public Invocation invoke(Object... args) throws JacquardException {
        return invoke(new Cancel(), args);
    }

    /**
     * {@link #invoke(Object...)}; cancelling {@code cancel}, or interrupting
     * the calling thread, kills the worker. The interrupt status is kept.
     */
    public Invocation invoke(Cancel cancel, Object... args) throws JacquardException {
        Objects.requireNonNull(cancel, "cancel");
        return run(arguments(args), cancel);
    }

    /**
     * Invokes the function and decodes its result.
     *
     * @throws JacquardException an invalid argument, a failed invocation, or a
     *     result that does not decode (which keeps the successful raw result)
     */
    public <R> R call(Codec<R> result, Object... args) throws JacquardException {
        return invoke(args).decode(result);
    }

    /** {@link #call(Codec, Object...)}; cancelling {@code cancel} kills the worker. */
    public <R> R call(Cancel cancel, Codec<R> result, Object... args) throws JacquardException {
        return invoke(cancel, args).decode(result);
    }

    /**
     * {@link #mapBatches(Cancel, Codec, List, int, Object...)} without
     * cancellation.
     */
    public <R> List<R> mapBatches(Codec<R> item, List<?> rows, int batchSize, Object... leading) throws JacquardException {
        return mapBatches(new Cancel(), item, rows, batchSize, leading);
    }

    /**
     * Runs a pure function whose final parameter and result are Lists over
     * explicit chunks of {@code rows}, with {@code leading} as the other
     * arguments, and concatenates the decoded results. Every chunk is
     * validated before the first runs, one at a time: validation returns
     * nothing, so each chunk's encoding is unreachable once it is checked,
     * and the chunk is encoded and checked again just before it runs. At most
     * one chunk's encoding is reachable at a time. Before each chunk, and
     * once more after the last, the validation observes {@code cancel} and
     * the caller's interrupt (which it never clears). Validation has no
     * deadline of its own, as in the other SDKs. Nothing is retried.
     *
     * @throws JacquardException a refusal before any worker started
     *     ({@link JacquardException#batchIndex()} is -1): an invalid chunk, or
     *     a stop, which is reported as the host's aborted invocation, without
     *     a worker; or the failure of one batch, with the decoded values of
     *     the batches before it in {@link JacquardException#completed()}
     */
    public <R> List<R> mapBatches(Cancel cancel, Codec<R> item, List<?> rows, int batchSize, Object... leading)
            throws JacquardException {
        Objects.requireNonNull(cancel, "cancel");
        Objects.requireNonNull(item, "item");
        Objects.requireNonNull(rows, "rows");
        Objects.requireNonNull(leading, "leading");
        if (batchSize < 1) {
            throw JacquardException.invalid("batch size must be positive");
        }
        Hash list = data.name("list");
        List<BoundaryType> parameters = signature.parameters();
        if (!signature.effects().isEmpty() || parameters.isEmpty() || !isList(parameters.get(parameters.size() - 1), list)
                || !isList(signature.result(), list)) {
            throw JacquardException.binding("mapBatches needs a pure function with a final List parameter and a List result");
        }
        List<Value> empty = batch(leading, List.of());
        int count = (int) ((rows.size() + (long) batchSize - 1) / batchSize);
        for (int index = 0; index < count; index++) {
            refuseIfStopped(empty, cancel);
            validate(leading, chunk(rows, index, batchSize));
        }
        // A stop during the last chunk's validation starts no worker.
        refuseIfStopped(empty, cancel);
        List<R> completed = new ArrayList<>();
        Codec<List<R>> results = Codec.list(item);
        for (int index = 0; index < count; index++) {
            try {
                completed.addAll(run(batch(leading, chunk(rows, index, batchSize)), cancel).decode(results));
            } catch (JacquardException error) {
                throw JacquardException.batch(index, new ArrayList<Object>(completed), error);
            }
        }
        return completed;
    }

    /** Throws the host's aborted invocation, which starts no worker, once {@code cancel} or the interrupt is set. */
    private void refuseIfStopped(List<Value> empty, Cancel cancel) throws JacquardException {
        if (cancel.isCancelled() || Thread.currentThread().isInterrupted()) {
            // The host observes the stop before it spawns a worker.
            Invocation stopped = run(empty, cancel);
            if (!stopped.ok()) {
                throw JacquardException.invocation(stopped.raw());
            }
        }
    }

    /** Encodes and checks one chunk, keeping nothing: the encoding is unreachable once this returns. */
    private void validate(Object[] leading, List<?> chunk) throws JacquardException {
        batch(leading, chunk);
    }

    private static List<?> chunk(List<?> rows, int index, int batchSize) {
        int start = index * batchSize;
        return rows.subList(start, (int) Math.min(rows.size(), (long) start + batchSize));
    }

    private List<Value> batch(Object[] leading, List<?> chunk) throws JacquardException {
        List<Object> args = new ArrayList<>(Arrays.asList(leading));
        args.add(chunk);
        return check(encode(args));
    }

    private static boolean isList(BoundaryType type, Hash list) {
        return type instanceof BoundaryType.Nominal nominal && nominal.identity().equals(list);
    }

    @Override
    public String toString() {
        return "Function[" + name + ", " + target + "]";
    }
}
