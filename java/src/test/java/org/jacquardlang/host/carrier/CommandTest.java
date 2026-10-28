package org.jacquardlang.host.carrier;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.junit.jupiter.api.Assertions.fail;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.attribute.PosixFilePermissions;
import java.time.Duration;
import java.util.List;
import java.util.Map;
import java.util.stream.Stream;

import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.condition.DisabledOnOs;
import org.junit.jupiter.api.condition.OS;
import org.junit.jupiter.api.io.TempDir;

/**
 * The bounded command runner the SDK uses for Core's public commands: exit
 * status and output, output limits, and timeout, cancellation, and
 * interruption that kill the command and its child. A descendant that keeps
 * the output open after the command exits holds neither the caller nor the
 * heap. The commands are shell scripts, so these run on Unix only.
 */
@DisabledOnOs(OS.WINDOWS)
class CommandTest {
    private static final Duration MINUTE = Duration.ofMinutes(1);

    @TempDir
    Path scratch;

    @AfterEach
    void cleanUp() throws IOException {
        OutputPipes.mkfifoOverride = null;
        Thread.interrupted();
        // Kill every process a script recorded, even after a failed assertion.
        try (Stream<Path> files = Files.list(scratch)) {
            for (Path file : files.filter(path -> path.getFileName().toString().endsWith(".pid")).toList()) {
                for (String pid : Files.readString(file).split("\\s+")) {
                    if (!pid.isEmpty()) {
                        ProcessHandle.of(Long.parseLong(pid)).ifPresent(ProcessHandle::destroyForcibly);
                    }
                }
            }
        }
    }

    private Command.Output sh(String script, Duration timeout, Cancel cancel) throws CarrierException {
        return Command.run(List.of("/bin/sh", "-c", script), Map.of("SCRATCH", scratch.toString()), timeout, 1 << 20, 4096, cancel);
    }

    private long pid(String name) throws Exception {
        Path file = scratch.resolve(name);
        long deadline = System.nanoTime() + MINUTE.toNanos();
        while (true) {
            // The scripts write the pid and a newline, so a complete line is a complete pid.
            String text = Files.exists(file) ? Files.readString(file) : "";
            if (text.endsWith("\n") && !text.isBlank()) {
                return Long.parseLong(text.strip());
            }
            if (System.nanoTime() > deadline) {
                fail(name + " was never written");
            }
            Thread.sleep(5);
        }
    }

    /**
     * Waits for {@code pid} to exit, failing if it outlives ten seconds; the
     * scripts' children would otherwise live for five minutes.
     */
    private static void dies(String what, long pid) throws InterruptedException {
        long deadline = System.nanoTime() + Duration.ofSeconds(10).toNanos();
        while (ProcessHandle.of(pid).map(ProcessHandle::isAlive).orElse(false)) {
            if (System.nanoTime() > deadline) {
                fail(what + " is still running");
            }
            Thread.sleep(10);
        }
    }

    /** Runs {@code body} on another thread once {@code name} holds a pid. */
    private Thread once(String name, Runnable body) {
        return Thread.ofPlatform().daemon().start(() -> {
            try {
                pid(name);
            } catch (Exception error) {
                return;
            }
            body.run();
        });
    }

    @Test
    void theExitStatusOutputAndAddedEnvironmentAreReported() throws Exception {
        Command.Output output = Command.run(List.of("/bin/sh", "-c", "printf \"$GREETING\"; printf err >&2; exit 3"),
                Map.of("GREETING", "hello"), MINUTE, 100, 100, new Cancel());
        assertEquals(3, output.exitStatus());
        assertEquals("hello", new String(output.stdout(), StandardCharsets.UTF_8));
        assertEquals("err", new String(output.stderr(), StandardCharsets.UTF_8));
        assertFalse(output.stdoutOverflow());
    }

    @Test
    void outputBeyondTheLimitsIsReadAndCountedButNotKept() throws Exception {
        Command.Output output = Command.run(List.of("/bin/sh", "-c", "head -c 3000000 /dev/zero; head -c 10000 /dev/zero >&2"),
                Map.of(), MINUTE, 1 << 20, 100, new Cancel());
        assertEquals(0, output.exitStatus());
        assertArrayEquals(new byte[1 << 20], output.stdout());
        assertTrue(output.stdoutOverflow());
        assertArrayEquals(new byte[100], output.stderr());
    }

    @Test
    void aCommandThatDoesNotExitInTimeIsKilledWithItsChild() throws Exception {
        CarrierException error = assertThrows(CarrierException.class,
                () -> sh("sleep 300 & echo $! > \"$SCRATCH/child.pid\"; wait", Duration.ofMillis(500), new Cancel()));
        assertTrue(error.isTimeout(), error.getMessage());
        dies("the timed-out command's child", pid("child.pid"));
    }

