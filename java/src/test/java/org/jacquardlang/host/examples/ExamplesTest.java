package org.jacquardlang.host.examples;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.junit.jupiter.api.Assertions.fail;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.PrintStream;
import java.net.URISyntaxException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.TimeUnit;

import org.jacquardlang.host.Failure;
import org.jacquardlang.host.json.Json;
import org.jacquardlang.host.json.JsonCodec;
import org.jacquardlang.host.sdk.JacquardException;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

/**
 * The example projects run as commands through the Java SDK against the real
 * Core named by {@code JACQUARD_BIN} and {@code JACQUARD_PRELUDE}; the live
 * tests are skipped without them, and fail instead when
 * {@code JACQUARD_REQUIRE_LIVE} is set. The usage test needs no Core.
 */
class ExamplesTest {
    @TempDir
    Path root;

    private static void requireLive() {
        String bin = System.getenv("JACQUARD_BIN");
        String prelude = System.getenv("JACQUARD_PRELUDE");
        if (bin == null || bin.isEmpty() || prelude == null || prelude.isEmpty()) {
            if (System.getenv("JACQUARD_REQUIRE_LIVE") != null) {
                fail("live tests need JACQUARD_BIN and JACQUARD_PRELUDE");
            }
            assumeTrue(false, "live tests need JACQUARD_BIN and JACQUARD_PRELUDE");
        }
    }

    /** One command's output and its failure, if any. */
    record Outcome(String output, Exception failure) {
        boolean ok() {
            return failure == null;
        }
    }

    private static Outcome command(String... arguments) {
        ByteArrayOutputStream bytes = new ByteArrayOutputStream();
        PrintStream out = new PrintStream(bytes, true, StandardCharsets.UTF_8);
        Exception failure = null;
        try {
            JacquardExamples.run(List.of(arguments), out);
        } catch (JacquardException | JacquardExamples.Other error) {
            failure = error;
        }
        return new Outcome(bytes.toString(StandardCharsets.UTF_8), failure);
    }

    private static Map<String, Json> summary(String output) throws Exception {
        List<String> lines = output.strip().lines().toList();
        return JsonCodec.decodeObject(lines.get(lines.size() - 1).getBytes(StandardCharsets.UTF_8), 4);
    }

    @Test
    void allFourProjectsRunAsCommands() throws Exception {
        requireLive();
        for (String project : JacquardExamples.PROJECTS) {
            String workdir = root.resolve(project).toString();
            Outcome outcome = command("--workdir", workdir, project);
            assertTrue(outcome.ok(), project + ": " + outcome.failure() + "\n" + outcome.output());
            switch (project) {
                case "quickstart" -> assertEquals("Order total 35: shipping 5\nOrder total 75: shipping 0\n", outcome.output());
                case "planner" -> assertTrue(outcome.output().contains("\"score\": 16") && outcome.output().contains("planner.plan: ok"),
                        outcome.output());
                case "triage" -> {
                    List<Json> rows = JsonCodec.decode(outcome.output().strip().getBytes(StandardCharsets.UTF_8), 16).asArray();
                    List<String> urgencies = rows.stream().map(row -> row.asObject().get("urgency").asString()).toList();
                    assertEquals(List.of("PAGE", "TODAY", "WATCH"), urgencies, outcome.output());
                }
                default -> {
                    assertTrue(outcome.output().contains("\"$tag\": \"Fulfilled\""), outcome.output());
                    Outcome repeated = command("--workdir", workdir, project);
                    assertTrue(repeated.ok() && repeated.output().contains("DuplicateOrder") && repeated.output().contains("\"deliveries\":1"),
                            repeated.output());
                }
            }
        }
        String recovery = root.resolve("recovery").toString();
        Outcome failed = command("--workdir", recovery, "--fault", "publish-failed", "fulfillment");
        if (!(failed.failure() instanceof JacquardException error)) {
            fail("publish failure: " + failed.failure() + "\n" + failed.output());
            return;
        }
        assertEquals(Failure.OPERATION_FAILED, error.failure());
        assertEquals("E1613", error.result().code());
        assertTrue(failed.output().contains("\"pendingDeliveries\":1"), failed.output());
        // A failed explicit delivery prints no count.
        Outcome undelivered = command("--workdir", recovery, "--fault", "publish-failed", "--deliver-pending", "fulfillment");
        assertTrue(undelivered.failure() instanceof JacquardExamples.Other && !undelivered.output().contains("delivered"), undelivered.output());
        for (String expected : List.of("{\"delivered\":1}", "{\"delivered\":0}")) {
            Outcome delivered = command("--workdir", recovery, "--deliver-pending", "fulfillment");
            assertTrue(delivered.ok() && delivered.output().contains(expected), "deliver pending: " + delivered.failure() + " " + delivered.output());
        }
        Path inspect = root.resolve("triage-inspect");
        Outcome inspected = command("--workdir", root.resolve("triage").toString(), "--inspect", inspect.toString(), "triage");
        assertTrue(inspected.ok(), String.valueOf(inspected.failure()));
        assertTrue(Files.exists(Path.of(inspect + ".0.json")), "inspection not saved");
        assertTrue(command("unknown").failure() instanceof JacquardExamples.Other);
        assertTrue(command("--fault", "nope", "fulfillment").failure() instanceof JacquardExamples.Other);
    }

