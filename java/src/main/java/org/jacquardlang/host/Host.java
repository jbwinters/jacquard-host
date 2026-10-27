package org.jacquardlang.host;

import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.atomic.AtomicBoolean;

import org.jacquardlang.host.carrier.Cancel;
import org.jacquardlang.host.carrier.CarrierException;
import org.jacquardlang.host.carrier.Options;
import org.jacquardlang.host.carrier.Worker;
import org.jacquardlang.host.json.Json;
import org.jacquardlang.host.json.JsonCodec;
import org.jacquardlang.host.protocol.CoreFrame;
import org.jacquardlang.host.protocol.EffectRequest;
import org.jacquardlang.host.protocol.FailureCategory;
import org.jacquardlang.host.protocol.Hash;
import org.jacquardlang.host.protocol.Interface;
import org.jacquardlang.host.protocol.Limits;
import org.jacquardlang.host.protocol.OperationEntry;
import org.jacquardlang.host.protocol.Protocol;
import org.jacquardlang.host.protocol.Session;
import org.jacquardlang.host.protocol.State;
import org.jacquardlang.host.protocol.Terminal;
import org.jacquardlang.host.protocol.Value;

/**
 * Invokes checked Jacquard terms in one prepared store through
 * {@code jacquard-host-v0} and serves their effect requests from a closed
 * registry of Java handlers. Each invocation runs one fresh, serial worker.
 */
public final class Host {
    /** Host evidence schema recorded beside Core's frames. */
    public static final String EVIDENCE_SCHEMA = "jacquard-host-java-evidence-v0";

    private final String store;
    private final Limits limits;
    private final Options options;

    /**
     * Validates options before any worker starts.
     *
     * @throws IllegalArgumentException for invalid limits or carrier options
     */
    public Host(String store, HostOptions options) {
        this.store = Objects.requireNonNull(store, "store");
        Limits selected = options.limits() == null ? Limits.HARD : options.limits();
        try {
            selected.validate();
        } catch (ProtocolException error) {
            throw new IllegalArgumentException(error.getMessage(), error);
        }
        options.carrier().validate();
        this.limits = selected;
        this.options = options.carrier();
    }

    /** The prepared store directory. */
    public String store() {
        return store;
    }

    /** The limits each session selects. */
    public Limits limits() {
        return limits;
    }

    /**
     * Runs {@code target} with {@code args}. The invoke frame is validated
     * before a worker starts. Cancelling {@code cancel}, or interrupting the
     * calling thread, kills the worker; nothing is retried.
     *
     * @throws NullPointerException if any argument is null
     */
    public InvocationResult invoke(Hash target, Interface spec, List<Value> args, OperationRegistry registry, Cancel cancel) {
        Objects.requireNonNull(target, "target");
        Objects.requireNonNull(spec, "spec");
        Objects.requireNonNull(args, "args");
        Objects.requireNonNull(registry, "registry");
        Objects.requireNonNull(cancel, "cancel");
        List<OperationEntry> entries = registry.entries();
        try {
            Map<String, Json> frame = Protocol.invokeFrame(target, spec, args, entries, limits);
            JsonCodec.frameBytes(frame, limits.maxFrameBytes(), limits.maxJsonDepth());
        } catch (ProtocolException error) {
            InvocationResult result = new InvocationResult(ResultKind.HOST_ERROR);
            result.hostEvidence = Json.object(
                    "code", Json.of(error.code()),
                    "detail", Json.of(error.detail()),
                    "reason", Json.of("invoke_refused_locally"),
                    "schema", Json.of(EVIDENCE_SCHEMA));
            return result;
        }
        return run(session -> session.invoke(target, spec, args, entries), registry, cancel);
    }

    /**
     * Negotiates a session and shuts it down without invoking.
     *
     * @throws NullPointerException if {@code cancel} is null
     */
    public InvocationResult shutdownProbe(Cancel cancel) {
        Objects.requireNonNull(cancel, "cancel");
        return run(Session::shutdown, new OperationRegistry(), cancel);
    }

    @FunctionalInterface
    private interface Opening {
        Map<String, Json> render(Session session) throws ProtocolException;
    }

    /** Whether a host frame was being encoded or sent when a failure occurred. */
    private static final class Sending {
        boolean active;
    }

