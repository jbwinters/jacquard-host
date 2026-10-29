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
import org.junit.jupiter.api.condition.EnabledOnOs;
import org.junit.jupiter.api.condition.OS;
import org.junit.jupiter.api.io.TempDir;

/**
 * The bounded command runner the SDK uses for Core's public commands: exit
 * status and output, output limits, and timeout, cancellation, and
 * interruption that kill the command and its child. On Linux with
 * {@code setsid} the command leads its own process group, which is killed
 * when it exits and on a stop, so a descendant still holding the output
 * holds neither the caller nor the heap, and a process outside the group is
 * left alone even when it holds the output. Without groups a recorded
 * descendant that closed its output is killed when the command returns. The
 * commands are shell scripts, so these run on Unix only.
 */
@DisabledOnOs(OS.WINDOWS)
class CommandTest {
    private static final Duration MINUTE = Duration.ofMinutes(1);

    @TempDir
    Path scratch;

    @AfterEach
    void cleanUp() throws IOException {
        OutputPipes.mkfifoOverride = null;
        Command.recordDescendants = true;
        Command.recordedHook = null;
        Command.startedHook = null;
        Command.exitedHook = null;
        Command.withoutGroups = false;
        Command.killGroupAtExit = true;
        Command.killGroupOnStop = true;
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

    private static void assumeGroups() {
        org.junit.jupiter.api.Assumptions.assumeTrue(Command.SETSID != null, "no setsid here");
    }

    /** {@code setsid} execs the command without forking: same pid, and it leads its own group and session. */
    @EnabledOnOs(OS.LINUX)
    @Test
    void theCommandKeepsItsPidAndLeadsItsOwnGroup() throws Exception {
        assumeGroups();
        long[] started = {-1};
        Command.startedHook = pid -> started[0] = pid;
        Command.Output output = sh("echo $$; cut -d' ' -f5,6 /proc/$$/stat", MINUTE, new Cancel());
        assertEquals(0, output.exitStatus());
        String[] lines = new String(output.stdout(), StandardCharsets.UTF_8).strip().split("\n");
        long shell = Long.parseLong(lines[0]);
        assertEquals(started[0], shell, "setsid forked");
        assertEquals(shell + " " + shell, lines[1], "the group and session ids are not the command's pid");
    }

    /**
     * Once the leader is reaped, a live process whose pid is the old group id
     * has reused it: it leads a new group, and the group kill leaves it alone.
     */
    @EnabledOnOs(OS.LINUX)
    @Test
    void aGroupIdReusedAfterTheLeaderIsReapedIsNotKilled() throws Exception {
        assumeGroups();
        Process leader = new ProcessBuilder("/bin/true").start();
        assertTrue(leader.waitFor(10, java.util.concurrent.TimeUnit.SECONDS));
        // A process leading its own group stands in for one that reused the reaped leader's pid.
        Process other = new ProcessBuilder(Command.SETSID, "--", "/bin/sleep", "300").start();
        try {
            Path stat = Path.of("/proc", Long.toString(other.pid()));
            long deadline = System.nanoTime() + Duration.ofSeconds(10).toNanos();
            while (Command.groupOf(stat) != other.pid()) {
                if (System.nanoTime() > deadline) {
                    fail("setsid did not make the process a group leader");
                }
                Thread.sleep(10);
            }
            assertEquals(0, Command.killGroup(other.pid(), leader));
            assertTrue(other.isAlive(), "the process that reused the group id was killed");
            // While the leader is alive, its group is still killed.
            assertTrue(Command.killGroup(other.pid(), other) >= 1, "the live leader's group was not killed");
            dies("the group leader", other.pid());
        } finally {
            other.destroyForcibly();
        }
    }

    /**
     * The command exits soon, leaving a sleeper holding its output and a
     * reparented {@code yes} flooding stderr. Both are in the command's
     * group, so both are killed when it exits, and the command succeeds
     * without holding the caller or growing the heap.
     */
    @EnabledOnOs(OS.LINUX)
    @Test
    void aDescendantHoldingTheOutputHoldsNeitherTheCallerNorTheHeap() throws Exception {
        assumeGroups();
        String script = "echo ready; sleep 300 & echo $! > \"$SCRATCH/holder.pid\"; "
                + "(yes jacquard-flood >&2 & echo $! > \"$SCRATCH/flooder.pid\"); sleep 0.3";
        long heap = usedHeap();
        long started = System.nanoTime();
        Command.Output output = sh(script, MINUTE, new Cancel());
        long elapsed = (System.nanoTime() - started) / 1_000_000;
        assertEquals(0, output.exitStatus());
        assertTrue(new String(output.stdout(), StandardCharsets.UTF_8).startsWith("ready\n"));
        assertTrue(elapsed < 4000, "returned after " + elapsed + " ms");
        long grown = usedHeap() - heap;
        assertTrue(grown < 64 << 20, "the heap grew by " + grown + " bytes");
        dies("the holder", pid("holder.pid"));
        dies("the flooder", pid("flooder.pid"));
    }

    /**
     * A descendant that left the group ({@code setsid yes}) keeps flooding
     * stderr after the command exits; it is not killed, but the host closes
     * its FIFO ends after the exit, so the command returns within 4 s, the
     * heap grows by less than 64 MiB, and the flooder dies on its next write.
     * On the JDK's own pipes the reaper would drain the flood at the exit,
     * under the lock {@code available()} takes and into an unbounded buffer.
     * A watchdog kills the flooder after 20 s, so that variant cannot hang.
     */
    @EnabledOnOs(OS.LINUX)
    @Test
    void anEscapedFlooderHoldsNeitherTheCallerNorTheHeap() throws Exception {
        assumeGroups();
        Thread watchdog = once("flooder.pid", () -> {
            try {
                ProcessHandle flooder = ProcessHandle.of(pid("flooder.pid")).orElse(null);
                if (flooder != null && !flooder.onExit().isDone()) {
                    try {
                        flooder.onExit().get(20, java.util.concurrent.TimeUnit.SECONDS);
                    } catch (java.util.concurrent.TimeoutException late) {
                        flooder.destroyForcibly();
                    }
                }
            } catch (Exception error) {
                // The test reports what it saw.
            }
        });
        String script = "echo ready; (setsid yes jacquard-flood >&2 & echo $! > \"$SCRATCH/flooder.pid\"); sleep 0.3";
        long heap = usedHeap();
        long started = System.nanoTime();
        Command.Output output = sh(script, MINUTE, new Cancel());
        long elapsed = (System.nanoTime() - started) / 1_000_000;
        assertEquals(0, output.exitStatus());
        assertTrue(elapsed < 4000, "returned after " + elapsed + " ms");
        long grown = usedHeap() - heap;
        assertTrue(grown < 64 << 20, "the heap grew by " + grown + " bytes");
        dies("the escaped flooder", pid("flooder.pid"));
        watchdog.join();
    }

    /**
     * {@code sh -c 'sleep 300 & echo $!'} exits 0 at once while its child
     * holds stdout. As in Rust, the group is killed at the exit and the
     * command succeeds with its output.
     */
    @EnabledOnOs(OS.LINUX)
    @Test
    void aBackgroundChildIsKilledWhenTheCommandExits() throws Exception {
        assumeGroups();
        Command.Output output = sh("sleep 300 & echo $!; echo $! > \"$SCRATCH/child.pid\"", MINUTE, new Cancel());
        assertEquals(0, output.exitStatus());
        long child = pid("child.pid");
        assertEquals(child + "\n", new String(output.stdout(), StandardCharsets.UTF_8));
        dies("the background child", child);
    }

    /**
     * Under a JVM that is a child subreaper (as a container's PID 1 can be),
     * the background child is reparented to the JVM itself when the shell
     * exits; it is still in the command's group, so it is killed. A child JVM
     * runs {@link SubreaperMain} under {@code prctl(PR_SET_CHILD_SUBREAPER)},
     * set by a Python launcher that then execs it.
     */
    @EnabledOnOs(OS.LINUX)
    @Test
    void underASubreaperJvmTheBackgroundChildIsKilled() throws Exception {
        assumeGroups();
        Path python = Stream.of("/usr/bin/python3", "/bin/python3").map(Path::of).filter(Files::isExecutable).findFirst().orElse(null);
        org.junit.jupiter.api.Assumptions.assumeTrue(python != null, "no python3 here");
        Path launcher = scratch.resolve("subreaper.py");
        Files.writeString(launcher, "import ctypes, os, sys\n"
                + "libc = ctypes.CDLL(None, use_errno=True)\n"
                + "if libc.prctl(36, 1, 0, 0, 0) != 0:\n    sys.exit('prctl failed')\n"
                + "os.execv(sys.argv[1], sys.argv[1:])\n");
        String javaBin = Path.of(System.getProperty("java.home"), "bin", "java").toString();
        Process child = new ProcessBuilder(python.toString(), "-I", launcher.toString(), javaBin, "-Xmx64m", "-XX:TieredStopAtLevel=1",
                "-cp", System.getProperty("java.class.path"), SubreaperMain.class.getName())
                .redirectInput(ProcessBuilder.Redirect.from(new java.io.File("/dev/null")))
                .redirectErrorStream(true).start();
        String output;
        try {
            output = new String(child.getInputStream().readAllBytes(), StandardCharsets.UTF_8);
            assertTrue(child.waitFor(60, java.util.concurrent.TimeUnit.SECONDS), "the subreaper JVM did not exit");
        } finally {
            child.destroyForcibly();
        }
        List<String> lines = output.lines().toList();
        assertEquals(0, child.exitValue(), output);
        assertEquals("subreaper true", lines.get(0), output);
        assertEquals("status 0", lines.get(1), output);
        assertTrue(lines.get(2).startsWith("child "), output);
        assertEquals("alive false", lines.get(3), output);
    }

    /**
     * A process outside the command's group that holds the command's stdout
     * (opened through {@code /proc/<pid>/fd/1}, started after the command,
     * and reparented) is not killed.
     */
    @EnabledOnOs(OS.LINUX)
    @Test
    void anUnrelatedProcessHoldingTheOutputIsNotKilled() throws Exception {
        assumeGroups();
        Thread opener = once("command.id", () -> {
            try {
                long command = pid("command.id");
                ProcessBuilder launcher = new ProcessBuilder("/bin/sh", "-c",
                        "(exec 3<>/proc/" + command + "/fd/1; exec sleep 300) & echo $! > \"$SCRATCH/holder.pid\"")
                        .redirectInput(ProcessBuilder.Redirect.from(new java.io.File("/dev/null")))
                        .redirectOutput(ProcessBuilder.Redirect.DISCARD).redirectError(ProcessBuilder.Redirect.DISCARD);
                launcher.environment().put("SCRATCH", scratch.toString());
                launcher.start().waitFor();
                long holder = pid("holder.pid");
                // Release the command once the holder has the descriptor.
                long deadline = System.nanoTime() + MINUTE.toNanos();
                while (!holds(holder, command) && System.nanoTime() < deadline) {
                    Thread.sleep(5);
                }
                Files.writeString(scratch.resolve("held"), "");
            } catch (Exception error) {
                throw new IllegalStateException(error);
            }
        });
        Command.Output output = Command.run(List.of("/bin/sh", "-c",
                "echo $$ > \"$SCRATCH/command.id\"; while [ ! -e \"$SCRATCH/held\" ]; do sleep 0.01; done; echo done"),
                Map.of("SCRATCH", scratch.toString()), MINUTE, 1 << 20, 4096, new Cancel());
        opener.join();
        assertEquals(0, output.exitStatus());
        long holder = pid("holder.pid");
        Thread.sleep(200);
        assertTrue(ProcessHandle.of(holder).map(ProcessHandle::isAlive).orElse(false), "the unrelated holder was killed");
    }

    /** Whether {@code holder} has a descriptor on the same pipe as {@code command}'s stdout. */
    private static boolean holds(long holder, long command) {
        try {
            String pipe = Files.readSymbolicLink(Path.of("/proc/" + command + "/fd/1")).toString();
            try (Stream<Path> fds = Files.list(Path.of("/proc/" + holder + "/fd"))) {
                return fds.anyMatch(fd -> {
                    try {
                        return Files.readSymbolicLink(fd).toString().equals(pipe);
                    } catch (IOException error) {
                        return false;
                    }
                });
            }
        } catch (IOException error) {
            return false;
        }
    }

    /**
     * A cancellation while the command runs kills its whole group, including
     * a descendant whose parent already exited (so it is no longer the
     * command's descendant).
     */
    @EnabledOnOs(OS.LINUX)
    @Test
    void aStopKillsTheWholeGroup() throws Exception {
        assumeGroups();
        Cancel cancel = new Cancel();
        Thread canceller = once("orphan.pid", cancel::cancel);
        CarrierException error = assertThrows(CarrierException.class,
                () -> sh("(sleep 300 & echo $! > \"$SCRATCH/orphan.pid\"); sleep 300", MINUTE, cancel));
        assertTrue(error.isAborted(), error.getMessage());
        dies("the orphaned group member", pid("orphan.pid"));
        canceller.join();
    }

    /**
     * Without groups, a descendant that closed its output and keeps running
     * does not fail the command; once recorded, it is killed when the
     * command returns. The command waits until the record holds the
     * descendant, so the record cannot miss it.
     */
    @Test
    void aRecordedDescendantThatClosedItsOutputIsKilled() throws Exception {
        Command.withoutGroups = true;
        Path recorded = scratch.resolve("recorded");
        Command.recordedHook = pids -> {
            try {
                Path file = scratch.resolve("quiet.pid");
                String text = Files.exists(file) ? Files.readString(file) : "";
                if (text.endsWith("\n") && pids.contains(Long.parseLong(text.strip()))) {
                    Files.writeString(recorded, "");
                }
            } catch (IOException | RuntimeException error) {
                throw new IllegalStateException(error);
            }
        };
        Command.Output output = sh("sleep 300 </dev/null >/dev/null 2>&1 & echo $! > \"$SCRATCH/quiet.pid\"; "
                + "while [ ! -e \"$SCRATCH/recorded\" ]; do sleep 0.01; done; echo done", MINUTE, new Cancel());
        assertEquals(0, output.exitStatus());
        assertEquals("done\n", new String(output.stdout(), StandardCharsets.UTF_8));
        dies("the recorded descendant", pid("quiet.pid"));
    }

    /** Without groups, timeout, cancellation, and interruption still kill the command and its child. */
    @Test
    void withoutGroupsAStopKillsTheCommandAndItsChild() throws Exception {
        Command.withoutGroups = true;
        CarrierException error = assertThrows(CarrierException.class,
                () -> sh("sleep 300 & echo $! > \"$SCRATCH/child.pid\"; wait", Duration.ofMillis(500), new Cancel()));
        assertTrue(error.isTimeout(), error.getMessage());
        dies("the timed-out command's child", pid("child.pid"));
    }

    /**
     * A stop that arrives while the exit's cleanup runs (a test seam sets it
     * there) is reported instead of success, with and without groups, and an
     * interrupt is kept.
     */
    @Test
    void aStopDuringTheExitCleanupIsReported() throws Exception {
        for (boolean grouped : new boolean[] {true, false}) {
            Command.withoutGroups = !grouped;
            Cancel cancel = new Cancel();
            Command.exitedHook = cancel::cancel;
            CarrierException error = assertThrows(CarrierException.class, () -> sh("echo done", MINUTE, cancel), "grouped " + grouped);
            assertTrue(error.isAborted(), error.getMessage());
            Thread caller = Thread.currentThread();
            Command.exitedHook = caller::interrupt;
            error = assertThrows(CarrierException.class, () -> sh("echo done", MINUTE, new Cancel()), "grouped " + grouped);
            assertTrue(error.isAborted(), error.getMessage());
            assertTrue(Thread.interrupted(), "the interrupt status was cleared");
        }
    }

    @Test
    void aCommandThatCannotStartIsALoss() {
        CarrierException error = assertThrows(CarrierException.class,
                () -> Command.run(List.of(scratch.resolve("missing").toString()), Map.of(), MINUTE, 10, 10, new Cancel()));
        assertTrue(error.isLoss() && !error.isTimeout() && !error.isAborted(), error.getMessage());
        assertTrue(error.getMessage().startsWith("could not start"), error.getMessage());
    }

    /**
     * A command that {@code setsid} cannot exec (its interpreter is missing)
     * is a start failure, as it is for the JDK without {@code setsid}, not an
     * exit status 127 from a command that never ran.
     */
    @EnabledOnOs(OS.LINUX)
    @Test
    void aCommandSetsidCannotExecIsALoss() throws Exception {
        org.junit.jupiter.api.Assumptions.assumeTrue(Command.grouped(), "no setsid");
        Path script = scratch.resolve("no-interpreter");
        Files.writeString(script, "#!" + scratch.resolve("missing-interpreter") + "\n");
        Files.setPosixFilePermissions(script, PosixFilePermissions.fromString("rwx------"));
        CarrierException error = assertThrows(CarrierException.class,
                () -> Command.run(List.of(script.toString()), Map.of(), MINUTE, 10, 4096, new Cancel()));
        assertTrue(error.isLoss() && !error.isTimeout() && !error.isAborted(), error.getMessage());
        assertTrue(error.getMessage().startsWith("could not start " + script + " (setsid: "), error.getMessage());
        Command.withoutGroups = true;
        error = assertThrows(CarrierException.class, () -> Command.run(List.of(script.toString()), Map.of(), MINUTE, 10, 4096, new Cancel()));
        assertTrue(error.isLoss() && error.getMessage().startsWith("could not start " + script), error.getMessage());
        // The command's own exit status 127 is still its result.
        Command.withoutGroups = false;
        assertEquals(127, sh("echo 'setsid-like' >&2; exit 127", MINUTE, new Cancel()).exitStatus());
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
