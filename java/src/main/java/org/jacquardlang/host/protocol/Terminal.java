package org.jacquardlang.host.protocol;

import java.util.List;
import java.util.Map;

import org.jacquardlang.host.json.Json;

/** Core's final frame. */
public sealed interface Terminal extends CoreFrame permits Terminal.Outcome, Terminal.Fatal, Terminal.ShutdownAck {
    /**
     * An invocation outcome: a value (null for an error result), or
     * diagnostics, with Core's evidence and terminal name.
     */
    record Outcome(Value value, List<Diagnostic> diagnostics, String terminal, Map<String, Json> evidence,
            Map<String, Json> raw) implements Terminal {
        public Outcome {
            diagnostics = List.copyOf(diagnostics);
            evidence = Json.sorted(evidence);
            raw = Json.sorted(raw);
        }
    }

    /** A session-level failure. */
    record Fatal(List<Diagnostic> diagnostics, Map<String, Json> raw) implements Terminal {
        public Fatal {
            diagnostics = List.copyOf(diagnostics);
            raw = Json.sorted(raw);
        }
    }

    /** The acknowledgement of a host shutdown. */
    record ShutdownAck(Map<String, Json> raw) implements Terminal {
        public ShutdownAck {
            raw = Json.sorted(raw);
        }
    }
}
