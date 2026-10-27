package org.jacquardlang.host;

/** The kind of an invocation's result. */
public enum ResultKind {
    OK("ok"),
    ERROR("error"),
    FATAL("fatal"),
    SHUTDOWN_ACK("shutdown_ack"),
    CARRIER_LOSS("carrier_loss"),
    PROTOCOL_ERROR("protocol_error"),
    HOST_ERROR("host_error");

    private final String wireName;

    ResultKind(String wireName) {
        this.wireName = wireName;
    }

    /** The kind's name, as the other adapters spell it. */
    public String wireName() {
        return wireName;
    }
}
