package org.jacquardlang.host.carrier;

import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.time.Duration;
import java.util.Arrays;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.locks.LockSupport;

/**
 * Runs one short command to completion, bounded in time and output, the way
 * the SDK runs Core's {@code --version}, {@code check}, {@code hash}, and
 * {@code run} commands.
 *
 * <p>Like a {@link Worker}, the command's stdout and stderr are host-owned
 * FIFOs on Unix (see {@link OutputPipes}), read only after {@code available()}
 * reports bytes, on the caller's thread, in polls of 0.1 to 5 ms that recheck
 * the timeout, the {@link Cancel}, and the caller's interrupt status, which is
 * never cleared. Its stdin is the null device. Once the command has exited,
 * what is left in its pipes (at most {@link #EXIT_DRAIN} more bytes of each)
 * is read and the host closes its ends; a descendant that still holds the
 * output neither holds the caller nor is waited for, and its next write
 * fails. On timeout, cancellation, or interruption the command's process
 * tree is killed: a snapshot of its descendants and the command itself. As
 * for the worker, a descendant that was already reparented keeps running. On
 * other systems the JDK's pipes are used, with their exit-time drain.
 */
public final class Command {
    private Command() {}

    /** How long the host waits for a killed command to exit. */
    private static final long EXIT_GRACE = TimeUnit.SECONDS.toNanos(1);
    private static final long POLL_MIN = TimeUnit.MICROSECONDS.toNanos(100);
    private static final long POLL_MAX = TimeUnit.MILLISECONDS.toNanos(5);
    /** The most of each stream one poll reads, so a fast writer cannot hold the caller. */
    private static final int SLICE = 1 << 20;
    /** The most of each stream read after the command has exited. */
    public static final int EXIT_DRAIN = 4 << 20;

    /**
     * A command that exited.
     *
     * @param exitStatus the exit status; the JDK reports a signal N as 128 + N
     * @param stdout at most the requested limit of stdout
     * @param stdoutOverflow whether stdout was longer than its limit
     * @param stderr at most the requested limit of stderr
     */
    public record Output(int exitStatus, byte[] stdout, boolean stdoutOverflow, byte[] stderr) {
        public Output {
            stdout = stdout.clone();
            stderr = stderr.clone();
        }

        @Override
        public byte[] stdout() {
            return stdout.clone();
        }

        @Override
        public byte[] stderr() {
            return stderr.clone();
        }
    }

    /** A bounded prefix of one stream. */
    private static final class Capture {
        private final InputStream stream;
        private final int limit;
        private final byte[] chunk = new byte[8192];
        private byte[] kept = new byte[0];
        private int length;
        private boolean overflow;
        private boolean ended;

        Capture(InputStream stream, int limit) {
            this.stream = stream;
            this.limit = limit;
        }

        /** Reads at most {@code budget} bytes already in the pipe; returns how many. Never blocks. */
        int step(int budget) {
            int total = 0;
            if (ended) {
                return 0;
            }
            try {
                while (total < budget) {
                    int available = stream.available();
                    if (available <= 0) {
                        break;
                    }
                    int n = stream.read(chunk, 0, Math.min(Math.min(available, chunk.length), budget - total));
                    if (n < 0) {
                        ended = true;
                        break;
                    }
                    keep(n);
                    total += n;
                }
            } catch (IOException error) {
                ended = true;
            }
            return total;
        }

        private void keep(int n) {
            int room = limit - length;
            if (n > room) {
                overflow = true;
            }
            int take = Math.min(n, room);
            if (take > 0) {
                if (length + take > kept.length) {
                    kept = Arrays.copyOf(kept, Math.min(limit, Math.max(length + take, kept.length * 2)));
                }
                System.arraycopy(chunk, 0, kept, length, take);
                length += take;
            }
        }

        byte[] bytes() {
            return Arrays.copyOf(kept, length);
        }
    }

