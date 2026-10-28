package org.jacquardlang.host.kit;

import java.util.Map;

import org.jacquardlang.host.json.Json;

/**
 * One host-side action in a recording.
 *
 * @param direction {@code "core_to_host"} for a read, else a host write
 * @param rawHex raw carrier bytes to write, or null
 * @param frame the frame to write; empty for reads and raw writes
 * @param afterTerminal whether the recording sends this after Core's terminal
 */
public record Step(String direction, String rawHex, Map<String, Json> frame, boolean afterTerminal) {
    public Step {
        frame = Json.sorted(frame);
    }
}
