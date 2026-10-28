package org.jacquardlang.host.sdk;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

import org.jacquardlang.host.Failure;
import org.jacquardlang.host.InvocationException;
import org.jacquardlang.host.InvocationResult;

/**
 * Every SDK failure. {@link #kind()} says what failed; an invocation that ran
 * keeps its raw {@link #result()}.
 */
public final class JacquardException extends Exception {
    private static final long serialVersionUID = 1L;

    /** What failed. */
    public enum Kind {
        /** The program could not be checked, hashed, installed, or loaded from its cache. */
        PREPARATION,
        /**
         * A mapping or binding problem: an unknown name, a missing handler, or a
         * successful Core result that does not decode into the requested type
         * ({@link #result()} is then set).
         */
        BINDING,
        /** A local input that cannot be sent; no worker started. */
        INVALID_ARGUMENT,
        /** The invocation ran and did not succeed; {@link #failure()} classifies it. */
        INVOCATION
    }

    private final Kind kind;
    private final transient InvocationResult result;
    private final int batchIndex;
    private final transient List<Object> completed;

    private JacquardException(Kind kind, String message, InvocationResult result, Throwable cause, int batchIndex,
            List<Object> completed) {
        super(message, cause);
        this.kind = kind;
        this.result = result;
        this.batchIndex = batchIndex;
        this.completed = completed;
    }

    static JacquardException preparation(String message) {
        return new JacquardException(Kind.PREPARATION, message, null, null, -1, List.of());
    }

    static JacquardException binding(String message) {
        return new JacquardException(Kind.BINDING, message, null, null, -1, List.of());
    }

    /** A successful Core result that could not be mapped; the result is kept. */
    static JacquardException binding(String message, InvocationResult result) {
        return new JacquardException(Kind.BINDING, message, result, null, -1, List.of());
    }

    static JacquardException invalid(String message) {
        return new JacquardException(Kind.INVALID_ARGUMENT, message, null, null, -1, List.of());
    }

    /** An invalid argument at a value's location, such as {@code shipping-cost.argument[0]}. */
    static JacquardException invalidAt(String path, String message) {
        return invalid(path + ": " + message);
    }

    /** A failed invocation; {@code result} is not ok. */
    static JacquardException invocation(InvocationResult result) {
        InvocationException cause;
        try {
            result.valueOrThrow();
            throw new IllegalArgumentException("the invocation succeeded");
        } catch (InvocationException error) {
            cause = error;
        }
        return new JacquardException(Kind.INVOCATION, result.message(), result, cause, -1, List.of());
    }

    /**
     * {@code error} as the failure of batch {@code index}, after the decoded
     * values of the batches before it. The copy keeps null items, which is how
     * {@link Codec#UNIT} decodes unit.
     */
    static JacquardException batch(int index, List<Object> completed, JacquardException error) {
        return new JacquardException(error.kind, "batch " + index + ": " + error.getMessage(), error.result, error.getCause(), index,
                Collections.unmodifiableList(new ArrayList<>(completed)));
    }

    /** What failed. */
    public Kind kind() {
        return kind;
    }

    /** The raw result, when a worker ran: always for {@link Kind#INVOCATION}, and for a result that did not decode. */
    public InvocationResult result() {
        return result;
    }

    /** How the invocation failed, for {@link Kind#INVOCATION}; otherwise null. */
    public Failure failure() {
        return getCause() instanceof InvocationException error ? error.failure() : null;
    }

    /**
     * For {@link Function#mapBatches}: the index of the batch that failed, or -1
     * when a batch was refused before any worker started (and for every other
     * failure).
     */
    public int batchIndex() {
        return batchIndex;
    }

    /**
     * For {@link Function#mapBatches}: the decoded values of the batches that
     * completed before the failed one, in order, unmodifiable; a unit item is
     * null. Nothing is retried.
     */
    public List<Object> completed() {
        return completed;
    }
}
