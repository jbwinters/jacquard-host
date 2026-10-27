package org.jacquardlang.host;

import static org.jacquardlang.host.Support.descendant;
import static org.jacquardlang.host.Support.eventually;
import static org.jacquardlang.host.Support.faulty;
import static org.jacquardlang.host.Support.invokeReady;
import static org.jacquardlang.host.Support.kill;
import static org.jacquardlang.host.Support.kit;
import static org.jacquardlang.host.Support.reason;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.List;
import java.util.Map;
import java.util.stream.Stream;

import org.jacquardlang.host.carrier.Cancel;
import org.jacquardlang.host.carrier.Options;
import org.jacquardlang.host.kit.Kit;
import org.jacquardlang.host.protocol.BoundaryType;
import org.jacquardlang.host.protocol.Interface;
import org.jacquardlang.host.protocol.Value;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.condition.EnabledOnOs;
import org.junit.jupiter.api.condition.OS;

/**
 * Runs in its own test JVM (surefire does not reuse forks), so descriptor
 * and thread counts see no other class's leftovers. It counts open
 * descriptors and the adapter's own threads; JDK-owned threads (the process
 * reaper pool, compiler threads) start and stop on their own and are not
 * counted.
 */
@EnabledOnOs(OS.LINUX)
class DescriptorsTest {
    private static long descriptors() throws IOException {
        try (Stream<Path> entries = Files.list(Path.of("/proc/self/fd"))) {
            return entries.count();
        }
    }

    private static long adapterThreads() {
        return Thread.getAllStackTraces().keySet().stream()
                .filter(thread -> thread.isAlive() && thread.getName().startsWith("jacquard-"))
                .count();
    }

    @Test
    void invocationsReleaseTheirPipesAndThreads() throws Exception {
        assertTrue(invokeReady(faulty("ok", Duration.ofSeconds(10), null)).ok()); // warm up lazily opened files
        long before = descriptors();
        assertEquals(0, adapterThreads());
        for (int i = 0; i < 5; i++) {
            InvocationResult result = invokeReady(faulty("ok", Duration.ofSeconds(10), null));
            assertTrue(result.ok(), result.toString());
        }
        assertTrue(descriptors() <= before, "descriptors grew from " + before + " to " + descriptors());
        assertEquals(0, adapterThreads(), "adapter threads outlived their invocations");

        // A daemonized descendant keeps holding stderr after the invocation
        // returns; the host does not wait for it, and its pipes are closed.
        InvocationResult escaped = invokeReady(faulty("escaped-descendant", Duration.ofSeconds(10), null));
        long pid = descendant("escaped-descendant", escaped.stderr());
        long afterEscaped = descriptors();
        long threadsAfterEscaped = adapterThreads();
        kill(pid);
        assertTrue(escaped.ok(), escaped.toString());
        assertTrue(afterEscaped <= before, "escaped descendant: descriptors grew from " + before + " to " + afterEscaped);
        assertEquals(0, threadsAfterEscaped, "escaped descendant: adapter threads remain");

        // Handlers run on their own threads, which end with the handler.
        Kit kit = kit();
        BoundaryType text = new BoundaryType.Nominal(kit.identity("text"), List.of());
        Interface spec = new Interface(List.of(text), text, List.of(kit.identity("kit-world")));
        OperationRegistry registry = new OperationRegistry().bind(kit.identity("kit-world"), kit.identity("send"),
                (context, args) -> new Value.Text("pong"));
        Host replaying = new Host("unused",
                new HostOptions(Support.standin("replay", "one-once-effect-success", Duration.ofSeconds(20)), null));
        for (int i = 0; i < 3; i++) {
            InvocationResult result = replaying.invoke(kit.identity("kit-echo"), spec, List.of(new Value.Text("ping")), registry,
                    new Cancel());
            assertTrue(result.ok(), result.toString());
        }
        assertTrue(eventually(() -> adapterThreads() == 0), "handler threads remain");
        assertTrue(descriptors() <= before, "handlers: descriptors grew from " + before + " to " + descriptors());

        // Failure paths release the same resources: expiry, cancellation,
        // an interrupted caller, and a truncated frame.
        for (int i = 0; i < 3; i++) {
            assertEquals("deadline", reason(invokeReady(faulty("silent", Duration.ofMillis(300), null))));
            Cancel cancelled = new Cancel();
            cancelled.cancel();
            InvocationResult aborted = faulty("silent", Duration.ofSeconds(10), null).invoke(kit.identity("kit-ready"),
                    Support.unit(), List.of(), new OperationRegistry(), cancelled);
            assertTrue(!aborted.ok(), aborted.toString());
            Thread.currentThread().interrupt();
            InvocationResult interrupted = invokeReady(faulty("silent", Duration.ofSeconds(10), null));
            assertTrue(Thread.interrupted(), "the interrupt status was not restored");
            assertTrue(!interrupted.ok(), interrupted.toString());
            assertTrue(!invokeReady(faulty("truncated", Duration.ofSeconds(10), null)).ok());
        }
        assertTrue(eventually(() -> adapterThreads() == 0), "failure paths: adapter threads remain");
        assertTrue(eventually(() -> {
            try {
                return descriptors() <= before;
            } catch (IOException error) {
                return false;
            }
        }), "failure paths: descriptors grew from " + before);
    }

