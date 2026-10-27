package org.jacquardlang.host.carrier;

/**
 * Decides whether a stdin chunk may start: never once input is closed, the
 * lifetime deadline has passed, or cancellation was requested, even before
 * the host notices the deadline or the request.
 */
final class Gate {
    private volatile boolean closed;
    private final long deadline;
    private final Cancel cancel;

    Gate(long deadline, Cancel cancel) {
        this.deadline = deadline;
        this.cancel = cancel;
    }

    boolean admits() {
        return !closed && System.nanoTime() - deadline < 0 && !cancel.isCancelled();
    }

    void close() {
        closed = true;
    }
}
