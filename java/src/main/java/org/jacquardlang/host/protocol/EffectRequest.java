package org.jacquardlang.host.protocol;

import java.util.List;
import java.util.Map;

import org.jacquardlang.host.json.Json;

/** Core asking the host to perform one operation. */
public record EffectRequest(long ordinal, Hash effect, Hash operation, List<Value> arguments, Map<String, Json> raw)
        implements CoreFrame {
    public EffectRequest {
        arguments = List.copyOf(arguments);
        raw = Json.sorted(raw);
    }
}
