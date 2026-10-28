package org.jacquardlang.host.protocol;

import java.util.ArrayList;
import java.util.Collections;
import java.util.EnumSet;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeMap;

import org.jacquardlang.host.Compat;
import org.jacquardlang.host.ProtocolException;
import org.jacquardlang.host.json.Json;
import org.jacquardlang.host.json.JsonCodec;

/**
 * The carrier-independent v0 host state machine: one hello, one selection,
 * one invocation or shutdown, consecutive requests, and one terminal. A
 * session is used from one thread at a time.
 */
public final class Session {
    private State state = State.HELLO;
    private Limits limits = Limits.HARD;
    private final List<EffectRequest> requests = new ArrayList<>();
    private final List<Map<String, Json>> responses = new ArrayList<>();
    private Terminal terminal;
    private Long pending;

    /** A session awaiting {@code core_hello}. */
    public Session() {}

    /** The current state. */
    public State state() {
        return state;
    }

    /** The selected limits; the hard limits until selection. */
    public Limits limits() {
        return limits;
    }

    /** The effect requests received, in order. */
    public List<EffectRequest> requests() {
        return Collections.unmodifiableList(requests);
    }

    /** The responses rendered, in order. */
    public List<Map<String, Json>> responses() {
        return Collections.unmodifiableList(responses);
    }

    /** Core's terminal frame, once received. */
    public Terminal terminal() {
        return terminal;
    }

    /** The ordinal of the request awaiting a response, or null. */
    public Long pending() {
        return pending;
    }

    /** Validates the next Core frame. Returns null for {@code core_hello}. */
    public CoreFrame receive(Map<String, Json> frame) throws ProtocolException {
        if (state == State.CLOSED) {
            throw ProtocolException.order("Core sent a frame after its terminal");
        }
        if (state == State.HELLO) {
            if (!Json.of("core_hello").equals(frame.get("kind"))) {
                throw ProtocolException.order("first Core frame must be core_hello");
            }
            Protocol.exact(Json.of(frame), "carrier", "kind", "limits", "versions");
            if (!Json.of(Compat.CARRIER).equals(frame.get("carrier"))) {
                throw ProtocolException.malformed("unknown carrier");
            }
            List<Json> versions = frame.get("versions").asArray();
            if (versions == null) {
                throw ProtocolException.malformed("expected an array");
            }
            if (versions.isEmpty()) {
                throw ProtocolException.malformed("versions must be nonempty");
            }
            boolean offered = false;
            for (Json version : versions) {
                String name = version.asString();
                if (name == null) {
                    throw ProtocolException.malformed("expected a string");
                }
                offered |= name.equals(Compat.PROTOCOL);
            }
            if (!Limits.fromJson(frame.get("limits")).equals(Limits.HARD)) {
                throw ProtocolException.malformed("hello must advertise exactly the frozen limits");
            }
            if (!offered) {
                throw new ProtocolException("E1600", "Core does not offer " + Compat.PROTOCOL);
            }
            state = State.NEGOTIATING;
            return null;
        }
        CoreFrame parsed = Protocol.parseCoreFrame(frame, limits);
        switch (parsed) {
            case EffectRequest request -> {
                if (state != State.RUNNING) {
                    throw ProtocolException.order("effect request without an available invocation slot");
                }
                if (request.ordinal() != requests.size() + 1L) {
                    throw ProtocolException.order("request ordinal is not consecutive");
                }
                if (request.ordinal() > limits.maxEffectRequests()) {
                    throw ProtocolException.exceeded("Core exceeded max_effect_requests");
                }
                requests.add(request);
                pending = request.ordinal();
                state = State.WAITING;
            }
            case Terminal end -> {
                boolean allowed = switch (end) {
                    case Terminal.Outcome ignored -> EnumSet.of(State.RUNNING, State.WAITING, State.FINISHING).contains(state);
                    case Terminal.Fatal ignored -> EnumSet.of(
                            State.NEGOTIATING, State.SELECTED, State.SHUTTING_DOWN, State.RUNNING, State.FINISHING).contains(state);
                    case Terminal.ShutdownAck ignored -> state == State.SHUTTING_DOWN;
                };
                if (!allowed) {
                    throw ProtocolException.order(switch (end) {
                        case Terminal.Outcome ignored -> "outcome without a live invocation";
                        case Terminal.Fatal ignored -> "fatal while a response is outstanding";
                        case Terminal.ShutdownAck ignored -> "shutdown_ack without shutdown";
                    });
                }
                pending = null;
                terminal = end;
                state = State.CLOSED;
            }
        }
        return parsed;
    }

