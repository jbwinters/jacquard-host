package org.jacquardlang.host.protocol;

/** The reason a pending request is withdrawn. */
public enum CancelReason {
    CANCELLED("cancelled"),
    TIMEOUT("timeout"),
    HOST_SHUTDOWN("host_shutdown");

    private final String wireName;

    CancelReason(String wireName) {
        this.wireName = wireName;
    }

    /** The wire name. */
    public String wireName() {
        return wireName;
    }
}
