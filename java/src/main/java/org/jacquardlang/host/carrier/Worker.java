package org.jacquardlang.host.carrier;

import java.io.FileInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.RandomAccessFile;
import java.nio.ByteBuffer;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.attribute.PosixFilePermissions;
import java.time.Duration;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Map;
import java.util.concurrent.CancellationException;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.Future;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.locks.LockSupport;
import java.util.function.BooleanSupplier;

import org.jacquardlang.host.ProtocolException;
import org.jacquardlang.host.json.Json;
import org.jacquardlang.host.json.JsonCodec;
import org.jacquardlang.host.protocol.Limits;

/**
 * One bounded {@code stdio-u32-json-v0} worker lifetime.
 *
 * <p>Java cannot start a process in its own process group, and a read from a
 * pipe cannot be interrupted. So stdout and stderr are never read with a
 * blocking call: the caller's thread polls {@code available()} in short
 * slices and reads only bytes already in the pipe, and every wait (for a
 * frame, a write, a handler, or the exit) rechecks the single lifetime
 * deadline, the cancellation token, and the thread's interrupt status. Writes
 * run on one stdin thread ({@link Writer}) because a pipe write can block.
 *
 * <p>On Unix, stdout and stderr are FIFOs the host creates with
 * {@code mkfifo} in a private directory and opens read-write before the
 * spawn, and the worker's output is redirected to them; the directory is
 * removed once the worker starts. The JDK's own process pipes drain
 * themselves on its reaper thread when the process exits, under the stream
 * lock that {@code available()} also takes, for as long as a descendant keeps
 * writing, and into an unbounded buffer. The JDK does not touch a redirected
 * file, so no host read can wait on that lock, and output a descendant keeps
 * writing stays in the pipe until the host disposes the worker and closes its
 * end. Elsewhere the JDK's pipes are used, with that drain.
 *
 * <p>At expiry, cancellation, or disposal while the worker runs, the worker's
 * process tree is killed: a snapshot of {@link Process#descendants()} and
 * the worker itself. A descendant that has already been reparented (because
 * its parent exited or it daemonized) is not in the snapshot and keeps
 * running.
 *
 * <p>The JDK reaps the worker itself as soon as it exits. So stdout ends when
 * the worker has exited and its pipe is empty, and a descendant still holding
 * a pipe after the exit is neither observed nor waited for.
 *
 * <p>A worker is used from one thread at a time.
 */
public final class Worker implements AutoCloseable {
    /** The worker lifetime when {@link Options#deadline()} is null. */
    public static final Duration DEFAULT_DEADLINE = Duration.ofSeconds(30);
    /** How long disposal waits for a killed worker to exit. */
    private static final long EXIT_GRACE = TimeUnit.SECONDS.toNanos(1);
    /** The first and the longest pause between polls. */
    private static final long POLL_MIN = TimeUnit.MICROSECONDS.toNanos(100);
    private static final long POLL_MAX = TimeUnit.MILLISECONDS.toNanos(5);
    /** The most stderr one poll reads, so a fast writer cannot hold the caller. */
    private static final int STDERR_SLICE = 1 << 20;
    /** The exit value the JDK reports for a process ended by SIGKILL. */
    private static final int SIGKILL_EXIT = 128 + 9;
    /** Whether the worker's output goes through host-owned FIFOs. */
    private static final boolean FIFOS = !System.getProperty("os.name", "").startsWith("Windows");
    /** The longest {@code mkfifo} may take, within the lifetime. */
    private static final long MKFIFO_WAIT = TimeUnit.SECONDS.toNanos(5);

    private final Process process;
    private final InputStream stdout;
    private final InputStream stderrStream;
    /** The host's read-write handles on the output FIFOs, or null. */
    private final Fifos fifos;
    private final Writer writer;
    private final Gate gate;
    private final long expires;
    private final Cancel cancel;
    private final AtomicBoolean expired = new AtomicBoolean();
    private final byte[] chunk = new byte[8192];
    private byte[] stderr = new byte[0];
    private int stderrLength;
    private int stderrLimit = Limits.HARD.maxStderrBytes();
    private boolean stderrEnded;
    private CarrierException fault;
    private boolean interrupted;
    private boolean inputClosed;
    private boolean killed;
    private boolean exitRecorded;
    private boolean disposed;
    private final String spawnError;
    private Integer exitStatus;
    private String signal;

