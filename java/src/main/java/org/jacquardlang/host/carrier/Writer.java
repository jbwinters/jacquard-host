package org.jacquardlang.host.carrier;

import java.io.IOException;
import java.io.OutputStream;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;

/**
 * The worker's one stdin thread. A pipe write cannot be interrupted in Java,
 * so writes run here and the caller waits for each under the worker's
 * lifetime. Each frame is written in chunks of at most {@link #CHUNK} bytes,
 * and the gate admits each chunk only while input is open, the lifetime has
 * not ended, and cancellation has not been requested. A chunk already
 * admitted may still complete after the gate closes, and the worker may read
 * it, until the caller's next poll kills the worker (at most one poll slice).
 */
final class Writer {
    /** The most bytes one admitted write may carry. */
    static final int CHUNK = 4096;
    private static final Object CLOSE = new Object();

    private record Job(byte[] bytes, CompletableFuture<IOException> done) {}

    private final OutputStream out;
    private final Gate gate;
    private final LinkedBlockingQueue<Object> queue = new LinkedBlockingQueue<>();
    private final Thread thread;
    private boolean finished;

    Writer(OutputStream out, Gate gate) {
        this.out = out;
        this.gate = gate;
        this.thread = new Thread(this::run, "jacquard-worker-stdin");
        this.thread.setDaemon(true);
    }

    void start() {
        thread.start();
    }

    /** Queues one write; null once the thread has stopped. */
    synchronized CompletableFuture<IOException> submit(byte[] bytes) {
        if (finished) {
            return null;
        }
        CompletableFuture<IOException> done = new CompletableFuture<>();
        queue.add(new Job(bytes, done));
        return done;
    }

    /** Asks the thread to close stdin after any queued write, and stop. */
    void close() {
        queue.add(CLOSE);
    }

    /** Waits up to {@code nanos} for the thread to stop; true once it has. */
    boolean join(long nanos) {
        if (thread.getState() == Thread.State.NEW) {
            return true;
        }
        boolean interrupted = false;
        long end = System.nanoTime() + nanos;
        try {
            while (thread.isAlive()) {
                long left = end - System.nanoTime();
                if (left <= 0) {
                    break;
                }
                try {
                    TimeUnit.NANOSECONDS.timedJoin(thread, left);
                } catch (InterruptedException error) {
                    interrupted = true;
                }
            }
        } finally {
            if (interrupted) {
                Thread.currentThread().interrupt();
            }
        }
        return !thread.isAlive();
    }

    boolean isAlive() {
        return thread.isAlive();
    }

    private void run() {
        try {
            while (true) {
                Object next = queue.take();
                if (next == CLOSE) {
                    return;
                }
                Job job = (Job) next;
                IOException failure = write(job.bytes());
                job.done().complete(failure);
                if (failure != null) {
                    return;
                }
            }
        } catch (InterruptedException error) {
            // Stop; nothing interrupts this thread except the JVM shutting down.
        } finally {
            try {
                out.close();
            } catch (IOException ignored) {
                // The worker may already have closed its end.
            }
            synchronized (this) {
                finished = true;
                for (Object left; (left = queue.poll()) != null; ) {
                    if (left instanceof Job job) {
                        job.done().complete(new IOException("worker input is closed"));
                    }
                }
            }
        }
    }

    private IOException write(byte[] bytes) {
        try {
            for (int offset = 0; offset < bytes.length; offset += CHUNK) {
                if (!gate.admits()) {
                    return new IOException("worker input closed by the host");
                }
                out.write(bytes, offset, Math.min(CHUNK, bytes.length - offset));
                out.flush();
            }
            return null;
        } catch (IOException error) {
            return error;
        }
    }
}
