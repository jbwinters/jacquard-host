package org.jacquardlang.host.kit;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;

import org.jacquardlang.host.json.Json;

/** One positive or hostile recording. */
public record Transcript(String name, String kind, String phase, Map<String, Json> expect, Map<String, Json> observed,
        String status, long exitStatus, List<Map<String, Json>> coreOutput, List<Step> hostInput) {
    public Transcript {
        expect = Json.sorted(expect);
        observed = Json.sorted(observed);
        List<Map<String, Json>> frames = new ArrayList<>();
        for (Map<String, Json> frame : coreOutput) {
            frames.add(Json.sorted(frame));
        }
        coreOutput = List.copyOf(frames);
        hostInput = List.copyOf(hostInput);
    }

    /** This recording with other expectations. */
    public Transcript withExpect(Map<String, Json> value) {
        return new Transcript(name, kind, phase, value, observed, status, exitStatus, coreOutput, hostInput);
    }

    /** This recording with other Core output. */
    public Transcript withCoreOutput(List<Map<String, Json>> value) {
        return new Transcript(name, kind, phase, expect, observed, status, exitStatus, value, hostInput);
    }
}