    private void require(Set<State> states) throws ProtocolException {
        if (!states.contains(state)) {
            throw ProtocolException.order("message is not permitted in state " + state.wireName());
        }
    }

    /** Chooses the session limits. */
    public Map<String, Json> select(Limits selected) throws ProtocolException {
        require(EnumSet.of(State.NEGOTIATING));
        selected.validate();
        limits = selected;
        state = State.SELECTED;
        return Json.object("kind", Json.of("host_select"), "limits", Json.of(selected.toJson()), "protocol", Json.of(Compat.PROTOCOL));
    }

    /** Renders the session's single invocation. */
    public Map<String, Json> invoke(Hash target, Interface spec, List<Value> args, List<OperationEntry> operations)
            throws ProtocolException {
        require(EnumSet.of(State.SELECTED));
        Map<String, Json> frame = Protocol.invokeFrame(target, spec, args, operations, limits);
        state = State.RUNNING;
        return frame;
    }

    /** Ends a selected session without invoking. */
    public Map<String, Json> shutdown() throws ProtocolException {
        require(EnumSet.of(State.SELECTED));
        state = State.SHUTTING_DOWN;
        return Json.object("kind", Json.of("shutdown"), "protocol", Json.of(Compat.PROTOCOL));
    }

    private static String requestId(long ordinal) {
        String hex = Long.toHexString(ordinal);
        return "0".repeat(16 - hex.length()) + hex;
    }

    private Map<String, Json> respond(Map<String, Json> fields, boolean terminalResponse) {
        long ordinal = pending;
        pending = null;
        TreeMap<String, Json> frame = new TreeMap<>(Json.CODE_POINT_ORDER);
        frame.putAll(fields);
        frame.put("protocol", Json.of(Compat.PROTOCOL));
        frame.put("invocation_id", Json.of(Protocol.INVOCATION_ID));
        frame.put("request_id", Json.of(requestId(ordinal)));
        Map<String, Json> rendered = Json.sorted(frame);
        responses.add(rendered);
        state = terminalResponse ? State.FINISHING : State.RUNNING;
        return rendered;
    }

    /** The {@code effect_ok} frame for a value, validated without changing state. */
    public Map<String, Json> okFrame(Value value) throws ProtocolException {
        require(EnumSet.of(State.WAITING));
        Json encoded = Protocol.encodeValue(value, new Budget(limits));
        Map<String, Json> frame = Json.object(
                "invocation_id", Json.of(Protocol.INVOCATION_ID),
                "kind", Json.of("effect_ok"),
                "protocol", Json.of(Compat.PROTOCOL),
                "request_id", Json.of(requestId(pending)),
                "value", encoded);
        JsonCodec.frameBytes(frame, limits.maxFrameBytes(), limits.maxJsonDepth());
        return frame;
    }

    /** Answers the pending request with a value. */
    public Map<String, Json> respondOk(Value value) throws ProtocolException {
        require(EnumSet.of(State.WAITING));
        Json encoded = Protocol.encodeValue(value, new Budget(limits));
        return respond(Json.object("kind", Json.of("effect_ok"), "value", encoded), false);
    }

    /** Answers the pending request with a permitted failure. */
    public Map<String, Json> respondFailure(FailureCategory category, String message) throws ProtocolException {
        require(EnumSet.of(State.WAITING));
        if (!JsonCodec.isScalar(message)) {
            throw ProtocolException.malformed("a JSON string contains a lone surrogate");
        }
        if (JsonCodec.utf8Length(message) > limits.maxHostMessageBytes()) {
            throw ProtocolException.exceeded("host message exceeds max_host_message_bytes");
        }
        return respond(Json.object(
                "category", Json.of(category.wireName()),
                "completion", Json.of(category.completion()),
                "kind", Json.of("effect_failure"),
                "message", Json.of(message)), true);
    }

    /** Withdraws the pending request. */
    public Map<String, Json> cancel(CancelReason reason, String completion) throws ProtocolException {
        require(EnumSet.of(State.WAITING));
        if (!List.of("not_started", "failed", "unknown").contains(completion)) {
            throw ProtocolException.malformed("unknown cancellation enum");
        }
        return respond(Json.object(
                "completion", Json.of(completion), "kind", Json.of("cancel"), "reason", Json.of(reason.wireName())), true);
    }
}
