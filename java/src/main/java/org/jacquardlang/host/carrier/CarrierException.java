package org.jacquardlang.host.carrier;

import org.jacquardlang.host.ProtocolException;

/**
 * A carrier failure: either a loss (the worker could not start, closed a
 * pipe, exited unsuccessfully, was aborted, or outlived its deadline) or a
 * frame that violated the protocol.
 */
public final class CarrierException extends Exception {
    private static final long serialVersionUID = 1L;

    private final boolean timeout;
    private final boolean aborted;
    private final ProtocolException protocolError;

    private CarrierException(String message, boolean timeout, boolean aborted, ProtocolException protocolError) {
        super(message, protocolError);
        this.timeout = timeout;
        this.aborted = aborted;
        this.protocolError = protocolError;
    }

    /** A carrier loss. */
    public static CarrierException loss(String message) {
        return new CarrierException(message, false, false, null);
    }

    static CarrierException timeout(String message) {
        return new CarrierException(message, true, false, null);
    }

    static CarrierException aborted(String message) {
        return new CarrierException(message, false, true, null);
    }

    /** A frame that violated the protocol. */
    public static CarrierException protocol(ProtocolException error) {
        return new CarrierException(error.getMessage(), false, false, error);
    }

    /** Whether this is a loss rather than a protocol violation. */
    public boolean isLoss() {
        return protocolError == null;
    }

    /** Whether the worker outlived its lifetime deadline. */
    public boolean isTimeout() {
        return timeout;
    }

    /** Whether the invocation was cancelled or the calling thread interrupted. */
    public boolean isAborted() {
        return aborted;
    }

    /** The protocol violation, or null for a loss. */
    public ProtocolException protocolError() {
        return protocolError;
    }
}
