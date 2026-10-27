package org.jacquardlang.host;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.junit.jupiter.api.Assertions.fail;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.time.Duration;
import java.util.Comparator;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.function.Function;
import java.util.stream.Stream;

import org.jacquardlang.host.carrier.Cancel;
import org.jacquardlang.host.carrier.Options;
import org.jacquardlang.host.json.Json;
import org.jacquardlang.host.kit.Kit;
import org.jacquardlang.host.kit.Replay;
import org.jacquardlang.host.kit.Replayer;
import org.jacquardlang.host.protocol.BoundaryType;
import org.jacquardlang.host.protocol.Hash;
import org.jacquardlang.host.protocol.Interface;
import org.jacquardlang.host.protocol.Value;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;

/**
 * Live tests against the real Core named by {@code JACQUARD_BIN} and
 * {@code JACQUARD_PRELUDE}. Each test is skipped without them, and fails
 * instead when {@code JACQUARD_REQUIRE_LIVE} is set, as CI does.
 */
class LiveTest {
    private Path root;
    private String store;
    private Map<String, String> env;
    private String binary;
    private Kit kit;

    @BeforeEach
    void prepare() throws Exception {
        String bin = System.getenv("JACQUARD_BIN");
        String prelude = System.getenv("JACQUARD_PRELUDE");
        if (bin == null || bin.isEmpty() || prelude == null || prelude.isEmpty()) {
            if (System.getenv("JACQUARD_REQUIRE_LIVE") != null) {
                fail("live tests need JACQUARD_BIN and JACQUARD_PRELUDE");
            }
            assumeTrue(false, "live tests need JACQUARD_BIN and JACQUARD_PRELUDE");
        }
        binary = bin;
        kit = Support.kit();
        root = Files.createTempDirectory("jacquard-host-java-live-");
        Path source = root.resolve("fixtures.jac");
        Files.write(source, Kit.file("kit/fixtures.jac"));
        store = root.resolve("store").toString();
        env = new HashMap<>(System.getenv());
        env.put("JACQUARD_PRELUDE", prelude);
        ProcessBuilder builder = new ProcessBuilder(binary, "run", source.toString(), "--store", store).redirectErrorStream(true);
        builder.environment().clear();
        builder.environment().putAll(env);
        Process process = builder.start();
        byte[] output = process.getInputStream().readAllBytes();
        assertTrue(process.waitFor(60, TimeUnit.SECONDS), "installing fixtures timed out");
        assertEquals(0, process.exitValue(), "installing fixtures: " + new String(output, StandardCharsets.UTF_8));
    }

    @AfterEach
    void remove() throws IOException {
        if (root == null) {
            return;
        }
        try (Stream<Path> paths = Files.walk(root)) {
            for (Path path : paths.sorted(Comparator.reverseOrder()).toList()) {
                Files.deleteIfExists(path);
            }
        }
    }

    private Options options(Duration deadline) {
        return new Options(List.of(binary), env, deadline);
    }

    private Host host(Duration deadline) {
        return new Host(store, new HostOptions(options(deadline), null));
    }

    private Hash id(String name) {
        return kit.identity(name);
    }

    private Interface echo() {
        BoundaryType text = new BoundaryType.Nominal(id("text"), List.of());
        return new Interface(List.of(text), text, List.of(id("kit-world")));
    }

    private OperationRegistry registry(Handler handler) {
        return new OperationRegistry().bind(id("kit-world"), id("send"), handler);
    }

    private InvocationResult invokeEcho(Host host, OperationRegistry registry) {
        return host.invoke(id("kit-echo"), echo(), List.of(new Value.Text("ping")), registry, new Cancel());
    }

    @Test
    void liveCoreMatchesAllRecordings() throws Exception {
        List<Replay> replays = Replayer.replayKit(store, options(null), kit, new Cancel());
        for (Replay replay : replays) {
            assertTrue(replay.differences().isEmpty() && replay.proseDrift().isEmpty(),
                    replay.name() + ": " + replay.differences() + ", prose drift " + replay.proseDrift());
        }
        assertEquals(Kit.TRANSCRIPT_COUNT, replays.size());
    }