    private InvocationResult run(Opening first, OperationRegistry registry, Cancel cancel) {
        Session session = new Session();
        InvocationResult result = new InvocationResult(ResultKind.CARRIER_LOSS);
        Worker worker;
        try {
            worker = Worker.start(store, options, cancel);
        } catch (IllegalArgumentException error) {
            result.hostEvidence = Json.object(
                    "detail", Json.of(error.getMessage()), "reason", Json.of("invalid_options"), "schema", Json.of(EVIDENCE_SCHEMA));
            return result;
        }
        Sending sending = new Sending();
        AtomicBoolean ended = new AtomicBoolean();
        try {
            drive(worker, session, result, sending, first, registry, cancel, ended);
        } catch (CarrierException error) {
            ResultKind kind;
            String code;
            String reason;
            if (!error.isLoss()) {
                code = error.protocolError().code();
                kind = sending.active ? ResultKind.HOST_ERROR : ResultKind.PROTOCOL_ERROR;
                reason = sending.active ? "host_frame_refused" : "core_frame_refused";
            } else {
                kind = ResultKind.CARRIER_LOSS;
                code = "E1611";
                if (error.isTimeout()) {
                    reason = "deadline";
                } else if (worker.spawnError() != null) {
                    reason = "worker_spawn_failed";
                } else if (error.isAborted()) {
                    reason = "aborted";
                } else {
                    reason = "carrier_loss";
                }
            }
            result.kind = kind;
            Long outstanding = session.pending();
            result.hostEvidence = Json.object(
                    "code", Json.of(code),
                    "detail", Json.of(error.getMessage()),
                    "outstanding_request", outstanding == null ? Json.NULL : Json.of(outstanding),
                    "reason", Json.of(reason),
                    "schema", Json.of(EVIDENCE_SCHEMA),
                    "state", Json.of(session.state().wireName()));
        } finally {
            worker.dispose();
            ended.set(true);
        }
        result.requests = List.copyOf(session.requests());
        result.exitStatus = worker.exitStatus();
        result.signal = worker.signal();
        result.stderr = worker.stderr();
        return result;
    }

    private static void send(Worker worker, Map<String, Json> frame, Limits limits, InvocationResult result, Sending sending)
            throws CarrierException {
        sending.active = true;
        worker.writeFrame(frame, limits);
        result.hostFrames.add(frame);
        sending.active = false;
    }

    private static CarrierException protocol(ProtocolException error) {
        return CarrierException.protocol(error);
    }

    private void drive(Worker worker, Session session, InvocationResult result, Sending sending, Opening first,
            OperationRegistry registry, Cancel cancel, AtomicBoolean ended) throws CarrierException {
        Map<String, Json> hello = worker.readFrame(Limits.HARD);
        if (hello == null) {
            throw CarrierException.loss("worker exited before core_hello");
        }
        result.coreFrames.add(hello);
        Map<String, Json> selected;
        try {
            session.receive(hello);
            selected = session.select(limits);
        } catch (ProtocolException error) {
            throw protocol(error);
        }
        send(worker, selected, Limits.HARD, result, sending);
        worker.setStderrLimit(limits.maxStderrBytes());
        Map<String, Json> opening;
        try {
            opening = first.render(session);
        } catch (ProtocolException error) {
            throw protocol(error);
        }
        send(worker, opening, session.limits(), result, sending);
        while (session.state() != State.CLOSED) {
            Map<String, Json> frame = worker.readFrame(session.limits());
            if (frame == null) {
                throw CarrierException.loss("worker exited before a terminal frame");
            }
            result.coreFrames.add(frame);
            CoreFrame parsed;
            try {
                parsed = session.receive(frame);
            } catch (ProtocolException error) {
                throw protocol(error);
            }
            switch (parsed) {
                case EffectRequest request -> {
                    Map<String, Json> response = respond(worker, session, registry, request, cancel, ended);
                    send(worker, response, session.limits(), result, sending);
                }
                case Terminal.Outcome outcome -> {
                    result.kind = outcome.value() != null ? ResultKind.OK : ResultKind.ERROR;
                    result.value = outcome.value();
                    result.diagnostics = outcome.diagnostics();
                    result.terminal = outcome.terminal();
                    result.evidence = outcome.evidence();
                }
                case Terminal.Fatal fatal -> {
                    result.kind = ResultKind.FATAL;
                    result.diagnostics = fatal.diagnostics();
                }
                case Terminal.ShutdownAck ignored -> result.kind = ResultKind.SHUTDOWN_ACK;
                case null -> {
                    // core_hello was handled above.
                }
            }
        }
        worker.closeInput();
        Map<String, Json> extra = worker.readFrame(session.limits());
        if (extra != null) {
            result.coreFrames.add(extra);
            try {
                session.receive(extra);
            } catch (ProtocolException error) {
                throw protocol(error);
            }
        }
        worker.awaitExit();
        if (!Integer.valueOf(0).equals(worker.exitStatus())) {
            throw CarrierException.loss("worker exited unsuccessfully after its terminal");
        }
    }