    private Worker(Process process, Fifos fifos, Gate gate, long expires, Cancel cancel, String spawnError) {
        this.process = process;
        this.fifos = fifos;
        this.gate = gate;
        this.expires = expires;
        this.cancel = cancel;
        this.spawnError = spawnError;
        if (process == null) {
            this.stdout = null;
            this.stderrStream = null;
            this.writer = null;
        } else {
            this.stdout = fifos != null ? fifos.stdoutStream() : process.getInputStream();
            this.stderrStream = fifos != null ? fifos.stderrStream() : process.getErrorStream();
            this.writer = new Writer(process.getOutputStream(), gate);
        }
    }

    /**
     * The host's handles on the worker's stdout and stderr FIFOs. Each is
     * opened read-write, so the open never waits for a writer and the host
     * always holds a read end; reads go through a {@link FileInputStream} on
     * the same descriptor, whose {@code available()} asks the pipe directly.
     */
    private record Fifos(RandomAccessFile stdout, RandomAccessFile stderr, InputStream stdoutStream, InputStream stderrStream) {
        static Fifos open(Path stdout, Path stderr) throws IOException {
            RandomAccessFile out = new RandomAccessFile(stdout.toFile(), "rw");
            try {
                RandomAccessFile err = new RandomAccessFile(stderr.toFile(), "rw");
                return new Fifos(out, err, new FileInputStream(out.getFD()), new FileInputStream(err.getFD()));
            } catch (Throwable error) {
                out.close();
                throw error;
            }
        }

        /** Closes both descriptors. Nothing else holds a lock on them. */
        void close() {
            for (AutoCloseable handle : List.of(stdoutStream, stderrStream, stdout, stderr)) {
                try {
                    handle.close();
                } catch (Exception ignored) {
                    // Already closed through the shared descriptor.
                }
            }
        }
    }

    /**
     * Creates both FIFOs with one {@code mkfifo -m 600}. Returns false, with
     * {@code mkfifo} killed, once the call is cancelled or interrupted or the
     * lifetime ends; the caller then reports that instead of a spawn failure.
     * An interrupt stays set for the caller.
     *
     * @throws IOException when {@code mkfifo} cannot run, fails, or takes
     *     longer than {@link #MKFIFO_WAIT}
     */
    private static boolean makeFifos(Path stdout, Path stderr, long expires, Cancel cancel) throws IOException {
        Process mkfifo;
        try {
            mkfifo = new ProcessBuilder("mkfifo", "-m", "600", stdout.toString(), stderr.toString())
                    .redirectInput(ProcessBuilder.Redirect.from(new java.io.File("/dev/null")))
                    .redirectOutput(ProcessBuilder.Redirect.DISCARD)
                    .redirectError(ProcessBuilder.Redirect.DISCARD)
                    .start();
        } catch (IOException error) {
            throw new IOException(error.getMessage() + "; the Java host needs mkfifo on Unix", error);
        }
        long end = System.nanoTime() + MKFIFO_WAIT;
        while (true) {
            if (stopped(expires, cancel)) {
                mkfifo.toHandle().destroyForcibly();
                return false;
            }
            long left = end - System.nanoTime();
            if (left <= 0) {
                mkfifo.toHandle().destroyForcibly();
                throw new IOException("mkfifo did not finish in time");
            }
            try {
                if (mkfifo.waitFor(Math.min(left, POLL_MAX), TimeUnit.NANOSECONDS)) {
                    break;
                }
            } catch (InterruptedException error) {
                Thread.currentThread().interrupt();
            }
        }
        if (mkfifo.exitValue() != 0) {
            throw new IOException("mkfifo exited with status " + mkfifo.exitValue());
        }
        return true;
    }

