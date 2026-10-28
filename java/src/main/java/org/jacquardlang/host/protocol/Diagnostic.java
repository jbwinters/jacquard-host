package org.jacquardlang.host.protocol;

import java.util.Map;

import org.jacquardlang.host.json.Json;

/**
 * One {@code jacquard-diagnostic-v1} entry; {@code raw} keeps the exact
 * object. {@code code} is empty when Core sent null.
 */
public record Diagnostic(String code, String domain, String severity, String summary, String cause, String nextStep,
        Map<String, Json> raw) {
    public Diagnostic {
        raw = Json.sorted(raw);
    }
}