    @Test
    void aStderrFloodAfterTheWorkerExitsDoesNotHoldTheCaller() throws Exception {
        assertFloodDoesNotHold("(yes jacquard-flood >&2 & echo $! > \"$PIDFILE\"); sleep 0.3; exit 0", Duration.ofSeconds(10), null);
    }

    @Test
    void aStderrFloodDoesNotHoldExpiry() throws Exception {
        assertFloodDoesNotHold("(yes jacquard-flood >&2 & echo $! > \"$PIDFILE\"); exec sleep 30", Duration.ofMillis(500), null);
    }

    @Test
    void aStderrFloodDoesNotHoldCancellation() throws Exception {
        assertFloodDoesNotHold("(yes jacquard-flood >&2 & echo $! > \"$PIDFILE\"); exec sleep 30", Duration.ofSeconds(20),
                Duration.ofMillis(500));
    }

    @Test
    void aStdoutFloodDoesNotHoldTheCaller() throws Exception {
        assertFloodDoesNotHold("(yes jacquard-flood & echo $! > \"$PIDFILE\"); exec sleep 30", Duration.ofSeconds(10), null);
    }

    /**
     * The worker leaves a reparented descendant writing to its stdout or
     * stderr as fast as it can; the kill cannot find it. The JDK's own
     * process pipes drain such a writer on the reaper thread, under the lock
     * {@code available()} takes, into an unbounded buffer. The invocation must
     * return within its bound, keep no flood in the heap, and leave the
     * writer without a reader once it returns.
     */
    private void assertFloodDoesNotHold(String script, Duration deadline, Duration cancelAfter) throws Exception {
        assertTrue(invokeReady(faulty("ok", Duration.ofSeconds(10), null)).ok()); // warm up
        long before = descriptors();
        Path dir = Files.createTempDirectory("jacquard-flood-");
        Path pidFile = dir.resolve("pid");
        Options options = new Options(List.of("sh", "-c", script),
                new java.util.HashMap<>(Map.of("PATH", System.getenv("PATH"), "PIDFILE", pidFile.toString())), deadline);
        Runtime runtime = Runtime.getRuntime();
        System.gc();
        long heapBefore = runtime.totalMemory() - runtime.freeMemory();
        Cancel cancel = new Cancel();
        Thread trigger = null;
        if (cancelAfter != null) {
            trigger = Thread.ofPlatform().daemon().start(() -> {
                try {
                    Thread.sleep(cancelAfter.toMillis());
                } catch (InterruptedException ignored) {
                    return;
                }
                cancel.cancel();
            });
        }
        long start = System.nanoTime();
        InvocationResult result = new Host("unused", new HostOptions(options, null)).shutdownProbe(cancel);
        Duration elapsed = Duration.ofNanos(System.nanoTime() - start);
        long heapAfter = runtime.totalMemory() - runtime.freeMemory();
        if (trigger != null) {
            trigger.join();
        }
        assertTrue(eventually(() -> Files.exists(pidFile)), "the flood never started");
        long pid = Long.parseLong(Files.readString(pidFile).trim());
        try {
            assertTrue(!result.ok(), result.toString());
            Duration bound = (cancelAfter != null ? cancelAfter : deadline.compareTo(Duration.ofSeconds(1)) < 0 ? deadline : Duration.ZERO)
                    .plusSeconds(3);
            assertTrue(elapsed.compareTo(bound) < 0, "returned after " + elapsed);
            assertTrue(heapAfter - heapBefore < 64L << 20, "the heap grew by " + ((heapAfter - heapBefore) >> 20) + " MiB");
            // The host closed its end, so the writer's next write fails.
            assertTrue(eventually(() -> !Support.alive(pid)), "the flood still has a reader");
        } finally {
            kill(pid);
            Files.deleteIfExists(pidFile);
            Files.deleteIfExists(dir);
        }
        assertTrue(eventually(() -> adapterThreads() == 0), "adapter threads remain");
        assertTrue(eventually(() -> {
            try {
                return descriptors() <= before;
            } catch (IOException error) {
                return false;
            }
        }), "descriptors grew from " + before);
    }

