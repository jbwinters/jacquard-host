package org.jacquardlang.host.sdk;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.nio.file.Path;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;

import org.jacquardlang.host.Compat;
import org.jacquardlang.host.carrier.Cancel;
import org.jacquardlang.host.protocol.Limits;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.condition.DisabledOnOs;
import org.junit.jupiter.api.condition.OS;
import org.junit.jupiter.api.io.TempDir;

/**
 * Runtime options, and preparation against a fake Core: one version check and
 * prelude discovery per key across threads and runtimes, failures reported
 * without waiting for descendants, and every Core command bounded by its
 * timeout, its output limit, the caller's {@link Cancel}, and its interrupt.
 */
class RuntimeTest {
    @TempDir
    Path scratch;

    @Test
    void runtimeOptionsAreValidatedBeforeCoreRuns() throws Exception {
        RuntimeOptions base = RuntimeOptions.defaults().withPrelude(scratch);
        JacquardException deadline = assertThrows(JacquardException.class, () -> new Runtime(base.withDeadline(Duration.ZERO)));
        assertEquals(JacquardException.Kind.BINDING, deadline.kind());
        JacquardException limits = assertThrows(JacquardException.class, () -> new Runtime(base.withLimits(Limits.HARD.with("max_frame_bytes", 0))));
        assertTrue(limits.getMessage().startsWith("invalid runtime limits"), limits.getMessage());
        Runtime runtime = new Runtime(base.withBinary("/no/such/jacquard"));
        for (List<Path> sources : List.of(List.of(Path.of("rules.txt")), List.<Path>of(), List.of(Path.of("a.jac"), Path.of("b.jqd")))) {
            JacquardException error = assertThrows(JacquardException.class, () -> runtime.prepare(sources, PrepareOptions.defaults()));
            assertEquals(JacquardException.Kind.PREPARATION, error.kind(), sources.toString());
        }
        JacquardException missing = assertThrows(JacquardException.class, () -> runtime.prepare(List.of(Path.of("rules.jac")), PrepareOptions.defaults()));
        assertTrue(missing.getMessage().contains("cannot find Core executable /no/such/jacquard"), missing.getMessage());
        assertEquals(0, runtime.coreCommands());
    }

    @Test
    void emptyRuntimeOptionsMeanUnset() throws Exception {
        Runtime runtime = new Runtime(new RuntimeOptions("", scratch, null, null));
        String bin = System.getenv("JACQUARD_BIN");
        assertEquals(bin == null || bin.isEmpty() ? "jacquard" : bin, runtime.binary());
        assertEquals(scratch.toAbsolutePath(), runtime.prelude());
    }