    @Test
    void cancellationKillsTheCommandAndItsChild() throws Exception {
        Cancel cancel = new Cancel();
        Thread canceller = once("child.pid", cancel::cancel);
        CarrierException error = assertThrows(CarrierException.class,
                () -> sh("sleep 300 & echo $! > \"$SCRATCH/child.pid\"; wait", MINUTE, cancel));
        assertTrue(error.isAborted(), error.getMessage());
        dies("the cancelled command's child", pid("child.pid"));
        canceller.join();
    }

    @Test
    void interruptionKillsTheCommandAndKeepsTheInterruptStatus() throws Exception {
        Thread caller = Thread.currentThread();
        Thread interrupter = once("child.pid", caller::interrupt);
        CarrierException error = assertThrows(CarrierException.class,
                () -> sh("sleep 300 & echo $! > \"$SCRATCH/child.pid\"; wait", MINUTE, new Cancel()));
        assertTrue(error.isAborted(), error.getMessage());
        assertTrue(Thread.interrupted(), "the interrupt status was cleared");
        dies("the interrupted command's child", pid("child.pid"));
        interrupter.join();
    }

    private static long usedHeap() {
        Runtime runtime = Runtime.getRuntime();
        System.gc();
        return runtime.totalMemory() - runtime.freeMemory();
    }

    /**
     * The command exits soon, leaving a sleeper holding its output and a
     * reparented {@code yes} flooding stderr. On the JDK's own pipes the
     * reaper drains the flood under the stream lock {@code available()} takes,
     * into an unbounded buffer, so the caller would be held and the heap grow.
     */
    @Test
    void aDescendantHoldingTheOutputHoldsNeitherTheCallerNorTheHeap() throws Exception {
        String script = "echo ready; sleep 300 & echo $! > \"$SCRATCH/holder.pid\"; "
                + "(yes jacquard-flood >&2 & echo $! > \"$SCRATCH/flooder.pid\"); sleep 0.3";
        long heap = usedHeap();
        long started = System.nanoTime();
        Command.Output output = sh(script, MINUTE, new Cancel());
        long elapsed = (System.nanoTime() - started) / 1_000_000;
        assertTrue(elapsed < 4000, "returned after " + elapsed + " ms");
        assertEquals(0, output.exitStatus());
        assertEquals("ready\n", new String(output.stdout(), StandardCharsets.UTF_8));
        long grown = usedHeap() - heap;
        assertTrue(grown < 64 << 20, "the heap grew by " + grown + " bytes");
        // Its reader is gone, so the flooder's next write fails.
        dies("the flooder", pid("flooder.pid"));
    }

    @Test
    void aCommandThatCannotStartIsALoss() {
        CarrierException error = assertThrows(CarrierException.class,
                () -> Command.run(List.of(scratch.resolve("missing").toString()), Map.of(), MINUTE, 10, 10, new Cancel()));
        assertTrue(error.isLoss() && !error.isTimeout() && !error.isAborted(), error.getMessage());
        assertTrue(error.getMessage().startsWith("could not start"), error.getMessage());
    }

    private static List<String> commandDirectories() throws IOException {
        try (Stream<Path> entries = Files.list(Path.of(System.getProperty("java.io.tmpdir")))) {
            return entries.map(path -> path.getFileName().toString()).filter(name -> name.startsWith("jacquard-command-")).sorted().toList();
        }
    }

    /** A {@code mkfifo} that records its pid and never finishes stands in for a slow one. */
    @Test
    void cancellationWhileThePipesAreMadeKillsMkfifoAndLeavesNothing() throws Exception {
        Path shim = scratch.resolve("mkfifo");
        Files.writeString(shim, "#!/bin/sh\necho $$ > '" + scratch.resolve("mkfifo.pid") + "'\nexec sleep 300\n");
        Files.setPosixFilePermissions(shim, PosixFilePermissions.fromString("rwx------"));
        OutputPipes.mkfifoOverride = shim.toString();
        List<String> before = commandDirectories();
        Cancel cancel = new Cancel();
        Thread canceller = once("mkfifo.pid", cancel::cancel);
        Path marker = scratch.resolve("ran");
        CarrierException error = assertThrows(CarrierException.class, () -> sh("touch \"$SCRATCH/ran\"", MINUTE, cancel));
        assertTrue(error.isAborted(), error.getMessage());
        dies("mkfifo", pid("mkfifo.pid"));
        assertFalse(Files.exists(marker), "the command started after cancellation");
        assertEquals(before, commandDirectories(), "setup left a directory behind");
        canceller.join();
    }
}
