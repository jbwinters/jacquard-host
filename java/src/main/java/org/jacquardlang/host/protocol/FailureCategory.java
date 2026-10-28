package org.jacquardlang.host.protocol;

/** The category of a host effect failure. */
public enum FailureCategory {
    UNSUPPORTED_OPERATION("unsupported_operation", "not_started"),
    REFUSED_AUTHORITY("refused_authority", "not_started"),
    OUTSIDE_FAILURE("outside_failure", "failed"),
    COMPLETION_UNKNOWN("completion_unknown", "unknown");

    private final String wireName;
    private final String completion;

    FailureCategory(String wireName, String completion) {
        this.wireName = wireName;
        this.completion = completion;
    }

    /** The wire name. */
    public String wireName() {
        return wireName;
    }

    /** The one completion the protocol permits with this category. */
    public String completion() {
        return completion;
    }
}