    /**
     * Runs {@code command}, adding {@code environment} to the inherited
     * environment, and returns once it has exited.
     *
     * @param timeout bounds the whole run, from making the pipes to the exit
     * @param stdoutLimit how much stdout is kept; the rest is read and counted as overflow
     * @param stderrLimit how much stderr is kept
     * @throws CarrierException a loss when the command or its pipes cannot
     *     start; {@link CarrierException#isTimeout() a timeout} when it does
     *     not exit in time; {@link CarrierException#isAborted() aborted} when
     *     {@code cancel} is cancelled or the calling thread is interrupted,
     *     whose interrupt status stays set. The command is killed in the last
     *     two cases.
     * @throws IllegalArgumentException for an empty command or a timeout that is not positive
     */
    public static Output run(List<String> command, Map<String, String> environment, Duration timeout, int stdoutLimit,
            int stderrLimit, Cancel cancel) throws CarrierException {
        Objects.requireNonNull(cancel, "cancel");
        if (command.isEmpty() || command.stream().anyMatch(String::isEmpty)) {
            throw new IllegalArgumentException("command must be a nonempty executable argument list");
        }
        if (timeout.isZero() || timeout.isNegative()) {
            throw new IllegalArgumentException("timeout must be positive");
        }
        long budget = timeout.compareTo(Duration.ofNanos(Long.MAX_VALUE / 4)) > 0 ? Long.MAX_VALUE / 4 : timeout.toNanos();
        long expires = System.nanoTime() + budget;
        ProcessBuilder builder = new ProcessBuilder(command);
        if (environment != null) {
            builder.environment().putAll(environment);
        }
        builder.redirectInput(ProcessBuilder.Redirect.from(new File(OutputPipes.SUPPORTED ? "/dev/null" : "NUL")));
        OutputPipes pipes = null;
        Process process = null;
        try {
            if (OutputPipes.SUPPORTED) {
                try {
                    pipes = OutputPipes.create("jacquard-command-", expires, cancel);
                } catch (IOException | RuntimeException error) {
                    throw CarrierException.loss("could not create the command's output pipes (" + error.getMessage() + ")");
                }
                if (pipes == null) {
                    throw stop(expires, cancel);
                }
                pipes.redirect(builder);
            }
            CarrierException stopped = stopped(expires, cancel);
            if (stopped != null) {
                throw stopped;
            }
            try {
                process = builder.start();
            } catch (IOException | RuntimeException error) {
                throw CarrierException.loss("could not start " + command.get(0) + " (" + error.getMessage() + ")");
            }
        } finally {
            if (pipes != null) {
                if (process == null) {
                    pipes.close();
                }
                pipes.unlink();
            }
        }
        try {
            return await(process, pipes, expires, cancel, stdoutLimit, stderrLimit);
        } finally {
            if (process.isAlive()) {
                killTree(process);
                awaitExit(process, EXIT_GRACE);
            }
            if (pipes != null) {
                // A descendant still writing has no reader now, so its next write fails.
                pipes.close();
            } else {
                closeQuietly(process.getInputStream());
                closeQuietly(process.getErrorStream());
            }
        }
    }

    private static Output await(Process process, OutputPipes pipes, long expires, Cancel cancel, int stdoutLimit, int stderrLimit)
            throws CarrierException {
        Capture stdout = new Capture(pipes != null ? pipes.stdout() : process.getInputStream(), stdoutLimit);
        Capture stderr = new Capture(pipes != null ? pipes.stderr() : process.getErrorStream(), stderrLimit);
        long idle = POLL_MIN;
        while (true) {
            // The exit is observed before the pipes are read, so everything
            // the command itself wrote is read below.
            boolean exited = !process.isAlive();
            int read = stdout.step(SLICE) + stderr.step(SLICE);
            if (exited) {
                stdout.step(EXIT_DRAIN);
                stderr.step(EXIT_DRAIN);
                return new Output(process.exitValue(), stdout.bytes(), stdout.overflow, stderr.bytes());
            }
            CarrierException stopped = stopped(expires, cancel);
            if (stopped != null) {
                throw stopped;
            }
            if (read > 0) {
                idle = POLL_MIN;
            } else {
                long slice = Math.min(idle, expires - System.nanoTime());
                if (slice > 0 && !Thread.currentThread().isInterrupted()) {
                    // Parking leaves the interrupt status set.
                    LockSupport.parkNanos(slice);
                }
                idle = Math.min(idle * 2, POLL_MAX);
            }
        }
    }

    /** Why the run must stop now, or null. Reads the interrupt status without clearing it. */
    private static CarrierException stopped(long expires, Cancel cancel) {
        if (cancel.isCancelled() || Thread.currentThread().isInterrupted()) {
            return CarrierException.aborted("command aborted");
        }
        if (System.nanoTime() - expires >= 0) {
            return CarrierException.timeout("command did not exit in time");
        }
        return null;
    }

    /** The reason setup stopped. */
    private static CarrierException stop(long expires, Cancel cancel) {
        CarrierException stopped = stopped(expires, cancel);
        return stopped != null ? stopped : CarrierException.timeout("command did not exit in time");
    }

    /** Kills the command and a snapshot of its descendants, taken while it is alive. */
    private static void killTree(Process process) {
        List<ProcessHandle> tree;
        try {
            tree = process.descendants().toList();
        } catch (RuntimeException error) {
            tree = List.of();
        }
        process.toHandle().destroyForcibly();
        for (ProcessHandle handle : tree) {
            try {
                handle.destroyForcibly();
            } catch (RuntimeException ignored) {
                // Not permitted, or already gone.
            }
        }
    }

    /**
     * Waits up to {@code nanos} for a killed command to exit. An interrupt
     * that arrives or was already set is kept and restored afterwards.
     */
    private static void awaitExit(Process process, long nanos) {
        boolean interrupted = Thread.interrupted();
        long end = System.nanoTime() + nanos;
        try {
            while (process.isAlive()) {
                long left = end - System.nanoTime();
                if (left <= 0) {
                    return;
                }
                try {
                    process.waitFor(left, TimeUnit.NANOSECONDS);
                } catch (InterruptedException error) {
                    interrupted = true;
                }
            }
        } finally {
            if (interrupted) {
                Thread.currentThread().interrupt();
            }
        }
    }

    private static void closeQuietly(InputStream stream) {
        try {
            stream.close();
        } catch (IOException ignored) {
            // Nothing is waiting on it.
        }
    }
}