    /** Whether the call is cancelled or interrupted (the interrupt stays set) or the lifetime has ended. */
    private static boolean stopped(long expires, Cancel cancel) {
        return cancel.isCancelled() || Thread.currentThread().isInterrupted() || System.nanoTime() - expires >= 0;
    }

    private static void deleteQuietly(Path directory) {
        if (directory == null) {
            return;
        }
        for (String name : List.of("stdout", "stderr")) {
            try {
                Files.deleteIfExists(directory.resolve(name));
            } catch (IOException | RuntimeException ignored) {
                // Left for the system's temporary-file cleanup.
            }
        }
        try {
            Files.deleteIfExists(directory);
        } catch (IOException | RuntimeException ignored) {
            // Left for the system's temporary-file cleanup.
        }
    }

    private static Worker failed(Gate gate, long expires, Cancel cancel, String spawnError, String message) {
        Worker failed = new Worker(null, null, gate, expires, cancel, spawnError);
        failed.fault = CarrierException.loss(message);
        failed.exitRecorded = true;
        return failed;
    }

    /**
     * A worker that was never spawned because the call was cancelled or
     * interrupted, or its lifetime ended, during setup. Its first operation
     * reports the abort or the deadline, as for a running worker.
     */
    private static Worker stoppedBeforeSpawn(Gate gate, long expires, Cancel cancel) {
        Worker stopped = new Worker(null, null, gate, expires, cancel, null);
        stopped.exitRecorded = true;
        stopped.fault();
        return stopped;
    }

    /**
     * Spawns the worker. A spawn failure is reported by the first operation.
     *
     * @throws IllegalArgumentException for invalid options
     */
    public static Worker start(String store, Options options, Cancel cancel) {
        options.validate();
        List<String> command = new ArrayList<>(options.resolvedCommand());
        command.addAll(List.of("host", "worker", "--store", store));
        ProcessBuilder builder = new ProcessBuilder(command);
        if (options.env() != null) {
            Map<String, String> environment = builder.environment();
            environment.clear();
            environment.putAll(options.env());
        }
        Duration deadline = options.deadline() == null ? DEFAULT_DEADLINE : options.deadline();
        // Clamp to about 73 years so the nanosecond deadline cannot overflow.
        long budget = deadline.compareTo(Duration.ofNanos(Long.MAX_VALUE / 4)) > 0 ? Long.MAX_VALUE / 4 : deadline.toNanos();
        long expires = System.nanoTime() + budget;
        Gate gate = new Gate(expires, cancel);
        Fifos fifos = null;
        Path directory = null;
        Process process = null;
        try {
            if (FIFOS) {
                try {
                    directory = Files.createTempDirectory("jacquard-worker-",
                            PosixFilePermissions.asFileAttribute(PosixFilePermissions.fromString("rwx------")));
                    Path out = directory.resolve("stdout");
                    Path err = directory.resolve("stderr");
                    if (!makeFifos(out, err, expires, cancel)) {
                        return stoppedBeforeSpawn(gate, expires, cancel);
                    }
                    fifos = Fifos.open(out, err);
                    builder.redirectOutput(ProcessBuilder.Redirect.to(out.toFile()));
                    builder.redirectError(ProcessBuilder.Redirect.to(err.toFile()));
                } catch (IOException | RuntimeException error) {
                    return failed(gate, expires, cancel, String.valueOf(error.getMessage()),
                            "could not create the worker's output pipes (" + error.getMessage() + ")");
                }
            }
            if (stopped(expires, cancel)) {
                return stoppedBeforeSpawn(gate, expires, cancel);
            }
            try {
                process = builder.start();
            } catch (IOException | RuntimeException error) {
                return failed(gate, expires, cancel, String.valueOf(error.getMessage()),
                        "could not start " + command.get(0) + " (" + error.getMessage() + "); check the configured Core installation");
            }
        } finally {
            if (process == null && fifos != null) {
                // Any path that did not spawn, including an Error, closes the host's ends.
                fifos.close();
            }
            // The worker holds its ends and the host holds its own, so the
            // names are no longer needed.
            deleteQuietly(directory);
        }
        Worker worker;
        try {
            worker = new Worker(process, fifos, gate, expires, cancel, null);
        } catch (Throwable error) {
            // An Error after the spawn: kill the worker and close the host's ends.
            process.toHandle().destroyForcibly();
            if (fifos != null) {
                fifos.close();
            }
            throw error;
        }
        try {
            worker.writer.start();
        } catch (OutOfMemoryError error) {
            // The thread could not start; disposal kills the worker.
            worker.fault = CarrierException.loss("could not start the worker's stdin thread (" + error.getMessage() + ")");
        }
        return worker;
    }