    @Test
    void livePureAndEffectfulInvocationsRetainEvidence() throws Exception {
        Host host = host(null);
        Interface unit = new Interface(List.of(), new BoundaryType.Tuple(List.of()), List.of());
        InvocationResult pure = host.invoke(id("kit-ready"), unit, List.of(), new OperationRegistry(), new Cancel());
        assertEquals(0, pure.exitStatus(), pure.toString());
        assertEquals(Value.UNIT, pure.valueOrThrow());
        AtomicInteger calls = new AtomicInteger();
        OperationRegistry registry = registry((context, args) -> {
            calls.incrementAndGet();
            if (args.get(0) instanceof Value.Text text) {
                return new Value.Text(text.value() + "!");
            }
            throw HandlerException.outsideFailed("expected text");
        });
        InvocationResult result = invokeEcho(host, registry);
        assertEquals(1, result.requests().size());
        assertEquals(Json.of("effect_ok"), result.hostFrames().get(2).get("kind"));
        assertNotNull(result.evidence());
        assertEquals(Json.of("ok"), result.evidence().get("core").asObject().get("terminal"));
        assertEquals(new Value.Text("ping!"), result.valueOrThrow());
        assertEquals(1, calls.get());
        InvocationResult probe = host.shutdownProbe(new Cancel());
        assertEquals(ResultKind.SHUTDOWN_ACK, probe.kind(), probe.toString());
    }

    @Test
    void liveReadmeExampleRuns() throws Exception {
        // The README example takes its command from JACQUARD_BIN and inherits
        // this environment, which carries JACQUARD_PRELUDE.
        assertEquals(new Value.Text("ping!"),
                org.jacquardlang.host.readme.Echo.echo(store, id("kit-echo"), id("text"), id("kit-world"), id("send")));
    }

    @Test
    void liveFailurePairsAreNeverRetried() throws Exception {
        List<Map.Entry<Function<String, HandlerException>, String>> cases = List.of(
                Map.entry(HandlerException::unsupported, "E1606"),
                Map.entry(HandlerException::refused, "E1607"),
                Map.entry(HandlerException::outsideFailed, "E1613"),
                Map.entry(HandlerException::completionUnknown, "E1612"));
        for (Map.Entry<Function<String, HandlerException>, String> entry : cases) {
            AtomicInteger calls = new AtomicInteger();
            InvocationResult result = invokeEcho(host(null), registry((context, args) -> {
                calls.incrementAndGet();
                throw entry.getKey().apply("synthetic failure");
            }));
            String expected = entry.getValue();
            assertEquals(expected, result.code(), result.toString());
            assertEquals(1, calls.get(), expected);
            assertTrue(result.diagnostics().get(0).cause().endsWith("synthetic failure"), expected + ": " + result.diagnostics().get(0).cause());
        }
        Map<String, Handler> others = Map.of(
                "checked exception", (context, args) -> {
                    throw new IOException("private detail");
                },
                "unchecked exception", (context, args) -> {
                    throw new IllegalStateException("private detail");
                },
                "error", (context, args) -> {
                    throw new AssertionError("private detail");
                });
        for (Map.Entry<String, Handler> entry : others.entrySet()) {
            InvocationResult result = invokeEcho(host(null), registry(entry.getValue()));
            assertTrue(result.code().equals("E1612") && !result.message().contains("private detail"), entry.getKey() + ": " + result);
            assertEquals(Failure.OPERATION_FAILED, assertThrows(InvocationException.class, result::valueOrThrow).failure(), entry.getKey());
        }
    }

    @Test
    void liveHandlerDeadlineKillsTheWorkerWithoutALateResponse() throws Exception {
        for (boolean cooperative : new boolean[] {true, false}) {
            AtomicInteger calls = new AtomicInteger();
            LinkedBlockingQueue<Boolean> finished = new LinkedBlockingQueue<>();
            OperationRegistry registry = registry((context, args) -> {
                calls.incrementAndGet();
                if (cooperative) {
                    // Waits for cancellation.
                    while (!context.isCancelled()) {
                        Thread.sleep(5);
                    }
                } else {
                    // Ignores cancellation; the host abandons it.
                    Thread.sleep(1000);
                }
                finished.add(context.isCancelled());
                return new Value.Text("late");
            });
            long start = System.nanoTime();
            InvocationResult result = invokeEcho(host(Duration.ofMillis(500)), registry);
            Duration elapsed = Duration.ofNanos(System.nanoTime() - start);
            assertTrue(Json.of("deadline").equals(result.hostEvidence().get("reason"))
                    && result.code().equals("E1611")
                    && calls.get() == 1
                    && result.hostFrames().size() == 2
                    && elapsed.compareTo(Duration.ofSeconds(2)) < 0,
                    "cooperative=" + cooperative + ": " + result.hostEvidence() + " frames=" + result.hostFrames().size() + " after " + elapsed);
            Boolean cancelled = finished.poll(3, TimeUnit.SECONDS);
            assertNotNull(cancelled, "handler did not finish");
            assertTrue(cancelled, "cooperative=" + cooperative + ": handler context was not cancelled");
            assertFalse(result.ok());
        }
    }
}
