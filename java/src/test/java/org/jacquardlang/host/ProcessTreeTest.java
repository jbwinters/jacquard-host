package org.jacquardlang.host;

import static org.jacquardlang.host.Support.alive;
import static org.jacquardlang.host.Support.descendant;
import static org.jacquardlang.host.Support.dies;
import static org.jacquardlang.host.Support.faulty;
import static org.jacquardlang.host.Support.invokeReady;
import static org.jacquardlang.host.Support.kill;
import static org.jacquardlang.host.Support.reason;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.time.Duration;
import java.util.List;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.condition.DisabledOnOs;
import org.junit.jupiter.api.condition.OS;

/**
 * The worker's process tree at expiry and after exit. The stand-in's
 * descendants use {@code sleep} and {@code sh}, so these run on Unix only.
 */
@DisabledOnOs(OS.WINDOWS)
class ProcessTreeTest {
    private static Duration since(long start) {
        return Duration.ofNanos(System.nanoTime() - start);
    }

    @Test
    void deadlineStopsTheWorkersProcessTree() throws Exception {
        // A child, and a grandchild under a waiting shell, hold the pipes
        // while the worker stays silent.
        for (String mode : List.of("child-silent", "grandchild-silent")) {
            long start = System.nanoTime();
            InvocationResult result = invokeReady(faulty(mode, Duration.ofSeconds(2), null));
            assertTrue(since(start).compareTo(Duration.ofSeconds(5)) < 0 && "deadline".equals(reason(result)),
                    mode + ": " + since(start) + " " + result.hostEvidence());
            dies(mode, descendant(mode, result.stderr()));
        }
    }

    @Test
    void descendantsReparentedBeforeTheKillAreNotFound() throws Exception {
        // A descendant that daemonized while the worker ran is outside the
        // snapshot: the host still returns at the deadline, but it survives.
        long start = System.nanoTime();
        InvocationResult result = invokeReady(faulty("daemon-silent", Duration.ofSeconds(2), null));
        long pid = descendant("daemon-silent", result.stderr());
        try {
            assertTrue(since(start).compareTo(Duration.ofSeconds(5)) < 0 && "deadline".equals(reason(result)), result.toString());
            assertTrue(alive(pid), "the daemonized descendant was found after all; update LIMITS.md");
        } finally {
            kill(pid);
        }
        // A launcher that exits at once, leaving a descendant holding stdout:
        // stdout ends when the launcher exits, and the descendant survives.
        start = System.nanoTime();
        result = invokeReady(faulty("descendant", Duration.ofSeconds(10), null));
        pid = descendant("descendant", result.stderr());
        try {
            assertTrue(since(start).compareTo(Duration.ofSeconds(5)) < 0, "returned after " + since(start));
            assertEquals("carrier_loss", reason(result), result.toString());
            assertTrue(result.message().contains("worker exited before core_hello"), result.message());
            assertTrue(alive(pid), "the reparented descendant was found after all; update LIMITS.md");
        } finally {
            kill(pid);
        }
    }

    @Test
    void theExitWaitObeysTheDeadline() {
        InvocationResult ok = invokeReady(faulty("ok", Duration.ofSeconds(10), null));
        assertTrue(ok.ok() && Integer.valueOf(0).equals(ok.exitStatus()) && ok.signal() == null, ok.toString());
        // The worker sends its terminal, closes stdout, and never exits.
        long start = System.nanoTime();
        InvocationResult result = invokeReady(faulty("hang-after-close", Duration.ofSeconds(2), null));
        assertEquals("deadline", reason(result), result.toString());
        assertTrue(since(start).compareTo(Duration.ofSeconds(5)) < 0, "returned after " + since(start));
        assertEquals("SIGKILL", result.signal());
        assertNull(result.exitStatus());
    }

    @Test
    void slowCleanExitSucceeds() {
        long start = System.nanoTime();
        InvocationResult result = invokeReady(faulty("slow-exit", Duration.ofSeconds(10), null));
        assertTrue(result.ok() && since(start).compareTo(Duration.ofMillis(1500)) >= 0, result + " after " + since(start));
    }

    @Test
    void cleanupAfterAnEarlyErrorIsNotHeldByAnEscapedDescendant() {
        // The worker sends a malformed hello and exits; its daemonized
        // descendant keeps stderr open, which the host does not wait for.
        long start = System.nanoTime();
        InvocationResult result = invokeReady(faulty("escaped-descendant-unknown-field", Duration.ofSeconds(10), null));
        Duration elapsed = since(start);
        kill(descendant("escaped-descendant-unknown-field", result.stderr()));
        assertTrue(result.kind() == ResultKind.PROTOCOL_ERROR && result.code().equals("E1601"), result.toString());
        assertTrue(elapsed.compareTo(Duration.ofSeconds(5)) < 0, "cleanup took " + elapsed);
    }

    @Test
    void descendantsLeftAfterTheWorkerExitsAreNotObserved() throws Exception {
        // Unlike the Go and Rust hosts, the JDK reaps the worker and closes its
        // pipes at exit, so a descendant still holding stderr is not seen: the
        // result follows the worker's own output and exit status.
        for (String mode : List.of("stderr-descendant", "escaped-descendant")) {
            long start = System.nanoTime();
            InvocationResult result = invokeReady(faulty(mode, Duration.ofSeconds(10), null));
            long pid = descendant(mode, result.stderr());
            try {
                assertTrue(result.ok() && Integer.valueOf(0).equals(result.exitStatus()), mode + ": " + result);
                assertTrue(since(start).compareTo(Duration.ofSeconds(5)) < 0, mode + ": returned after " + since(start));
                assertTrue(alive(pid), mode + ": the descendant was stopped after all; update LIMITS.md");
            } finally {
                kill(pid);
            }
        }
        InvocationResult failed = invokeReady(faulty("stderr-descendant-nonzero", Duration.ofSeconds(10), null));
        kill(descendant("stderr-descendant-nonzero", failed.stderr()));
        assertTrue(failed.kind() == ResultKind.CARRIER_LOSS && failed.code().equals("E1611"), failed.toString());
        assertEquals(74, failed.exitStatus());
    }
}
