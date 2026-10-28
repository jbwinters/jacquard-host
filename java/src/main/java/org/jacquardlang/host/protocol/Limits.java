package org.jacquardlang.host.protocol;

import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

import org.jacquardlang.host.ProtocolException;
import org.jacquardlang.host.json.Json;

/** The v0 ceilings a host selects; each must lie in {@code 1..=} its hard value. */
public record Limits(
        int maxArguments,
        int maxCollectionItems,
        int maxDiagnosticBytes,
        int maxDiagnostics,
        int maxEffectRequests,
        int maxEffects,
        int maxFrameBytes,
        int maxHostMessageBytes,
        int maxJsonDepth,
        int maxOperations,
        int maxStderrBytes,
        int maxTextBytes,
        int maxValueNodes) {

    /** The frozen v0 ceilings Core advertises in {@code core_hello}. */
    public static final Limits HARD = new Limits(64, 1024, 65536, 32, 1024, 64, 1_048_576, 4096, 64, 256, 65536, 262_144, 4096);

    /** The largest integer the other adapters accept exactly in limits and ordinals. */
    static final long MAX_SAFE_INTEGER = (1L << 53) - 1;

    /** The wire names, in protocol (sorted) order. */
    public static final List<String> NAMES = List.of(
            "max_arguments",
            "max_collection_items",
            "max_diagnostic_bytes",
            "max_diagnostics",
            "max_effect_requests",
            "max_effects",
            "max_frame_bytes",
            "max_host_message_bytes",
            "max_json_depth",
            "max_operations",
            "max_stderr_bytes",
            "max_text_bytes",
            "max_value_nodes");

    private int[] values() {
        return new int[] {
            maxArguments, maxCollectionItems, maxDiagnosticBytes, maxDiagnostics, maxEffectRequests, maxEffects,
            maxFrameBytes, maxHostMessageBytes, maxJsonDepth, maxOperations, maxStderrBytes, maxTextBytes, maxValueNodes
        };
    }

    private static Limits of(int[] v) {
        return new Limits(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12]);
    }

    /** These limits with the one named by its wire name, such as {@code "max_frame_bytes"}, replaced. */
    public Limits with(String name, int value) {
        int index = NAMES.indexOf(name);
        if (index < 0) {
            throw new IllegalArgumentException("unknown limit " + name);
        }
        int[] v = values();
        v[index] = value;
        return of(v);
    }

    /** E1602 for a limit outside {@code 1..=} its hard ceiling. */
    public void validate() throws ProtocolException {
        int[] v = values();
        int[] hard = HARD.values();
        for (int i = 0; i < v.length; i++) {
            if (v[i] < 1 || v[i] > hard[i]) {
                throw ProtocolException.exceeded("limit " + NAMES.get(i) + " exceeds its bounds");
            }
        }
    }

    /** The wire object for {@code host_select}. */
    public Map<String, Json> toJson() {
        Map<String, Json> fields = new LinkedHashMap<>();
        int[] v = values();
        for (int i = 0; i < v.length; i++) {
            fields.put(NAMES.get(i), Json.of(v[i]));
        }
        return Json.sorted(fields);
    }

    /** Parses and validates an exact limits object. */
    public static Limits fromJson(Json value) throws ProtocolException {
        Map<String, Json> fields = Protocol.exact(value, NAMES.toArray(String[]::new));
        int[] hard = HARD.values();
        int[] parsed = new int[hard.length];
        for (int i = 0; i < hard.length; i++) {
            String name = NAMES.get(i);
            Long number = fields.get(name).asInt();
            if (number == null || number < -MAX_SAFE_INTEGER || number > MAX_SAFE_INTEGER) {
                throw ProtocolException.malformed("limit " + name + " must be an integer");
            }
            if (number < 1 || number > hard[i]) {
                throw ProtocolException.exceeded("limit " + name + " exceeds its bounds");
            }
            parsed[i] = (int) (long) number;
        }
        return of(parsed);
    }
}
