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
import java.util.stream.Stream;

import org.jacquardlang.host.carrier.Cancel;
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
