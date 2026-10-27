package org.jacquardlang.host;

/** How an unsuccessful invocation failed. */
public enum Failure {
    /** The worker's lifetime deadline expired. */
    DEADLINE_EXCEEDED,
    /** The carrier was lost: spawn failure, closed pipes, bad exit, or abort. */
    WORKER_LOST,
    /** An outside operation failed or its completion is unknown. */
    OPERATION_FAILED,
    /** Core reported a diagnostic, or a frame was refused. */
    OTHER
}
