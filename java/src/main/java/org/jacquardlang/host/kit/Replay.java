package org.jacquardlang.host.kit;

import java.util.List;
import java.util.Map;

import org.jacquardlang.host.json.Json;

/**
 * The observed result of replaying one recording.
 *
 * @param status {@code "conforms"} or {@code "diverges"}
 */
public record Replay(String name, boolean matchesRecording, String status, List<String> differences, List<String> proseDrift,
        Map<String, Json> summary, List<Map<String, Json>> coreFrames, Integer exitStatus, String signal, byte[] stderr) {
    public Replay {
        differences = List.copyOf(differences);
        proseDrift = List.copyOf(proseDrift);
        summary = Json.sorted(summary);
        coreFrames = List.copyOf(coreFrames);
        stderr = stderr.clone();
    }

    @Override
    public byte[] stderr() {
        return stderr.clone();
    }
}