    /** The spawn error, when the worker never started. */
    public String spawnError() {
        return spawnError;
    }

    /** The exit status once the worker has exited, unless the host's SIGKILL ended it. */
    public Integer exitStatus() {
        return exitStatus;
    }

    /**
     * {@code "SIGKILL"} when the host killed the worker and the JDK reported
     * exit value 137 (128 + 9); otherwise null. The JDK reports any other
     * signal as exit status 128 + its number.
     */
    public String signal() {
        return signal;
    }

    /** Whether the worker's lifetime ended by expiry, cancellation, or interruption. */
    public boolean isExpired() {
        return expired.get();
    }

    /** A flag handlers can observe; true once the worker expires or is cancelled. */
    public BooleanSupplier expiredFlag() {
        return expired::get;
    }

    /** Whether the stdin thread is still running (after disposal: stuck on a pipe a descendant holds). */
    public boolean writerAlive() {
        return writer != null && writer.isAlive();
    }

    /** The carrier failure that ended the worker, if any; null otherwise. */
    public CarrierException fault() {
        if (fault == null) {
            if (Thread.interrupted()) {
                interrupted = true;
            }
            if (cancel.isCancelled() || interrupted) {
                expire();
                fault = CarrierException.aborted("invocation aborted; outside completion is unknown");
            } else if (System.nanoTime() - expires >= 0) {
                expire();
                fault = CarrierException.timeout("worker lifetime deadline exceeded");
            }
        }
        return fault;
    }

    private CarrierException faultOr(CarrierException fallback) {
        CarrierException current = fault();
        return current != null ? current : fallback;
    }

    /** Kills the worker's process tree and closes its input. */
    private void expire() {
        expired.set(true);
        killTree();
        closeInput();
    }

    /**
     * Kills the worker and a snapshot of its descendants. The snapshot is
     * taken only while the JDK still reports the worker alive, so it lists
     * the worker's own descendants; each handle carries its start time, which
     * the JDK checks before signalling. The worker is signalled through its
     * {@link ProcessHandle}: {@link Process#destroyForcibly()} also closes the
     * worker's stdin on the calling thread, which blocks behind a write the
     * stdin thread has in progress, and so would never reach the descendants.
     */
    private void killTree() {
        if (process == null || !process.isAlive()) {
            return;
        }
        List<ProcessHandle> tree;
        try {
            tree = process.descendants().toList();
        } catch (RuntimeException error) {
            tree = List.of();
        }
        killed = true;
        process.toHandle().destroyForcibly();
        for (ProcessHandle handle : tree) {
            try {
                handle.destroyForcibly();
            } catch (RuntimeException ignored) {
                // Not permitted, or already gone.
            }
        }
    }

    /** Parks for up to {@code nanos}, never past the lifetime deadline. */
    private void pause(long nanos) {
        long slice = Math.min(nanos, expires - System.nanoTime());
        if (slice > 0) {
            LockSupport.parkNanos(slice);
        }
        if (Thread.interrupted()) {
            interrupted = true;
        }
    }

    /**
     * Waits for {@code future} until it completes, the deadline passes, or the
     * invocation is cancelled. Returns null if the future failed.
     */
    public <T> T await(Future<T> future) throws CarrierException {
        long idle = POLL_MIN;
        while (true) {
            CarrierException current = fault();
            if (current != null) {
                throw current;
            }
            long slice = Math.max(0, Math.min(idle, expires - System.nanoTime()));
            try {
                return future.get(slice, TimeUnit.NANOSECONDS);
            } catch (TimeoutException error) {
                drainStderr();
                idle = Math.min(idle * 2, POLL_MAX);
            } catch (InterruptedException error) {
                interrupted = true;
            } catch (ExecutionException | CancellationException error) {
                return null;
            }
        }
    }

