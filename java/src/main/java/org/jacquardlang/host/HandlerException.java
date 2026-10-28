package org.jacquardlang.host;

import java.util.Objects;

import org.jacquardlang.host.protocol.FailureCategory;

/**
 * A handler's structured refusal or failure, reported to Core with its
 * category and message. Any other exception a handler throws is reported as
 * completion unknown, without its message.
 */
public final class HandlerException extends Exception {
    private static final long serialVersionUID = 1L;

    private final FailureCategory category;

    /** A failure in one of the four permitted categories, with a message for Core. */
    public HandlerException(FailureCategory category, String message) {
        super(Objects.requireNonNull(message, "message"));
        this.category = Objects.requireNonNull(category, "category");
    }

    /** The failure category. */
    public FailureCategory category() {
        return category;
    }

    /** The host does not provide the operation (not started). */
    public static HandlerException unsupported(String message) {
        return new HandlerException(FailureCategory.UNSUPPORTED_OPERATION, message);
    }

    /** The host refused to authorize the operation (not started). */
    public static HandlerException refused(String message) {
        return new HandlerException(FailureCategory.REFUSED_AUTHORITY, message);
    }

    /** An outside action failed without effect. */
    public static HandlerException outsideFailed(String message) {
        return new HandlerException(FailureCategory.OUTSIDE_FAILURE, message);
    }

    /** An outside action's completion is unknown. */
    public static HandlerException completionUnknown(String message) {
        return new HandlerException(FailureCategory.COMPLETION_UNKNOWN, message);
    }
}
