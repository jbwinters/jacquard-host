package org.jacquardlang.host.carrier;

/**
 * Decides whether a stdin chunk may start: never once input is closed, the
 * lifetime deadline has passed, cancellation was requested, or the calling
 * thread is interrupted, even before the host notices any of these. It is
 * read from other threads, and is also the stop check for handler threads.
 */
final class Gate {
    private volatile boolean closed;
    private final long deadline;
    private final Cancel cancel;
    /** The thread that drives the worker; its interrupt stops the call. */
    private final Thread caller;
    /** Set before the caller consumes its interrupt. */
    private volatile boolean callerInterrupted;

    Gate(long deadline, Cancel cancel, Thread caller) {
        this.deadline = deadline;
        this.cancel = cancel;
        this.caller = caller;
    }

    /**
     * Whether the call is cancelled, interrupted, or past its lifetime. The
     * interrupt status is read before the recorded interrupt, which is set
     * before the status is cleared, so an interrupt is never missed.
     */
    boolean stopped() {
        return System.nanoTime() - deadline >= 0 || cancel.isCancelled() || caller.isInterrupted() || callerInterrupted;
    }

    /** Records that the caller was interrupted; called before it clears its status. */
    void interrupted() {
        callerInterrupted = true;
    }

    boolean admits() {
        return !closed && !stopped();
    }

    void close() {
        closed = true;
    }
}