    /** Selects how much stderr is retained. */
    public void setStderrLimit(int limit) {
        stderrLimit = Math.max(0, limit);
        stderrLength = Math.min(stderrLength, stderrLimit);
    }

    /** The retained stderr prefix. */
    public byte[] stderr() {
        return Arrays.copyOf(stderr, stderrLength);
    }

    /** Reads whatever stderr is in the pipe, keeping a bounded prefix. Never blocks. */
    private void drainStderr() {
        if (stderrStream == null || stderrEnded) {
            return;
        }
        try {
            for (int budget = STDERR_SLICE; budget > 0; ) {
                int available = stderrStream.available();
                if (available <= 0) {
                    return;
                }
                int n = stderrStream.read(chunk, 0, Math.min(available, chunk.length));
                if (n < 0) {
                    stderrEnded = true;
                    return;
                }
                int keep = Math.min(n, stderrLimit - stderrLength);
                if (keep > 0) {
                    if (stderrLength + keep > stderr.length) {
                        stderr = Arrays.copyOf(stderr, Math.min(stderrLimit, Math.max(stderrLength + keep, stderr.length * 2)));
                    }
                    System.arraycopy(chunk, 0, stderr, stderrLength, keep);
                    stderrLength += keep;
                }
                budget -= n;
            }
        } catch (IOException error) {
            stderrEnded = true;
        }
    }

    /**
     * One non-blocking step on stdout: bytes read, 0 when none are ready yet,
     * or -1 at end of stream. The exit is observed before the pipe is checked,
     * so all of the worker's own output has been read when -1 is returned.
     */
    private int readStep(byte[] buffer, int offset, int length) {
        boolean exited = !process.isAlive();
        try {
            int available = stdout.available();
            if (available > 0) {
                int n = stdout.read(buffer, offset, Math.min(available, length));
                return n < 0 ? -1 : n;
            }
        } catch (IOException error) {
            return -1;
        }
        return exited ? -1 : 0;
    }

    /** Fills {@code buffer}; returns its length, or {@code ~filled} at end of stream. */
    private int fill(byte[] buffer) throws CarrierException {
        int filled = 0;
        long idle = POLL_MIN;
        while (filled < buffer.length) {
            CarrierException current = fault();
            if (current != null) {
                throw current;
            }
            int n = readStep(buffer, filled, buffer.length - filled);
            if (n > 0) {
                filled += n;
                idle = POLL_MIN;
                continue;
            }
            if (n < 0) {
                return ~filled;
            }
            drainStderr();
            pause(idle);
            idle = Math.min(idle * 2, POLL_MAX);
        }
        return filled;
    }

    /** Reads one frame under {@code limits}. Returns null at a clean end of stream. */
    public Map<String, Json> readFrame(Limits limits) throws CarrierException {
        CarrierException current = fault();
        if (current != null) {
            throw current;
        }
        if (stdout == null) {
            return null;
        }
        byte[] prefix = new byte[4];
        int got = fill(prefix);
        if (got < 0) {
            if (~got == 0) {
                return null;
            }
            throw CarrierException.loss("worker closed stdout during a frame");
        }
        long length = Integer.toUnsignedLong(ByteBuffer.wrap(prefix).getInt());
        if (length == 0 || length > limits.maxFrameBytes()) {
            throw CarrierException.protocol(ProtocolException.exceeded("frame length exceeds max_frame_bytes"));
        }
        byte[] payload = new byte[(int) length];
        if (fill(payload) < 0) {
            throw CarrierException.loss("worker closed stdout during a frame");
        }
        // A frame that arrives in the final poll slice is not acted on.
        current = fault();
        if (current != null) {
            throw current;
        }
        try {
            return JsonCodec.decodeObject(payload, limits.maxJsonDepth());
        } catch (ProtocolException error) {
            throw CarrierException.protocol(error);
        }
    }