    @Test
    void partialCompletionFaultsRecoverExplicitlyAcrossRestart() throws Exception {
        requireLive();
        for (Object[] row : new Object[][] {{"after-commit", "E1612", 1L}, {"publish-failed", "E1613", 1L}, {"after-delivery", "E1612", 0L}}) {
            String fault = (String) row[0];
            String workdir = root.resolve(fault).toString();
            Outcome faulted = command("--workdir", workdir, "--fault", fault, "fulfillment");
            if (!(faulted.failure() instanceof JacquardException error)) {
                fail(fault + ": " + faulted.failure());
                return;
            }
            assertEquals(row[1], error.result().code(), fault + ": " + error.getMessage());
            assertTrue(faulted.output().contains("\"bookings\":1"), fault + ": " + faulted.output());
            // A restart sees the committed booking; recovery is explicit.
            Outcome delivered = command("--workdir", workdir, "--deliver-pending", "fulfillment");
            assertTrue(delivered.ok() && delivered.output().contains("{\"delivered\":" + row[2] + "}"), fault + ": " + delivered.output());
            Outcome repeated = command("--workdir", workdir, "fulfillment");
            assertTrue(repeated.ok() && repeated.output().contains("DuplicateOrder") && repeated.output().contains("\"pendingDeliveries\":0"),
                    fault + ": " + repeated.output());
            assertEquals(Json.of(1L), summary(repeated.output()).get("deliveries"), fault);
        }
    }

    private static String location(Class<?> type) {
        try {
            return Path.of(type.getProtectionDomain().getCodeSource().getLocation().toURI()).toString();
        } catch (URISyntaxException error) {
            throw new AssertionError(error);
        }
    }

    /** Runs the command in a fresh JVM; returns its exit status, stdout, and stderr. */
    private String[] process(String... arguments) throws Exception {
        String java = Path.of(System.getProperty("java.home"), "bin", "java").toString();
        String classpath = location(JacquardExamples.class) + File.pathSeparator + location(JacquardException.class);
        List<String> command = new ArrayList<>(List.of(java, "-Xshare:auto", "-XX:TieredStopAtLevel=1", "-XX:+UseSerialGC", "-Xmx128m",
                "-cp", classpath, JacquardExamples.class.getName()));
        command.addAll(List.of(arguments));
        Path out = root.resolve("stdout");
        Path err = root.resolve("stderr");
        Process process = new ProcessBuilder(command).redirectOutput(out.toFile()).redirectError(err.toFile()).start();
        try {
            assertTrue(process.waitFor(120, TimeUnit.SECONDS), "the command did not finish");
        } finally {
            process.destroyForcibly();
        }
        return new String[] {Integer.toString(process.exitValue()), Files.readString(out), Files.readString(err)};
    }

    @Test
    void theCommandExits2ForUsageErrors() throws Exception {
        for (String[] arguments : new String[][] {{"unknown"}, {"--fault", "nope", "fulfillment"}, {"--workdir"}, {}}) {
            String[] result = process(arguments);
            assertEquals("2", result[0], List.of(arguments) + ": " + result[2]);
            assertFalse(result[2].isBlank(), List.of(arguments) + ": no message");
        }
    }

    @Test
    void theCommandExits1ForAJacquardFailure() throws Exception {
        requireLive();
        String[] result = process("--workdir", root.resolve("work").toString(), "--fault", "publish-failed", "fulfillment");
        assertEquals("1", result[0], result[2]);
        assertTrue(result[2].contains("E1613"), result[2]);
        assertTrue(result[1].contains("\"pendingDeliveries\":1"), result[1]);
    }
}
