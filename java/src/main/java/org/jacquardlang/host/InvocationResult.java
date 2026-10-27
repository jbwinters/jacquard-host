package org.jacquardlang.host;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import java.util.Map;

import org.jacquardlang.host.json.Json;
import org.jacquardlang.host.protocol.Diagnostic;
import org.jacquardlang.host.protocol.EffectRequest;
import org.jacquardlang.host.protocol.Value;

/** Core's frames and evidence beside the host's observations. */
public final class InvocationResult {
    ResultKind kind;
    Value value;
    List<Diagnostic> diagnostics = List.of();
    String terminal;
    Map<String, Json> evidence;
    final List<Map<String, Json>> coreFrames = new ArrayList<>();
    final List<Map<String, Json>> hostFrames = new ArrayList<>();
    List<EffectRequest> requests = List.of();
    Integer exitStatus;
    String signal;
    byte[] stderr = new byte[0];
    Map<String, Json> hostEvidence = Map.of();

    InvocationResult(ResultKind kind) {
        this.kind = kind;
    }

    /** The result kind. */
    public ResultKind kind() {
        return kind;
    }

    /** The value of a successful outcome, else null. */
    public Value value() {
        return value;
    }

    /** Core's diagnostics, for an error outcome or a fatal frame. */
    public List<Diagnostic> diagnostics() {
        return diagnostics;
    }

    /** Core's evidence terminal name for an outcome, else null. */
    public String terminal() {
        return terminal;
    }

    /** Core's evidence for an outcome, else null. */
    public Map<String, Json> evidence() {
        return evidence;
    }

    /** Every frame Core sent, in order. */
    public List<Map<String, Json>> coreFrames() {
        return Collections.unmodifiableList(coreFrames);
    }

    /** Every frame the host sent, in order. */
    public List<Map<String, Json>> hostFrames() {
        return Collections.unmodifiableList(hostFrames);
    }

    /** The effect requests Core made. */
    public List<EffectRequest> requests() {
        return requests;
    }

    /** The worker's exit status, when known and not ended by the host's SIGKILL. */
    public Integer exitStatus() {
        return exitStatus;
    }

    /** {@code "SIGKILL"} when the host killed the worker; see {@code Worker#signal()}. */
    public String signal() {
        return signal;
    }

    /** The retained stderr prefix. */
    public byte[] stderr() {
        return Arrays.copyOf(stderr, stderr.length);
    }

    /** The host's evidence ({@code jacquard-host-java-evidence-v0}); empty on success. */
    public Map<String, Json> hostEvidence() {
        return hostEvidence;
    }

    /** Whether Core returned a successful outcome. */
    public boolean ok() {
        return kind == ResultKind.OK;
    }

    private String evidenceText(String key) {
        Json value = hostEvidence.get(key);
        return value == null ? null : value.asString();
    }

    /** The first Core diagnostic code, else the host evidence code. */
    public String code() {
        if (!diagnostics.isEmpty() && !diagnostics.get(0).code().isEmpty()) {
            return diagnostics.get(0).code();
        }
        String code = evidenceText("code");
        return code == null ? "" : code;
    }

    /** A summary for people. */
    public String message() {
        if (ok()) {
            return "Jacquard invocation succeeded";
        }
        String detail;
        if (!diagnostics.isEmpty()) {
            detail = diagnostics.get(0).summary() + " " + diagnostics.get(0).cause();
        } else {
            String text = evidenceText("detail");
            detail = text == null ? kind.wireName() : text;
        }
        String code = code();
        return (code.isEmpty() ? kind.wireName() : code) + ": " + detail;
    }

    /** The value, or an {@link InvocationException} classifying the failure. */
    public Value valueOrThrow() throws InvocationException {
        if (ok()) {
            return value;
        }
        throw new InvocationException(this);
    }

    @Override
    public String toString() {
        return "InvocationResult[" + kind.wireName() + ", " + message() + "]";
    }
}
