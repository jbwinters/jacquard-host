package org.jacquardlang.host.sdk;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.stream.Stream;

import org.jacquardlang.host.Compat;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.condition.EnabledOnOs;
import org.junit.jupiter.api.condition.OS;
import org.junit.jupiter.api.io.TempDir;

/**
 * A Core {@code check} whose descendant keeps the command's output open
 * does not hold the preparation: the command's group is killed when it
 * exits, and repeated preparations leak no descriptor or thread. Its own test
 * class, so its own JVM, and so no other class changes the counts.
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

    /** The names of this JVM's threads, leaving out the GC and compiler threads the JVM adds and removes as it likes. */
    private static List<String> names() throws IOException {
        try (Stream<Path> entries = Files.list(Path.of("/proc/self/task"))) {
            return entries.map(task -> {
                try {
                    return Files.readString(task.resolve("comm")).strip();
                } catch (IOException error) {
                    return "?";
                }
            }).filter(name -> !name.startsWith("GC Thread") && !name.startsWith("G1 ") && !name.contains("CompilerThre"))
                    .sorted().toList();
        }
    }

    private static long threads() throws IOException {
        return names().size();
    }

    @Test
    void aDescendantHoldingCoreOutputIsKilledAndLeaksNothing() throws Exception {
        org.junit.jupiter.api.Assumptions.assumeTrue(Files.isExecutable(Path.of("/usr/bin/setsid")) || Files.isExecutable(Path.of("/bin/setsid")));
        // check leaves a sleeper holding its output and fails.
        String check = "check) sleep 300 & echo $! > \"$ROOT/check.pid\"; echo boom >&2; exit 3 ;;";
        try (FakeCore fake = FakeCore.create(scratch.resolve("holder"), Compat.CORE_VERSION, null, check)) {
            // Warm up lazily opened files and JDK threads before counting.
            fake.prepareFails("warm.jac", new org.jacquardlang.host.carrier.Cancel());
            FakeCore.dies("the check command's descendant", fake.pid("check.pid"));
            long before = descriptors();
            long threads = threads();
            List<String> named = names();
            for (int i = 0; i < 3; i++) {
                JacquardException error = fake.prepareFails("rules-" + i + ".jac", new org.jacquardlang.host.carrier.Cancel());
                assertEquals("Core check failed: boom", error.getMessage());
                FakeCore.dies("the check command's descendant", fake.pid("check.pid"));
            }
            assertEquals(before, descriptors(), "descriptors grew");
            assertTrue(threads() <= threads, "threads grew from " + named + " to " + names());
        }
    }
}
