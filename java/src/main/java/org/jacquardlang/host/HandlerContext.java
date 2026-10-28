package org.jacquardlang.host;

import java.util.function.BooleanSupplier;

import org.jacquardlang.host.carrier.Cancel;

/** What a handler sees: whether the invocation is still live. */
public final class HandlerContext {
    private final BooleanSupplier expired;
    private final BooleanSupplier ended;
    private final Cancel cancel;

    HandlerContext(BooleanSupplier expired, BooleanSupplier ended, Cancel cancel) {
        this.expired = expired;
        this.ended = ended;
        this.cancel = cancel;
    }

    /**
     * A context for calling a handler outside an invocation, such as in a
     * test: it is cancelled once {@code cancel} is.
     */
    public static HandlerContext of(Cancel cancel) {
        java.util.Objects.requireNonNull(cancel, "cancel");
        return new HandlerContext(() -> false, () -> false, cancel);
    }

    /**
     * Whether the worker expired, the caller cancelled, or the invocation
     * ended. Handlers should stop; the host no longer waits for them.
     */
    public boolean isCancelled() {
        return expired.getAsBoolean() || ended.getAsBoolean() || cancel.isCancelled();
    }
}
