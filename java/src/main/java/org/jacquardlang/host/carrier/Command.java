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
 * never cleared. Its stdin is the null device. While the command runs, its
 * descendants are recorded about every 10 ms, each by pid and start time.
 *
 * <p>Once the command has exited, the host waits up to {@link #OUTPUT_GRACE}
 * for its output to close: on Linux, until no other process of this user
 * holds either FIFO, which it finds through {@code /proc/<pid>/fd}. A process
 * still holding one then fails the command with "its output did not close",
 * and is killed if it started no earlier than the command (or than this JVM,
 * when the command exited before its start time was read) and still has the
 * start time it was found with. Then what is left in the pipes (at most
 * {@link #EXIT_DRAIN} more bytes of each) is read and the host closes its
 * ends. Elsewhere there is no {@code /proc}: a descendant still holding the
 * output is not detected, the host closes its ends after the exit, and that
 * descendant's next write fails.
 *
 * <p>Whatever the outcome, every recorded descendant still alive with the
 * start time it was recorded with is killed. On timeout, cancellation, or
 * interruption the command's process tree is killed too: a snapshot of its
 * descendants and the command itself. A descendant that was never recorded
 * (one started and reparented between two records) and holds no output keeps
 * running. On other systems the JDK's pipes are used, with their exit-time
 * drain.
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
    /** How long the output may stay open after the command exits. */
    public static final Duration OUTPUT_GRACE = Duration.ofSeconds(1);
    /** How often the command's descendants are recorded, and the output's holders looked for after the exit. */
    private static final long SAMPLE_INTERVAL = TimeUnit.MILLISECONDS.toNanos(10);

    /**
     * Test seam: whether descendants are recorded. Tests clear it to show
     * that a holder of the output is killed without being recorded.
     */
    static volatile boolean recordDescendants = true;
    /** Test seam: runs after each record with every pid recorded so far. Null outside tests. */
    static volatile java.util.function.Consumer<java.util.Set<Long>> recordedHook;

    /** The command's descendants seen while it ran, by pid, with their start times. */
    private static final class Recorded {
        private final java.util.Map<Long, java.time.Instant> started = new java.util.HashMap<>();
        private long next = System.nanoTime();

        /** Records the command's current descendants, at most once per {@link #SAMPLE_INTERVAL}. */
        void sample(Process process) {
            long now = System.nanoTime();
            if (!recordDescendants || now - next < 0) {
                return;
            }
            next = now + SAMPLE_INTERVAL;
            try {
                process.descendants().forEach(handle -> handle.info().startInstant()
                        .ifPresent(start -> started.putIfAbsent(handle.pid(), start)));
            } catch (RuntimeException error) {
                // Not supported here, or the tree changed while it was read.
            }
            java.util.function.Consumer<java.util.Set<Long>> hook = recordedHook;
            if (hook != null) {
                hook.accept(java.util.Set.copyOf(started.keySet()));
            }
        }

        /** Kills every recorded descendant that is still alive with the start time it was recorded with. */
        void killAlive() {
            started.forEach((pid, start) -> ProcessHandle.of(pid).ifPresent(handle -> killIfStarted(handle, start)));
        }
    }

    /** Kills {@code handle} only if it still has start time {@code start}, so a reused pid is never killed. */
    private static void killIfStarted(ProcessHandle handle, java.time.Instant start) {
        try {
            if (handle.info().startInstant().map(start::equals).orElse(false)) {
                handle.destroyForcibly();
            }
        } catch (RuntimeException ignored) {
            // Not permitted, or already gone.
        }
    }

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
        Recorded recorded = new Recorded();
        // A command that already exited has no start time; a holder then
        // must at least have started after this JVM.
        java.time.Instant started = process.toHandle().info().startInstant()
                .orElseGet(() -> ProcessHandle.current().info().startInstant().orElse(null));
        try {
            return await(process, pipes, recorded, started, expires, cancel, stdoutLimit, stderrLimit);
        } finally {
            if (process.isAlive()) {
                killTree(process);
                awaitExit(process, EXIT_GRACE);
            }
            recorded.killAlive();
            if (pipes != null) {
                // A descendant still writing has no reader now, so its next write fails.
                pipes.close();
            } else {
                closeQuietly(process.getInputStream());
                closeQuietly(process.getErrorStream());
            }
        }
    }

    private static Output await(Process process, OutputPipes pipes, Recorded recorded, java.time.Instant started, long expires,
            Cancel cancel, int stdoutLimit, int stderrLimit) throws CarrierException {
        Capture stdout = new Capture(pipes != null ? pipes.stdout() : process.getInputStream(), stdoutLimit);
        Capture stderr = new Capture(pipes != null ? pipes.stderr() : process.getErrorStream(), stderrLimit);
        long idle = POLL_MIN;
        while (true) {
            // The exit is observed before the pipes are read, so everything
            // the command itself wrote is read below.
            boolean exited = !process.isAlive();
            if (!exited) {
                recorded.sample(process);
            }
            int read = stdout.step(SLICE) + stderr.step(SLICE);
            if (exited) {
                if (pipes != null) {
                    awaitClosed(pipes, started, stdout, stderr, expires, cancel);
                }
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

    /**
     * Waits up to {@link #OUTPUT_GRACE} after the exit until no other process
     * holds the output, reading what arrives meanwhile. On failure it kills
     * the holders it last found that started no earlier than the command.
     */
    private static void awaitClosed(OutputPipes pipes, java.time.Instant started, Capture stdout, Capture stderr, long expires,
            Cancel cancel) throws CarrierException {
        long end = System.nanoTime() + OUTPUT_GRACE.toNanos();
        long scan = System.nanoTime();
        long idle = POLL_MIN;
        java.util.Map<ProcessHandle, java.time.Instant> holders = java.util.Map.of();
        while (true) {
            long now = System.nanoTime();
            if (now - scan >= 0) {
                holders = new java.util.HashMap<>();
                for (ProcessHandle holder : pipes.holders()) {
                    java.time.Instant start = holder.info().startInstant().orElse(null);
                    if (start != null) {
                        holders.put(holder, start);
                    }
                }
                if (holders.isEmpty()) {
                    return;
                }
                scan = now + SAMPLE_INTERVAL;
            }
            CarrierException stopped = stopped(expires, cancel);
            if (stopped == null && now - end >= 0) {
                stopped = CarrierException.loss("its output did not close");
            }
            if (stopped != null) {
                holders.forEach((holder, start) -> {
                    if (started != null && !start.isBefore(started)) {
                        killIfStarted(holder, start);
                    }
                });
                throw stopped;
            }
            if (stdout.step(SLICE) + stderr.step(SLICE) > 0) {
                idle = POLL_MIN;
            } else {
                long slice = Math.min(idle, end - now);
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