    private sealed interface Answer {}

    private record Returned(Value value) implements Answer {}

    private record Failed(FailureCategory category, String message) implements Answer {}

    /** The call stopped before the handler began; the fault check after the wait reports why. */
    private static final Answer NOT_STARTED = new Failed(FailureCategory.OUTSIDE_FAILURE, "handler was not started");

    /**
     * Runs the handler on its own thread, bounded by the worker lifetime, and
     * renders the response. A handler still running at the deadline is
     * abandoned: no late response is sent and nothing is retried.
     */
    private static Map<String, Json> respond(Worker worker, Session session, OperationRegistry registry, EffectRequest request,
            Cancel cancel, AtomicBoolean ended) throws CarrierException {
        Handler handler = registry.handler(request);
        Answer answer;
        if (handler == null) {
            answer = new Failed(FailureCategory.UNSUPPORTED_OPERATION, "operation is not configured");
        } else {
            // Decoding the request takes time; an outside action must not
            // begin once the call is cancelled, interrupted, or past its
            // lifetime, so check here and again as the handler thread starts.
            CarrierException early = worker.fault();
            if (early != null) {
                throw early;
            }
            HandlerContext context = new HandlerContext(worker.expiredFlag(), ended::get, cancel);
            List<Value> arguments = request.arguments();
            CompletableFuture<Answer> done = new CompletableFuture<>();
            Thread thread = new Thread(() -> done.complete(worker.stopping() ? NOT_STARTED : call(handler, context, arguments)),
                    "jacquard-handler");
            thread.setDaemon(true);
            boolean started;
            try {
                thread.start();
                started = true;
            } catch (OutOfMemoryError error) {
                started = false;
            }
            if (!started) {
                // The handler never ran.
                answer = new Failed(FailureCategory.OUTSIDE_FAILURE, "handler thread could not start");
            } else {
                Answer awaited = worker.await(done);
                answer = awaited != null ? awaited : new Failed(FailureCategory.COMPLETION_UNKNOWN, "handler threw an exception");
            }
        }
        CarrierException fault = worker.fault();
        if (fault != null) {
            throw fault;
        }
        Failed failure;
        try {
            switch (answer) {
                case Returned returned -> {
                    try {
                        session.okFrame(returned.value());
                        return session.respondOk(returned.value());
                    } catch (ProtocolException error) {
                        failure = new Failed(FailureCategory.OUTSIDE_FAILURE, "handler result has no encoding within the selected limits");
                    }
                }
                case Failed failed -> failure = failed;
            }
            return session.respondFailure(failure.category(), bounded(failure.message(), session.limits().maxHostMessageBytes()));
        } catch (ProtocolException error) {
            throw protocol(error);
        }
    }

    /** Calls a handler, turning every exception into a failure answer. */
    private static Answer call(Handler handler, HandlerContext context, List<Value> arguments) {
        try {
            Value value = handler.handle(context, arguments);
            return new Returned(value);
        } catch (HandlerException failure) {
            return new Failed(failure.category(), failure.getMessage());
        } catch (Throwable other) {
            // Its message may carry private detail and is never sent.
            return new Failed(FailureCategory.COMPLETION_UNKNOWN, "handler threw an exception");
        }
    }

    /**
     * At most {@code limit} UTF-8 bytes of {@code text}, cut at a code point
     * boundary; a lone surrogate, which has no encoding, becomes U+FFFD.
     */
    static String bounded(String text, int limit) {
        StringBuilder out = new StringBuilder();
        int used = 0;
        for (int i = 0; i < text.length(); ) {
            int point = text.codePointAt(i);
            i += Character.charCount(point);
            if (point >= Character.MIN_SURROGATE && point <= Character.MAX_SURROGATE) {
                point = 0xFFFD;
            }
            int size = point < 0x80 ? 1 : point < 0x800 ? 2 : point < 0x10000 ? 3 : 4;
            if (used + size > limit) {
                break;
            }
            out.appendCodePoint(point);
            used += size;
        }
        return out.toString();
    }
}
