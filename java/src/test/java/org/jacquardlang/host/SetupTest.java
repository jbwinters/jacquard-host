package org.jacquardlang.host;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.attribute.PosixFilePermissions;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.TimeUnit;
import java.util.stream.Stream;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.condition.DisabledOnOs;
import org.junit.jupiter.api.condition.OS;
import org.junit.jupiter.api.io.TempDir;

/**
 * Cancellation, interruption, and expiry while the output FIFOs are being
 * made. A {@code mkfifo} that never finishes stands in for a slow one; the
 * call must still end promptly and report why, not a spawn failure.
 */
@DisabledOnOs(OS.WINDOWS)
class SetupTest {
    @TempDir
    Path scratch;

    /**
     * Runs {@link SetupProbe} with a hanging {@code mkfifo}; returns its
     * reason, milliseconds, and interrupt flag, having checked that the
     * {@code mkfifo} was killed.
     */
    private String[] probe(String mode) throws Exception {
        Path bin = Files.createDirectory(scratch.resolve("bin"));
        Path shim = bin.resolve("mkfifo");
        Files.writeString(shim, "#!/bin/sh\nexec sleep 30\n");
        Files.setPosixFilePermissions(shim, PosixFilePermissions.fromString("rwx------"));
        Path tmp = Files.createDirectory(scratch.resolve("tmp"));
        String java = Path.of(System.getProperty("java.home"), "bin", "java").toString();
        String classpath = Support.location(SetupProbe.class) + File.pathSeparator + Support.location(Host.class);
        List<String> command = new ArrayList<>(List.of(java, "-Xshare:auto", "-XX:TieredStopAtLevel=1", "-XX:+UseSerialGC",
                "-Xmx64m", "-Djava.io.tmpdir=" + tmp, "-cp", classpath, SetupProbe.class.getName(), mode));
        ProcessBuilder builder = new ProcessBuilder(command).redirectErrorStream(true).redirectOutput(scratch.resolve("out").toFile());
        builder.environment().put("PATH", bin + File.pathSeparator + System.getenv("PATH"));
        Process process = builder.start();
        try {
            assertTrue(process.waitFor(60, TimeUnit.SECONDS), "probe did not finish");
        } finally {
            process.toHandle().descendants().forEach(ProcessHandle::destroyForcibly);
            process.destroyForcibly();
        }
        String output = Files.readString(scratch.resolve("out"), StandardCharsets.UTF_8).strip();
        assertEquals(0, process.exitValue(), output);
        try (Stream<Path> left = Files.list(tmp)) {
            assertEquals(List.of(), left.toList(), "setup left files behind");
        }
        String[] fields = output.lines().reduce((first, second) -> second).orElse("").split(" ");
        assertEquals(4, fields.length, output);
        assertEquals("0", fields[3], "mkfifo was left running");
        return fields;
    }

    private void assertPrompt(String[] fields, long limit) {
        long elapsed = Long.parseLong(fields[1]);
        assertTrue(elapsed < limit, "returned after " + elapsed + " ms");
    }

    @Test
    void cancellationDuringSetupEndsTheCallAsAborted() throws Exception {
        String[] fields = probe("cancel");
        assertEquals("aborted", fields[0]);
        assertPrompt(fields, 2000);
    }

    @Test
    void interruptionDuringSetupEndsTheCallAsAbortedAndKeepsTheInterrupt() throws Exception {
        String[] fields = probe("interrupt");
        assertEquals("aborted", fields[0]);
        assertEquals("true", fields[2]);
        assertPrompt(fields, 2000);
    }

    @Test
    void expiryDuringSetupIsReportedAsTheDeadline() throws Exception {
        String[] fields = probe("deadline");
        assertEquals("deadline", fields[0]);
        assertPrompt(fields, 2000);
    }
}
