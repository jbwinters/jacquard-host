package org.jacquardlang.host.carrier;

import java.util.concurrent.atomic.AtomicBoolean;

/** A cooperative cancellation token shared with the caller and handlers. */
public final class Cancel {
    private final AtomicBoolean cancelled = new AtomicBoolean();

    /** A token that is not cancelled. */
    public Cancel() {}

    /** Requests cancellation. */
    public void cancel() {
        cancelled.set(true);
    }

    /** Whether cancellation was requested. */
    public boolean isCancelled() {
        return cancelled.get();
    }
}