    /** Writes raw carrier bytes, waiting under the lifetime for the stdin thread. */
    public void writeBytes(byte[] bytes) throws CarrierException {
        CarrierException current = fault();
        if (current != null) {
            throw current;
        }
        if (writer == null || inputClosed) {
            throw CarrierException.loss("worker write failed (worker input is closed)");
        }
        var done = writer.submit(bytes);
        if (done == null) {
            throw faultOr(CarrierException.loss("worker write failed (worker input is closed)"));
        }
        // The stdin thread completes every job normally: null for success.
        IOException error = await(done);
        if (error != null) {
            throw faultOr(CarrierException.loss("worker write failed (" + error.getMessage() + ")"));
        }
        current = fault();
        if (current != null) {
            throw current;
        }
    }

    /** Encodes and writes one frame under the given limits. */
    public void writeFrame(Map<String, Json> frame, Limits limits) throws CarrierException {
        byte[] bytes;
        try {
            bytes = JsonCodec.frameBytes(frame, limits.maxFrameBytes(), limits.maxJsonDepth());
        } catch (ProtocolException error) {
            throw CarrierException.protocol(error);
        }
        writeBytes(bytes);
    }

    /**
     * Closes the worker's stdin. No further chunk starts; the stdin thread
     * closes the pipe after a write in progress ends.
     */
    public void closeInput() {
        gate.close();
        inputClosed = true;
        if (writer != null) {
            writer.close();
        }
    }

    /**
     * Closes stdin and waits for the worker to exit, bounded by the deadline.
     * Call only after stdout reached end of stream.
     */
    public void awaitExit() throws CarrierException {
        closeInput();
        if (process != null) {
            long idle = POLL_MIN;
            while (process.isAlive() && fault() == null) {
                drainStderr();
                pause(idle);
                idle = Math.min(idle * 2, POLL_MAX);
            }
            if (fault != null) {
                // fault() killed the worker; wait briefly so the exit is recorded.
                waitForExit(EXIT_GRACE);
            }
            recordExit();
            drainStderr();
        }
        CarrierException current = fault();
        if (current != null) {
            throw current;
        }
    }

    /** Waits up to {@code nanos} for the worker to exit, regardless of the deadline. */
    private void waitForExit(long nanos) {
        long end = System.nanoTime() + nanos;
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
    }

    private void recordExit() {
        if (exitRecorded || process == null || process.isAlive()) {
            return;
        }
        exitRecorded = true;
        int value = process.exitValue();
        if (killed && value == SIGKILL_EXIT) {
            signal = "SIGKILL";
        } else {
            exitStatus = value;
        }
    }

    /**
     * Kills a live worker's process tree, waits briefly for its exit, reads
     * the stderr left in the pipe, closes the output pipes, and stops the
     * stdin thread. Restores the caller's interrupt status if an interrupt
     * aborted the lifetime.
     */
    public void dispose() {
        if (disposed) {
            return;
        }
        disposed = true;
        closeInput();
        if (process != null) {
            if (process.isAlive()) {
                killTree();
                waitForExit(EXIT_GRACE);
            }
            recordExit();
            drainStderr();
            if (fifos != null) {
                // Closing the host's ends leaves a descendant still writing
                // with no reader, so its next write fails.
                fifos.close();
            } else {
                closeQuietly(stdout);
                closeQuietly(stderrStream);
            }
        }
        if (writer != null) {
            // The thread is idle, or its write fails once the killed worker's
            // end of the pipe closes; a descendant that escaped the kill and
            // holds that end keeps it blocked until it exits or reads.
            writer.join(EXIT_GRACE);
        }
        if (interrupted) {
            Thread.currentThread().interrupt();
        }
    }

    @Override
    public void close() {
        dispose();
    }

    private static void closeQuietly(InputStream stream) {
        try {
            stream.close();
        } catch (IOException ignored) {
            // Nothing is waiting on it.
        }
    }
}
