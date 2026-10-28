package org.jacquardlang.host.carrier;

import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.nio.file.DirectoryStream;
import java.nio.file.Files;
import java.nio.file.Path;
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
 * never cleared. Its stdin is the null device.
 *
 * <p>On Linux, when util-linux {@code setsid} is installed (at
 * {@code /usr/bin/setsid} or {@code /bin/setsid}), the command runs as
 * {@code setsid -- <command>}. The JDK's child is never a process group
 * leader, so {@code setsid} starts a new session and execs the command
 * without forking: the command keeps the child's pid and leads a new process
 * group whose id is that pid. Every descendant that does not leave the group
 * shares it. The host kills that group when the command exits, and on
 * timeout, cancellation, or interruption: it lists {@code /proc}, acquires
 * each process's {@link ProcessHandle} first, then reads its group id from
 * {@code /proc/<pid>/stat}, and kills it through that handle only when the
 * group id is the command's. The JDK checks the handle's start time before
 * signalling, so a pid reused after the handle was acquired is not killed,
 * and a group with a live member keeps its id, so a matching id proves
 * membership. A pass always runs to the end, and passes repeat (at most
 * {@link #GROUP_PASSES}) until one finds no live member. A descendant that
 * leaves the group (calls {@code setsid} itself) is neither detected nor
 * killed; the host closes its ends of the output after the exit, so its next
 * write fails.
 *
 * <p>Elsewhere (macOS, or Linux without {@code setsid}) the command's
 * descendants are recorded about every 10 ms, each by pid and start time, and
 * every recorded descendant still alive with that start time is killed when
 * the command returns; on timeout, cancellation, or interruption a snapshot
 * of its descendants is killed too. A descendant that was never recorded
 * (one started and reparented between two records, as in a fast fork and
 * exit) keeps running. On Windows the JDK's pipes are used, with their
 * exit-time drain.
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
    /** How often the command's descendants are recorded where there are no process groups. */
    private static final long SAMPLE_INTERVAL = TimeUnit.MILLISECONDS.toNanos(10);
    /** The most passes over {@code /proc} one group kill makes. */
    static final int GROUP_PASSES = 8;
    private static final Path PROC = Path.of("/proc");

    /** The {@code setsid} that starts each command in its own group, or null where groups are not used. */
    static final String SETSID = findSetsid();

    private static String findSetsid() {
        if (!System.getProperty("os.name", "").equals("Linux") || !Files.isReadable(Path.of("/proc/self/stat"))) {
            return null;
        }
        for (String candidate : List.of("/usr/bin/setsid", "/bin/setsid")) {
            Path path = Path.of(candidate);
            if (Files.isRegularFile(path) && Files.isExecutable(path)) {
                return candidate;
            }
        }
        return null;
    }

    /** Test seam: when set, commands run without a process group, as where {@code setsid} is absent. */
    static volatile boolean withoutGroups;
    /** Test seam: whether descendants are recorded where there are no groups. */
    static volatile boolean recordDescendants = true;
    /** Test seam: runs after each record with every pid recorded so far. Null outside tests. */
    static volatile java.util.function.Consumer<java.util.Set<Long>> recordedHook;
    /** Test seam: receives the pid of each command once it has started. Null outside tests. */
    static volatile java.util.function.LongConsumer startedHook;
    /** Test seam: runs after the cleanup that follows the exit, before the stop is checked again. Null outside tests. */
    static volatile Runnable exitedHook;
    /** Test seam: whether the group is killed when the command exits. */
    static volatile boolean killGroupAtExit = true;
    /** Test seam: whether the group is killed on a stop. */
    static volatile boolean killGroupOnStop = true;

    /** Whether the next command runs in its own process group. */
    static boolean grouped() {
        return SETSID != null && !withoutGroups;
    }

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
     * Kills every live process whose group id is {@code group}, in passes
     * over {@code /proc} until one finds none, at most {@link #GROUP_PASSES}.
     * Each process's handle is acquired before its group id is read, and the
     * kill goes through that handle, whose start time the JDK checks. A pass
     * is never cut short. Returns how many kills were sent.
     */
    static int killGroup(long group) {
        long self = ProcessHandle.current().pid();
        int killed = 0;
        for (int pass = 0; pass < GROUP_PASSES; pass++) {
            int found = 0;
            try (DirectoryStream<Path> entries = Files.newDirectoryStream(PROC)) {
                for (Path entry : entries) {
                    String name = entry.getFileName().toString();
                    if (name.isEmpty() || name.length() > 18 || !name.chars().allMatch(c -> c >= '0' && c <= '9')) {
                        continue;
                    }
                    long pid = Long.parseLong(name);
                    if (pid == self) {
                        continue;
                    }
                    ProcessHandle handle;
                    try {
                        handle = ProcessHandle.of(pid).orElse(null);
                    } catch (RuntimeException error) {
                        continue;
                    }
                    if (handle == null || groupOf(entry) != group) {
                        continue;
                    }
                    found++;
                    try {
                        if (handle.destroyForcibly()) {
                            killed++;
                        }
                    } catch (RuntimeException ignored) {
                        // Not permitted, or already gone.
                    }
                }
            } catch (IOException | RuntimeException error) {
                // What was killed so far; a later pass may see the rest.
            }
            if (found == 0) {
                break;
            }
        }
        return killed;
    }

    /**
     * The group id in {@code /proc/<pid>/stat}, read after the last
     * {@code )} (the command name may hold any byte), or -1 when the process
     * is gone, is a zombie, or the line cannot be read.
     */
    static long groupOf(Path process) {
        try {
            String stat = new String(Files.readAllBytes(process.resolve("stat")), java.nio.charset.StandardCharsets.ISO_8859_1);
            int close = stat.lastIndexOf(')');
            if (close < 0) {
                return -1;
            }
            String[] fields = stat.substring(close + 1).trim().split(" ");
            // state ppid pgrp ...
            if (fields.length < 3 || fields[0].equals("Z") || fields[0].equals("X")) {
                return -1;
            }
            return Long.parseLong(fields[2]);
        } catch (IOException | RuntimeException error) {
            return -1;
        }
    }

    /**
     * The command's executable, resolved on the {@code PATH} when it names no
     * directory, so that one {@code setsid} cannot exec is reported as a
     * start failure, as without {@code setsid}.
     */
    private static String executable(String name) throws CarrierException {
        java.util.List<Path> candidates = new java.util.ArrayList<>();
        if (name.contains("/")) {
            candidates.add(Path.of(name));
        } else {
            // The JDK looks a bare name up on this JVM's PATH, so the same is done here.
            String path = System.getenv("PATH");
            for (String directory : (path == null ? "" : path).split(":", -1)) {
                candidates.add(Path.of(directory.isEmpty() ? "." : directory, name));
            }
        }
        for (Path candidate : candidates) {
            if (Files.isRegularFile(candidate) && Files.isExecutable(candidate)) {
                return candidate.toString();
            }
        }
        throw CarrierException.loss("could not start " + name + " (not an executable file)");
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
        boolean grouped = grouped();
        List<String> argv = command;
        if (grouped) {
            argv = new java.util.ArrayList<>(List.of(SETSID, "--", executable(command.get(0))));
            argv.addAll(command.subList(1, command.size()));
        }
        ProcessBuilder builder = new ProcessBuilder(argv);
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
        java.util.function.LongConsumer hook = startedHook;
        if (hook != null) {
            hook.accept(process.pid());
        }
        Recorded recorded = grouped ? null : new Recorded();
        boolean[] cleaned = {false};
        try {
            return await(process, pipes, grouped, recorded, cleaned, expires, cancel, stdoutLimit, stderrLimit);
        } finally {
            if (!cleaned[0]) {
                if (grouped) {
                    // While the command is alive, or unreaped, its group id is reserved.
                    if (killGroupOnStop) {
                        killGroup(process.pid());
                    }
                    process.toHandle().destroyForcibly();
                    awaitExit(process, EXIT_GRACE);
                    if (killGroupOnStop) {
                        // Members forked while the first pass ran.
                        killGroup(process.pid());
                    }
                } else {
                    if (process.isAlive()) {
                        killTree(process);
                        awaitExit(process, EXIT_GRACE);
                    }
                    recorded.killAlive();
                }
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

    private static Output await(Process process, OutputPipes pipes, boolean grouped, Recorded recorded, boolean[] cleaned,
            long expires, Cancel cancel, int stdoutLimit, int stderrLimit) throws CarrierException {
        Capture stdout = new Capture(pipes != null ? pipes.stdout() : process.getInputStream(), stdoutLimit);
        Capture stderr = new Capture(pipes != null ? pipes.stderr() : process.getErrorStream(), stderrLimit);
        long idle = POLL_MIN;
        while (true) {
            // The exit is observed before the pipes are read, so everything
            // the command itself wrote is read below.
            boolean exited = !process.isAlive();
            if (!exited && recorded != null) {
                recorded.sample(process);
            }
            int read = stdout.step(SLICE) + stderr.step(SLICE);
            if (exited) {
                // The cleanup pass always runs to the end, as Rust kills the
                // group when the command exits.
                if (grouped) {
                    if (killGroupAtExit) {
                        killGroup(process.pid());
                    }
                } else {
                    recorded.killAlive();
                }
                cleaned[0] = true;
                stdout.step(EXIT_DRAIN);
                stderr.step(EXIT_DRAIN);
                Runnable hook = exitedHook;
                if (hook != null) {
                    hook.run();
                }
                // The cleanup may have outlived the deadline or a stop.
                CarrierException stopped = stopped(expires, cancel);
                if (stopped != null) {
                    throw stopped;
                }
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
