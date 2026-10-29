package org.jacquardlang.host.carrier;

import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.RandomAccessFile;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.attribute.PosixFilePermissions;
import java.util.List;
import java.util.concurrent.TimeUnit;

/**
 * The host's own ends of a child process's stdout and stderr on Unix.
 *
 * <p>The JDK's process pipes drain themselves on its reaper thread when the
 * process exits, under the stream lock that {@code available()} also takes,
 * for as long as a descendant keeps writing, and into an unbounded buffer. So
 * on Unix the host makes two FIFOs with one {@code mkfifo -m 600} in a private
 * temporary directory, opens each read-write (the open never waits for a
 * writer, and the host always holds a read end), and redirects the child's
 * output to them. Reads go through a {@link FileInputStream} on the same
 * descriptor, whose {@code available()} asks the pipe directly. The JDK does
 * not touch a redirected file, so no host read waits on its lock, and output
 * a descendant keeps writing stays in the pipe until the host closes its ends.
 * The names are removed once the child has started.
 */
final class OutputPipes {
    /** Whether output goes through host-owned FIFOs: everywhere but Windows. */
    static final boolean SUPPORTED = !System.getProperty("os.name", "").startsWith("Windows");
    /** The longest {@code mkfifo} may take, within the caller's lifetime. */
    private static final long MKFIFO_WAIT = TimeUnit.SECONDS.toNanos(5);
    private static final long POLL_MAX = TimeUnit.MILLISECONDS.toNanos(5);

    /**
     * Test seam: the {@code mkfifo} executable to run instead of the one on
     * the {@code PATH}. Null outside tests.
     */
    static volatile String mkfifoOverride;

    private final Path directory;
    private final Path stdoutPath;
    private final Path stderrPath;
    private final RandomAccessFile stdout;
    private final RandomAccessFile stderr;
    private final InputStream stdoutStream;
    private final InputStream stderrStream;

    private OutputPipes(Path directory, Path stdoutPath, Path stderrPath, RandomAccessFile stdout, RandomAccessFile stderr) {
        this.directory = directory;
        this.stdoutPath = stdoutPath;
        this.stderrPath = stderrPath;
        this.stdout = stdout;
        this.stderr = stderr;
        this.stdoutStream = streamOf(stdout);
        this.stderrStream = streamOf(stderr);
    }

    private static InputStream streamOf(RandomAccessFile file) {
        try {
            return new FileInputStream(file.getFD());
        } catch (IOException error) {
            throw new IllegalStateException(error);
        }
    }

    /**
     * Makes and opens both FIFOs in a new private directory named with
     * {@code prefix}. Returns null, with {@code mkfifo} killed and the
     * directory removed, once the call is cancelled or interrupted or the
     * lifetime ends; the caller then reports that instead of a spawn failure.
     * An interrupt stays set for the caller.
     *
     * @throws IOException when {@code mkfifo} cannot run, fails, or takes
     *     longer than five seconds, or a FIFO cannot be opened; nothing is
     *     left behind
     */
    static OutputPipes create(String prefix, long expires, Cancel cancel) throws IOException {
        Path directory = Files.createTempDirectory(prefix, PosixFilePermissions.asFileAttribute(PosixFilePermissions.fromString("rwx------")));
        boolean opened = false;
        try {
            Path out = directory.resolve("stdout");
            Path err = directory.resolve("stderr");
            if (!makeFifos(out, err, expires, cancel)) {
                return null;
            }
            RandomAccessFile stdout = new RandomAccessFile(out.toFile(), "rw");
            OutputPipes pipes;
            try {
                RandomAccessFile stderr = new RandomAccessFile(err.toFile(), "rw");
                try {
                    pipes = new OutputPipes(directory, out, err, stdout, stderr);
                } catch (Throwable error) {
                    stderr.close();
                    throw error;
                }
            } catch (Throwable error) {
                stdout.close();
                throw error;
            }
            opened = true;
            return pipes;
        } finally {
            if (!opened) {
                deleteQuietly(directory);
            }
        }
    }

    /**
     * Creates both FIFOs with one {@code mkfifo -m 600}. Returns false, with
     * {@code mkfifo} killed, once the call is cancelled or interrupted or the
     * lifetime ends. An interrupt stays set for the caller.
     */
    private static boolean makeFifos(Path stdout, Path stderr, long expires, Cancel cancel) throws IOException {
        String executable = mkfifoOverride != null ? mkfifoOverride : "mkfifo";
        Process mkfifo;
        try {
            mkfifo = new ProcessBuilder(executable, "-m", "600", stdout.toString(), stderr.toString())
                    .redirectInput(ProcessBuilder.Redirect.from(new File("/dev/null")))
                    .redirectOutput(ProcessBuilder.Redirect.DISCARD)
                    .redirectError(ProcessBuilder.Redirect.DISCARD)
                    .start();
        } catch (IOException error) {
            throw new IOException(error.getMessage() + "; the Java host needs mkfifo on Unix", error);
        }
        long end = System.nanoTime() + MKFIFO_WAIT;
        while (true) {
            if (Worker.stopped(expires, cancel)) {
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

    /** Redirects the child's stdout and stderr to the FIFOs. */
    void redirect(ProcessBuilder builder) {
        builder.redirectOutput(ProcessBuilder.Redirect.to(stdoutPath.toFile()));
        builder.redirectError(ProcessBuilder.Redirect.to(stderrPath.toFile()));
    }

    /** The host's read end of stdout. */
    InputStream stdout() {
        return stdoutStream;
    }

    /** The host's read end of stderr. */
    InputStream stderr() {
        return stderrStream;
    }

    /**
     * Removes the names. The child holds its ends and the host its own, so
     * the open descriptors stay valid.
     */
    void unlink() {
        deleteQuietly(directory);
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

    private static void deleteQuietly(Path directory) {
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
}
