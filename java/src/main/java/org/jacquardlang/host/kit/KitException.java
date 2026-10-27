package org.jacquardlang.host.kit;

/** The pinned kit could not be loaded or verified. */
public final class KitException extends Exception {
    private static final long serialVersionUID = 1L;

    /** A failure with its reason. */
    public KitException(String message) {
        super(message);
    }
}