    @Test
    void expiryWithALiveWorkerKillsTheChildHoldingABlockedWrite() throws Exception {
        assertExpiryKillsTheBlockingChild(Duration.ofMillis(500), null);
    }

    @Test
    void cancellationWithALiveWorkerKillsTheChildHoldingABlockedWrite() throws Exception {
        assertExpiryKillsTheBlockingChild(Duration.ofSeconds(20), Duration.ofMillis(500));
    }

    /**
     * The worker stays alive and its child holds stdin without reading, so
     * the invoke frame blocks the stdin thread. Expiry, or cancellation after
     * {@code cancelAfter}, must return promptly and kill both, which ends the
     * blocked write.
     */
    private void assertExpiryKillsTheBlockingChild(Duration deadline, Duration cancelAfter) throws Exception {
        assertTrue(invokeReady(faulty("ok", Duration.ofSeconds(10), null)).ok()); // warm up
        long before = descriptors();
        Kit kit = kit();
        BoundaryType text = new BoundaryType.Nominal(kit.identity("text"), List.of());
        Interface spec = new Interface(List.of(text), text, List.of());
        Cancel cancel = new Cancel();
        Thread trigger = null;
        if (cancelAfter != null) {
            trigger = Thread.ofPlatform().daemon().start(() -> {
                try {
                    Thread.sleep(cancelAfter.toMillis());
                } catch (InterruptedException ignored) {
                    return;
                }
                cancel.cancel();
            });
        }
        long start = System.nanoTime();
        InvocationResult result = faulty("stdin-child-alive", deadline, null)
                .invoke(kit.identity("kit-ready"), spec, List.of(new Value.Text("x".repeat(220_000))), new OperationRegistry(), cancel);
        Duration elapsed = Duration.ofNanos(System.nanoTime() - start);
        if (trigger != null) {
            trigger.join();
        }
        long pid = descendant("stdin-child-alive", result.stderr());
        try {
            assertTrue(!result.ok(), result.toString());
            Duration bound = (cancelAfter != null ? cancelAfter : deadline).plusSeconds(3);
            assertTrue(elapsed.compareTo(bound) < 0, "returned after " + elapsed);
            assertTrue(eventually(() -> !Support.alive(pid)), "the child holding stdin survived");
        } finally {
            kill(pid);
        }
        assertTrue(eventually(() -> adapterThreads() == 0), "the stdin thread outlived the kill");
        assertTrue(eventually(() -> {
            try {
                return descriptors() <= before;
            } catch (IOException error) {
                return false;
            }
        }), "descriptors grew from " + before);
    }

    @Test
    void aWriteBlockedByAnEscapedDescendantDoesNotHoldTheCaller() throws Exception {
        assertTrue(invokeReady(faulty("ok", Duration.ofSeconds(10), null)).ok()); // warm up
        long before = descriptors();
        // The worker exits after the selection while its child holds stdin
        // and never reads, so the invoke frame (larger than a pipe buffer)
        // cannot be written. The child was reparented before the deadline,
        // so the kill does not find it.
        Kit kit = kit();
        BoundaryType text = new BoundaryType.Nominal(kit.identity("text"), List.of());
        Interface spec = new Interface(List.of(text), text, List.of());
        long start = System.nanoTime();
        InvocationResult result = faulty("stdin-descendant", Duration.ofSeconds(3), null)
                .invoke(kit.identity("kit-ready"), spec, List.of(new Value.Text("x".repeat(220_000))), new OperationRegistry(), new Cancel());
        Duration elapsed = Duration.ofNanos(System.nanoTime() - start);
        long pid = descendant("stdin-descendant", result.stderr());
        try {
            assertEquals("deadline", reason(result), result.toString());
            assertTrue(elapsed.compareTo(Duration.ofSeconds(6)) < 0, "returned after " + elapsed);
            // The documented limit: the stdin thread stays blocked while the
            // descendant holds the pipe.
            assertEquals(1, adapterThreads(), "the blocked stdin thread");
        } finally {
            kill(pid);
        }
        // Once the descendant exits, the write fails and the thread ends.
        assertTrue(eventually(() -> adapterThreads() == 0), "the stdin thread outlived the descendant");
        assertTrue(eventually(() -> {
            try {
                return descriptors() <= before;
            } catch (IOException error) {
                return false;
            }
        }), "descriptors grew from " + before);
    }
}
