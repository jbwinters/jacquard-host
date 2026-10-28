package org.jacquardlang.host.sdk;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.stream.Stream;

import org.jacquardlang.host.Compat;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.condition.EnabledOnOs;
import org.junit.jupiter.api.condition.OS;
import org.junit.jupiter.api.io.TempDir;

/**
 * A Core command whose descendant escapes (with {@code setsid}) and keeps
 * the command's output open fails after the grace; the host finds the
 * escaped descendant through /proc and kills it, and repeated preparations
 * leak no descriptor or thread. Its own test class, so its own JVM, and so
 * no other class changes the counts.
 */
@EnabledOnOs(OS.LINUX)
class CoreDescriptorsTest {
    @TempDir
    Path scratch;

    private static long descriptors() throws IOException {
        try (Stream<Path> entries = Files.list(Path.of("/proc/self/fd"))) {
            return entries.count();
        }
    }

    private static long threads() throws IOException {
        try (Stream<Path> entries = Files.list(Path.of("/proc/self/task"))) {
            return entries.count();
        }
    }

    @Test
    void anEscapedDescendantHoldingCoreOutputIsKilledAndLeaksNothing() throws Exception {
        // --version leaves an escaped sleeper holding its output, so every
        // preparation fails there.
        String version = "echo 'jacquard " + Compat.CORE_VERSION + "'; setsid sleep 300 & echo $! > \"$ROOT/version.pid\"";
        try (FakeCore fake = FakeCore.create(scratch.resolve("escaped"), Compat.CORE_VERSION, version, "*) ;;")) {
            // Warm up lazily opened files and JDK threads before counting.
            fake.prepareFails("warm.jac", new org.jacquardlang.host.carrier.Cancel());
            FakeCore.dies("the escaped descendant", fake.pid("version.pid"));
            long before = descriptors();
            long threads = threads();
            for (int i = 0; i < 3; i++) {
                JacquardException error = fake.prepareFails("rules-" + i + ".jac", new org.jacquardlang.host.carrier.Cancel());
                assertEquals("Core --version failed: its output did not close", error.getMessage());
                FakeCore.dies("the escaped descendant", fake.pid("version.pid"));
            }
            assertEquals(before, descriptors(), "descriptors grew");
            assertTrue(threads() <= threads, "threads grew");
        }
    }
}