    @DisabledOnOs(OS.WINDOWS)
    @Test
    void versionIsCheckedOnceAndFailuresAreReportedWithoutWaitingForDescendants() throws Exception {
        try (FakeCore fake = FakeCore.failing(scratch.resolve("once"), Compat.CORE_VERSION)) {
            List<CompletableFuture<JacquardException>> workers = new ArrayList<>();
            long started = System.nanoTime();
            for (int i = 0; i < 4; i++) {
                workers.add(CompletableFuture.supplyAsync(() -> {
                    try {
                        return fake.prepareFails();
                    } catch (Exception error) {
                        throw new IllegalStateException(error);
                    }
                }));
            }
            for (CompletableFuture<JacquardException> worker : workers) {
                JacquardException error = worker.get(60, TimeUnit.SECONDS);
                assertEquals(JacquardException.Kind.PREPARATION, error.kind());
                assertEquals("Core check failed: boom", error.getMessage());
            }
            // The --version sleeper holds Core's output for 30 seconds.
            long elapsed = TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - started);
            assertTrue(elapsed < 10_000, "a descendant holding Core's output delayed preparation: " + elapsed + " ms");
            assertEquals(1, fake.calls("--version"));
            assertEquals(4, fake.calls("check"));
            assertFalse(fake.stagingLeft(), "a staging directory was left behind");
            // Another runtime for the same Core shares the check.
            Runtime other = new Runtime(RuntimeOptions.defaults().withBinary(fake.root.resolve("jacquard").toString())
                    .withPrelude(fake.root.resolve("prelude")));
            assertThrows(JacquardException.class, () -> other.prepare(List.of(fake.source("rules.jac")), fake.options()));
            assertEquals(1, fake.calls("--version"));
            assertEquals(1, other.coreCommands());
        }
    }

    @DisabledOnOs(OS.WINDOWS)
    @Test
    void aVersionMismatchIsRefusedAndCheckedAgain() throws Exception {
        try (FakeCore fake = FakeCore.failing(scratch.resolve("mismatch"), "9.9.9")) {
            for (int i = 0; i < 2; i++) {
                String message = fake.prepareFails().getMessage();
                assertTrue(message.contains("expected pinned Core version") && message.contains("jacquard 9.9.9"), message);
            }
            assertEquals(2, fake.calls("--version"));
            assertEquals(0, fake.calls("check"));
        }
    }

    @DisabledOnOs(OS.WINDOWS)
    @Test
    void preludeDiscoveryRunsOncePerCoreAndPrelude() throws Exception {
        try (FakeCore fake = FakeCore.hashing(scratch.resolve("prelude"))) {
            for (String source : List.of("rules.jac", "other.jac")) {
                JacquardException error = fake.prepareFails(source, new Cancel());
                assertEquals("Core run failed: exit status 4", error.getMessage());
            }
            // Three prelude files once, then one local hash per source.
            assertEquals(5, fake.calls("hash"));
            assertEquals(1, fake.calls("--version"));
            assertEquals(2, fake.calls("run"));
            assertFalse(fake.stagingLeft());
        }
    }

    @DisabledOnOs(OS.WINDOWS)
    @Test
    void coreOutputBeyondItsLimitIsRefused() throws Exception {
        try (FakeCore fake = FakeCore.create(scratch.resolve("overflow"), Compat.CORE_VERSION, null, "check) head -c 9000000 /dev/zero ;;")) {
            assertEquals("Core check output exceeds 8 MiB", fake.prepareFails().getMessage());
        }
    }

    private static final String SLOW_CHECK = "check) sleep 300 & echo $! > \"$ROOT/child.pid\"; wait ;;";

    @DisabledOnOs(OS.WINDOWS)
    @Test
    void aCoreCommandThatOutlivesItsTimeoutIsKilledWithItsChild() throws Exception {
        try (FakeCore fake = FakeCore.create(scratch.resolve("timeout"), Compat.CORE_VERSION, null, SLOW_CHECK)) {
            fake.runtime.commandTimeout = Duration.ofMillis(500);
            assertEquals("Core check failed: no exit within 500 ms", fake.prepareFails().getMessage());
            FakeCore.dies("the timed-out command's child", fake.pid("child.pid"));
            assertFalse(fake.stagingLeft());
        }
    }

    @DisabledOnOs(OS.WINDOWS)
    @Test
    void cancellationStopsTheRunningCoreCommand() throws Exception {
        try (FakeCore fake = FakeCore.create(scratch.resolve("cancel"), Compat.CORE_VERSION, null, SLOW_CHECK)) {
            Cancel cancel = new Cancel();
            Thread canceller = Thread.ofPlatform().daemon().start(() -> {
                try {
                    fake.pid("child.pid");
                } catch (Exception error) {
                    return;
                }
                cancel.cancel();
            });
            JacquardException error = fake.prepareFails("rules.jac", cancel);
            assertEquals("Core check failed: preparation was cancelled or interrupted", error.getMessage());
            FakeCore.dies("the cancelled command's child", fake.pid("child.pid"));
            canceller.join();
            assertFalse(fake.stagingLeft());
        }
    }

    @DisabledOnOs(OS.WINDOWS)
    @Test
    void interruptionStopsTheRunningCoreCommandAndKeepsTheInterrupt() throws Exception {
        try (FakeCore fake = FakeCore.create(scratch.resolve("interrupt"), Compat.CORE_VERSION, null, SLOW_CHECK)) {
            Thread caller = Thread.currentThread();
            Thread interrupter = Thread.ofPlatform().daemon().start(() -> {
                try {
                    fake.pid("child.pid");
                } catch (Exception error) {
                    return;
                }
                caller.interrupt();
            });
            JacquardException error = fake.prepareFails("rules.jac", new Cancel());
            boolean interrupted = Thread.interrupted();
            assertTrue(interrupted, "the interrupt status was cleared");
            assertEquals("Core check failed: preparation was cancelled or interrupted", error.getMessage());
            FakeCore.dies("the interrupted command's child", fake.pid("child.pid"));
            interrupter.join();
        }
    }

    /**
     * One preparation runs a slow {@code --version}; another waits for that
     * check. The waiter observes its own cancellation, or its interrupt (which
     * it keeps), while the first still runs, and never runs the command.
     */
    @DisabledOnOs(OS.WINDOWS)
    @Test
    void aPreparationWaitingForAnotherObservesItsCancelAndInterrupt() throws Exception {
        String slowVersion = "sleep 300 & echo $! > \"$ROOT/version.pid\"; wait; echo 'jacquard " + Compat.CORE_VERSION + "'";
        try (FakeCore fake = FakeCore.create(scratch.resolve("waiter"), Compat.CORE_VERSION, slowVersion, "check) exit 3 ;;")) {
            Cancel first = new Cancel();
            CompletableFuture<JacquardException> owner = CompletableFuture.supplyAsync(() -> {
                try {
                    return fake.prepareFails("rules.jac", first);
                } catch (Exception error) {
                    throw new IllegalStateException(error);
                }
            });
            long sleeper = fake.pid("version.pid");
            // The seam stops each waiter only once it waits, so the wait itself must notice.
            Cancel cancelled = new Cancel();
            try {
                Runtime.flightWaitHook = cancelled::cancel;
                assertEquals("preparation was cancelled or interrupted", fake.prepareFails("rules.jac", cancelled).getMessage());
                Runtime.flightWaitHook = () -> Thread.currentThread().interrupt();
                JacquardException interrupted = fake.prepareFails("rules.jac", new Cancel());
                assertTrue(Thread.interrupted(), "the waiter cleared its interrupt");
                assertEquals("preparation was cancelled or interrupted", interrupted.getMessage());
            } finally {
                Runtime.flightWaitHook = null;
            }
            assertTrue(FakeCore.alive(sleeper), "the first preparation's command stopped");
            assertEquals(1, fake.calls("--version"));
            first.cancel();
            assertEquals("Core --version failed: preparation was cancelled or interrupted", owner.get(60, TimeUnit.SECONDS).getMessage());
            FakeCore.dies("the first preparation's command", sleeper);
        }
    }
}
