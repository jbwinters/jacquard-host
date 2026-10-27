package org.jacquardlang.host;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.fail;

import java.io.File;
import java.net.URISyntaxException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Path;
import java.time.Duration;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

import org.jacquardlang.host.carrier.Cancel;
import org.jacquardlang.host.carrier.Options;
import org.jacquardlang.host.json.Json;
import org.jacquardlang.host.json.JsonCodec;
import org.jacquardlang.host.kit.Kit;
import org.jacquardlang.host.protocol.BoundaryType;
import org.jacquardlang.host.protocol.Hash;
import org.jacquardlang.host.protocol.Interface;
import org.jacquardlang.host.protocol.Limits;

/** Shared helpers for the tests. */
final class Support {
    private Support() {}

    /** Runs code expected to fail with a protocol refusal. */
    @FunctionalInterface
    interface Refusable {
        void run() throws Exception;
    }

    static void code(String expected, String context, Refusable body) {
        try {
            body.run();
        } catch (ProtocolException error) {
            assertEquals(expected, error.code(), context + ": " + error.getMessage());
            return;
        } catch (Exception error) {
            fail(context + ": expected " + expected + ", got " + error);
        }
        fail(context + ": expected " + expected + ", got success");
    }

    static Json json(String text) {
        try {
            return JsonCodec.decode(text.getBytes(StandardCharsets.UTF_8), 64);
        } catch (ProtocolException error) {
            throw new AssertionError(error);
        }
    }

    static Hash hash(char c) {
        return new Hash(String.valueOf(c).repeat(64));
    }

    static Interface unit() {
        return new Interface(List.of(), new BoundaryType.Tuple(List.of()), List.of());
    }

    static Kit kit() {
        try {
            return Kit.load();
        } catch (Exception error) {
            throw new AssertionError(error);
        }
    }

    static String location(Class<?> type) {
        try {
            return Path.of(type.getProtectionDomain().getCodeSource().getLocation().toURI()).toString();
        } catch (URISyntaxException error) {
            throw new AssertionError(error);
        }
    }

    /** The stand-in worker command: this JVM, tuned for a quick start. */
    static List<String> standinCommand() {
        String java = Path.of(System.getProperty("java.home"), "bin", "java").toString();
        String classpath = location(StandIn.class) + File.pathSeparator + location(Host.class);
        return List.of(java, "-Xshare:auto", "-XX:TieredStopAtLevel=1", "-XX:+UseSerialGC", "-Xmx64m", "-XX:-UsePerfData",
                "-cp", classpath, StandIn.class.getName());
    }

    static Options standin(String mode, String transcript, Duration deadline) {
        Map<String, String> env = new HashMap<>(System.getenv());
        env.put(StandIn.MODE_VAR, mode);
        env.put(StandIn.CASE_VAR, transcript);
        return new Options(standinCommand(), env, deadline);
    }

    static Host faulty(String mode, Duration deadline, Limits limits) {
        return new Host("unused", new HostOptions(standin(mode, "", deadline), limits));
    }

    static InvocationResult invokeReady(Host host) {
        return host.invoke(kit().identity("kit-ready"), unit(), List.of(), new OperationRegistry(), new Cancel());
    }

    static String reason(InvocationResult result) {
        Json reason = result.hostEvidence().get("reason");
        return reason == null ? null : reason.asString();
    }

    /** The pid a stand-in printed on stderr. */
    static long descendant(String mode, byte[] stderr) {
        String text = new String(stderr, StandardCharsets.UTF_8).trim();
        try {
            return Long.parseLong(text);
        } catch (NumberFormatException error) {
            throw new AssertionError(mode + ": stand-in stderr " + text);
        }
    }

    static boolean alive(long pid) {
        return ProcessHandle.of(pid).map(ProcessHandle::isAlive).orElse(false);
    }

    static void kill(long pid) {
        ProcessHandle.of(pid).ifPresent(ProcessHandle::destroyForcibly);
    }

    /** Waits up to two seconds for {@code pid} to exit, killing it and failing otherwise. */
    static void dies(String mode, long pid) throws InterruptedException {
        long deadline = System.nanoTime() + Duration.ofSeconds(2).toNanos();
        while (alive(pid)) {
            if (System.nanoTime() > deadline) {
                kill(pid);
                fail(mode + ": descendant survived the worker");
            }
            Thread.sleep(10);
        }
    }

    /** Waits up to two seconds for a condition. */
    static boolean eventually(java.util.function.BooleanSupplier condition) throws InterruptedException {
        long deadline = System.nanoTime() + Duration.ofSeconds(2).toNanos();
        while (!condition.getAsBoolean()) {
            if (System.nanoTime() > deadline) {
                return false;
            }
            Thread.sleep(10);
        }
        return true;
    }
}
