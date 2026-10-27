package org.jacquardlang.host;

import org.jacquardlang.host.json.Json;

/** An unsuccessful invocation. */
public final class InvocationException extends Exception {
    private static final long serialVersionUID = 1L;

    private final transient InvocationResult result;

    InvocationException(InvocationResult result) {
        super(result.message());
        this.result = result;
    }

    /** The result, with Core's frames and the host's evidence. */
    public InvocationResult result() {
        return result;
    }

    /** The classification. */
    public Failure failure() {
        if (Json.of("deadline").equals(result.hostEvidence().get("reason"))) {
            return Failure.DEADLINE_EXCEEDED;
        }
        if (result.kind() == ResultKind.CARRIER_LOSS) {
            return Failure.WORKER_LOST;
        }
        String terminal = result.terminal();
        if ("host_failure".equals(terminal) || "completion_unknown".equals(terminal)) {
            return Failure.OPERATION_FAILED;
        }
        return Failure.OTHER;
    }
}
