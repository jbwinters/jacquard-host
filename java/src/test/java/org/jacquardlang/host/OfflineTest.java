package org.jacquardlang.host;

import static org.jacquardlang.host.Support.code;
import static org.jacquardlang.host.Support.faulty;
import static org.jacquardlang.host.Support.hash;
import static org.jacquardlang.host.Support.invokeReady;
import static org.jacquardlang.host.Support.json;
import static org.jacquardlang.host.Support.kit;
import static org.jacquardlang.host.Support.reason;
import static org.jacquardlang.host.Support.standin;
import static org.jacquardlang.host.Support.unit;
import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.TreeMap;
import java.util.concurrent.atomic.AtomicReference;

import org.jacquardlang.host.carrier.Cancel;
import org.jacquardlang.host.carrier.Options;
import org.jacquardlang.host.json.Json;
import org.jacquardlang.host.json.JsonCodec;
import org.jacquardlang.host.kit.Kit;
import org.jacquardlang.host.kit.Replay;
import org.jacquardlang.host.kit.Replayer;
import org.jacquardlang.host.kit.Step;
import org.jacquardlang.host.kit.Transcript;
import org.jacquardlang.host.protocol.BoundaryType;
import org.jacquardlang.host.protocol.Budget;
import org.jacquardlang.host.protocol.FailureCategory;
import org.jacquardlang.host.protocol.Hash;
import org.jacquardlang.host.protocol.Interface;
import org.jacquardlang.host.protocol.Limits;
import org.jacquardlang.host.protocol.OperationEntry;
import org.jacquardlang.host.protocol.Protocol;
import org.jacquardlang.host.protocol.Session;
import org.jacquardlang.host.protocol.Value;
import org.junit.jupiter.api.Test;

/**
 * Offline tests: strict codecs, the session state machine, kit replay
 * through the Java carrier, and host fault handling against the stand-in.
 */
class OfflineTest {
    private static final Limits HARD = Limits.HARD;

    private static Budget budget() {
        return new Budget(HARD);
    }

    @Test
    void nullArgumentsAreRefusedBeforeAWorkerStarts() {
        Host host = faulty("ok", Duration.ofSeconds(10), null);
        assertThrows(NullPointerException.class, () -> host.shutdownProbe(null));
        assertThrows(NullPointerException.class,
                () -> host.invoke(kit().identity("kit-ready"), unit(), List.of(), new OperationRegistry(), null));
    }

    @Test
    void strictJsonRefusals() throws Exception {
        for (String text : List.of(
                "{\"a\":1,\"a\":2}",
                "{\"x\":{\"a\":1,\"a\":2}}",
                "{\"x\":1.0}",
                "{\"x\":1e0}",
                "{\"x\":NaN}",
                "{\"x\":Infinity}",
                "{\"x\":\"\\ud800\"}",
                "{\"x\":\"\\udc00\"}",
                "{\"x\":\"\\ud800A\"}",
                "{}{}",
                "[]",
                "{\"x\":4611686018427387904}",
                "{\"x\":01}",
                "{\"x\":true,}",
                "{\"x\":[1,]}",
                "{\"x\":\"\001\"}",
                "{\"x\":\"\\q\"}",
                "{\"x\":-}",
                "{\"x\":\"\\u+abc\"}",
                "{\"x\":\"\\u\uff10\uff10\uff10\uff10\"}")) {
            code("E1601", text, () -> JsonCodec.decodeObject(text.getBytes(StandardCharsets.UTF_8), 64));
        }
        code("E1601", "invalid UTF-8", () -> JsonCodec.decodeObject(new byte[] {0x7b, 0x22, (byte) 0xff, 0x22, 0x3a, 0x30, 0x7d}, 64));
        // An encoded surrogate (CESU-8) is not UTF-8.
        code("E1601", "encoded surrogate",
                () -> JsonCodec.decodeObject(new byte[] {0x7b, 0x22, (byte) 0xed, (byte) 0xa0, (byte) 0x80, 0x22, 0x3a, 0x30, 0x7d}, 64));
        Map<String, Json> parsed = JsonCodec.decodeObject(
                "{\"a\":4611686018427387903,\"b\":-4611686018427387904,\"c\":\"😀é\"}".getBytes(StandardCharsets.UTF_8), 64);
        assertEquals(Json.of(Json.INT_MAX), parsed.get("a"));
        assertEquals(Json.of(Json.INT_MIN), parsed.get("b"));
        assertEquals(Json.of("😀é"), parsed.get("c"));
        assertEquals(Json.of("😀"), JsonCodec.decodeObject("{\"x\":\"\\ud83d\\ude00\"}".getBytes(StandardCharsets.UTF_8), 64).get("x"));
        Json value = Json.of(Json.object(
                "z", Json.of(List.of(Json.NULL, Json.of(true), Json.of(-1))),
                "a", Json.of("q\"\\\n\001😀"),
                "__proto__", Json.of(Map.of())));
        assertEquals("{\"__proto__\":{},\"a\":\"q\\\"\\\\\\n\\u0001😀\",\"z\":[null,true,-1]}", JsonCodec.stringify(value, 64));
        code("E1601", "63-bit encode", () -> JsonCodec.stringify(Json.of(Json.object("x", Json.of(Json.INT_MAX + 1))), 64));
        // Keys sort by code point, not by UTF-16 unit: U+FFFF precedes U+1F600.
        assertEquals("{\"\uffff\":1,\"😀\":2}", JsonCodec.stringify(Json.of(Json.object("😀", Json.of(2), "\uffff", Json.of(1))), 64));
        code("E1601", "lone surrogate encode", () -> JsonCodec.stringify(Json.of("\ud800"), 64));
    }

    @Test
    void depthFrameAndLimits() throws Exception {
        code("E1602", "decode depth", () -> JsonCodec.decodeObject("{\"a\":[[]]}".getBytes(StandardCharsets.UTF_8), 2));
        code("E1602", "frame bytes", () -> JsonCodec.frameBytes(Json.object("a", Json.of("xx")), 4, 64));
        assertArrayEquals("\0\0\0\7{\"a\":1}".getBytes(StandardCharsets.UTF_8), JsonCodec.frameBytes(Json.object("a", Json.of(1)), 64, 64));
        code("E1602", "zero limit", () -> HARD.with("max_arguments", 0).validate());
        Map<String, Json> extra = new TreeMap<>(HARD.toJson());
        extra.put("extra", Json.of(1));
        code("E1601", "extra limit", () -> Limits.fromJson(Json.of(extra)));
        Map<String, Json> bool = new TreeMap<>(HARD.toJson());
        bool.put("max_arguments", Json.of(true));
        code("E1601", "boolean limit", () -> Limits.fromJson(Json.of(bool)));
        Map<String, Json> huge = new TreeMap<>(HARD.toJson());
        huge.put("max_arguments", Json.of(1L << 60));
        code("E1601", "limit beyond the exact integer range", () -> Limits.fromJson(Json.of(huge)));
        assertEquals(HARD, Limits.fromJson(Json.of(HARD.toJson())));
    }

    @Test
    void valuesRoundtripExactly() throws Exception {
        List<Value> values = List.of(
                new Value.Int(0),
                new Value.Int(Json.INT_MIN),
                new Value.Int(Json.INT_MAX),
                new Value.Text("hé😀"),
                new Value.HashValue(hash('a')),
                new Value.Tuple(List.of(new Value.Int(3), new Value.Text("x"))),
                new Value.Constructor(hash('b'), List.of(new Value.Int(3))),
                new Value.Real(0x7ff0_0000_0000_0001L),
                Value.real(-0.0),
                Value.real(Double.POSITIVE_INFINITY),
                Value.UNIT);
        for (Value value : values) {
            Json first = Protocol.encodeValue(value, budget());
            Value decoded = Protocol.decodeValue(first, budget());
            assertEquals(value, decoded);
            Json second = Protocol.encodeValue(decoded, budget());
            assertEquals(JsonCodec.stringify(first, 64), JsonCodec.stringify(second, 64));
        }
        assertEquals(new Value.Real(0x8000_0000_0000_0000L), Value.real(-0.0));
        assertEquals("{\"bits\":\"8000000000000000\",\"kind\":\"real\"}", JsonCodec.stringify(Protocol.encodeValue(Value.real(-0.0), budget()), 64));
        for (String text : List.of("-0", "00", "+1", "4611686018427387904", "-4611686018427387905", " 1", "99999999999999999999")) {
            Json wire = Json.of(Json.object("kind", Json.of("int"), "value", Json.of(text)));
            code("E1601", text, () -> Protocol.decodeValue(wire, budget()));
        }
        code("E1604", "secret", () -> Protocol.decodeValue(json("{\"kind\":\"secret\",\"value\":\"x\"}"), budget()));
        code("E1604", "Int range", () -> Protocol.encodeValue(new Value.Int(Json.INT_MAX + 1), budget()));
        code("E1604", "Int range below", () -> Protocol.encodeValue(new Value.Int(Long.MIN_VALUE), budget()));
        code("E1601", "lone surrogate Text", () -> Protocol.encodeValue(new Value.Text("a\udc00"), budget()));
        code("E1602", "Text bytes", () -> Protocol.encodeValue(new Value.Text("é".repeat(131_073)), budget()));
        String upper = "{\"kind\":\"nominal\",\"identity\":\"" + "A".repeat(64) + "\",\"arguments\":[]}";
        code("E1601", "uppercase hash", () -> Protocol.decodeType(json(upper), budget()));
        Limits small = HARD.with("max_value_nodes", 1);
        code("E1602", "value nodes",
                () -> Protocol.decodeValue(json("{\"kind\":\"tuple\",\"items\":[{\"kind\":\"int\",\"value\":\"1\"}]}"), new Budget(small)));
        code("E1601", "uppercase real", () -> Protocol.decodeValue(json("{\"kind\":\"real\",\"bits\":\"7FF0000000000001\"}"), budget()));
        code("E1601", "short hash", () -> Hash.parse("A"));
        assertThrows(IllegalArgumentException.class, () -> new Hash("A"));
    }

    @Test
    void decodersFollowCoresCheckOrder() {
        String item = "{\"kind\":\"int\",\"value\":\"1\"}";
        String many = String.join(",", java.util.Collections.nCopies(65, item));
        // Core: exact fields, one node, identity, then the count.
        String bad = "{\"kind\":\"constructor\",\"identity\":\"bad\",\"arguments\":[" + many + "]}";
        code("E1601", "constructor identity before count", () -> Protocol.decodeValue(json(bad), budget()));
        String good = "{\"kind\":\"constructor\",\"identity\":\"" + "a".repeat(64) + "\",\"arguments\":[" + many + "]}";
        code("E1602", "constructor argument count", () -> Protocol.decodeValue(json(good), budget()));
        // A malformed descriptor is refused as malformed even when the budget is spent.
        Limits spent = HARD.with("max_value_nodes", 0);
        String nominal = "{\"kind\":\"nominal\",\"identity\":\"" + "a".repeat(64) + "\"}";
        code("E1601", "exact fields before the node", () -> Protocol.decodeType(json(nominal), new Budget(spent)));
        code("E1604", "kind before the node", () -> Protocol.decodeValue(json("{\"kind\":\"secret\"}"), new Budget(spent)));
        code("E1601", "missing kind", () -> Protocol.decodeValue(json("{\"value\":\"1\"}"), budget()));
    }

    private static Map<String, Json> hello() {
        return Json.object(
                "carrier", Json.of(Compat.CARRIER),
                "kind", Json.of("core_hello"),
                "limits", Json.of(HARD.toJson()),
                "versions", Json.of(List.of(Json.of(Compat.PROTOCOL))));
    }

    private static Map<String, Json> with(Map<String, Json> frame, String key, Json value) {
        Map<String, Json> changed = new TreeMap<>(frame);
        changed.put(key, value);
        return changed;
    }

    @Test
    void helloAndSelection() throws Exception {
        code("E1601", "extra hello field", () -> new Session().receive(with(hello(), "extra", Json.of(1))));
        code("E1600", "unknown version",
                () -> new Session().receive(with(hello(), "versions", Json.of(List.of(Json.of("jacquard-host-v9"))))));
        code("E1601", "lowered hello limits",
                () -> new Session().receive(with(hello(), "limits", Json.of(HARD.with("max_arguments", 1).toJson()))));
        code("E1608", "first frame", () -> new Session().receive(Json.object("kind", Json.of("fatal"), "protocol", Json.of(Compat.PROTOCOL))));
        Session session = new Session();
        assertNull(session.receive(hello()));
        code("E1608", "invoke before select", () -> session.invoke(hash('a'), unit(), List.of(), List.of()));
        code("E1602", "select above ceiling", () -> session.select(HARD.with("max_frame_bytes", HARD.maxFrameBytes() + 1)));
    }

    @Test
    void invokeFrameValidation() throws Exception {
        Hash a = hash('a');
        Hash b = hash('b');
        Interface unsorted = new Interface(List.of(), new BoundaryType.Tuple(List.of()), List.of(b, a));
        code("E1601", "unsorted effects", () -> Protocol.invokeFrame(a, unsorted, List.of(), List.of(), HARD));
        Interface spec = new Interface(List.of(), new BoundaryType.Tuple(List.of()), List.of(a));
        code("E1601", "operation outside row",
                () -> Protocol.invokeFrame(a, spec, List.of(), List.of(new OperationEntry(b, a)), HARD));
        code("E1601", "duplicate operation",
                () -> Protocol.invokeFrame(a, spec, List.of(), List.of(new OperationEntry(a, b), new OperationEntry(a, b)), HARD));
        code("E1601", "argument count", () -> Protocol.invokeFrame(a, spec, List.of(new Value.Int(1)), List.of(), HARD));
        Map<String, Json> frame = Protocol.invokeFrame(a, spec, List.of(), List.of(new OperationEntry(a, b)), HARD);
        assertEquals(
                "{\"effects\":[\"" + a + "\"],\"operations\":[{\"effect\":\"" + a + "\",\"mode\":\"once\",\"operation\":\"" + b + "\"}]}",
                JsonCodec.stringify(frame.get("capabilities"), 64));
    }

    @Test
    void jsStringMatchesJavascriptCoercion() {
        assertEquals("x", Replayer.jsString(Json.of("x")));
        assertEquals("null", Replayer.jsString(Json.NULL));
        assertEquals("5", Replayer.jsString(Json.of(5)));
        assertEquals("true", Replayer.jsString(Json.of(true)));
        assertEquals("1,,a,b", Replayer.jsString(json("[1,null,[\"a\",\"b\"]]")));
        assertEquals("[object Object]", Replayer.jsString(json("{\"a\":1}")));
    }

    @Test
    void pinnedKitLoads() {
        Kit kit = kit();
        assertEquals(Kit.TRANSCRIPT_COUNT, kit.transcripts().size());
        assertTrue(kit.identities().containsKey("kit-echo"));
        assertNotNull(Kit.file("kit/fixtures.jac"));
        assertNotNull(Kit.file("kit/README.md"));
        assertNull(Kit.file("../NOTICE"));
    }

    @Test
    void outcomeEvidenceAndResultShareOneFrameBudget() throws Exception {
        Kit kit = kit();
        Map<String, Json> outcome = kit.transcripts().get(0).coreOutput().get(1);
        Protocol.parseCoreFrame(outcome, HARD.with("max_value_nodes", 2));
        code("E1602", "pure outcome", () -> Protocol.parseCoreFrame(outcome, HARD.with("max_value_nodes", 1)));
        // An error outcome spends its two evidence types only.
        List<Map<String, Json>> refused = kit.transcript("refused-authority").coreOutput();
        Map<String, Json> errorOutcome = refused.get(refused.size() - 1);
        Protocol.parseCoreFrame(errorOutcome, HARD.with("max_value_nodes", 2));
        code("E1602", "error outcome", () -> Protocol.parseCoreFrame(errorOutcome, HARD.with("max_value_nodes", 1)));
    }

    @Test
    void sessionEnforcesOneInvocationAndLocalTerminalRefusal() throws Exception {
        Kit kit = kit();
        Transcript pure = kit.transcripts().get(0);
        Session session = new Session();
        session.receive(pure.coreOutput().get(0));
        session.select(HARD);
        session.invoke(kit.identity("kit-ready"), unit(), List.of(), List.of());
        code("E1608", "second invoke", () -> session.invoke(hash('a'), unit(), List.of(), List.of()));
        session.receive(pure.coreOutput().get(1));
        code("E1608", "shutdown after terminal", session::shutdown);
        code("E1608", "frame after terminal", () -> session.receive(pure.coreOutput().get(1)));
    }

    @Test
    void requestOrderingAndOneResponseSlot() throws Exception {
        Transcript transcript = kit().transcript("one-once-effect-success");
        Session session = new Session();
        session.receive(transcript.coreOutput().get(0));
        session.select(HARD);
        Map<String, Json> invoke = null;
        for (Step step : transcript.hostInput()) {
            if (Json.of("invoke").equals(step.frame().get("kind"))) {
                invoke = step.frame();
                break;
            }
        }
        assertNotNull(invoke);
        Map<String, Json> wire = invoke.get("interface").asObject();
        List<BoundaryType> parameters = new ArrayList<>();
        for (Json parameter : wire.get("parameters").asArray()) {
            parameters.add(Protocol.decodeType(parameter, budget()));
        }
        List<Hash> effects = new ArrayList<>();
        for (Json effect : wire.get("effects").asArray()) {
            effects.add(Hash.parse(effect.asString()));
        }
        Interface spec = new Interface(parameters, Protocol.decodeType(wire.get("result"), budget()), effects);
        List<OperationEntry> operations = new ArrayList<>();
        for (Json entry : invoke.get("capabilities").asObject().get("operations").asArray()) {
            Map<String, Json> fields = entry.asObject();
            operations.add(new OperationEntry(Hash.parse(fields.get("effect").asString()), Hash.parse(fields.get("operation").asString())));
        }
        Hash callable = Hash.parse(invoke.get("target").asObject().get("callable").asString());
        session.invoke(callable, spec, List.of(new Value.Text("ping")), operations);
        Map<String, Json> request = transcript.coreOutput().get(1);
        code("E1601", "numeric invocation id", () -> session.receive(with(request, "invocation_id", Json.of(0))));
        code("E1608", "other invocation", () -> session.receive(with(request, "invocation_id", Json.of("ffffffffffffffff"))));
        code("E1608", "skipped ordinal", () -> session.receive(with(request, "request_id", Json.of("0000000000000002"))));
        code("E1608", "ordinal beyond the exact range", () -> session.receive(with(request, "request_id", Json.of("ffffffffffffffff"))));
        session.receive(request);
        code("E1608", "second request while waiting", () -> session.receive(request));
        session.respondOk(new Value.Text("pong"));
        code("E1608", "second response", () -> session.respondOk(new Value.Text("again")));
    }

    private static Replay replayStandin(Transcript transcript, String original) {
        return Replayer.replayOne(transcript, "unused", standin("replay", original, Duration.ofSeconds(20)), new Cancel());
    }

    @Test
    void allRecordingsReplayThroughTheJavaCarrier() {
        for (Transcript transcript : kit().transcripts()) {
            Replay replay = replayStandin(transcript, transcript.name());
            assertTrue(replay.differences().isEmpty() && replay.proseDrift().isEmpty() && replay.status().equals(transcript.status()),
                    transcript.name() + ": differences " + replay.differences() + ", prose drift " + replay.proseDrift() + ", status "
                            + replay.status());
        }
    }

    @Test
    void replayTreatsAbsentExpectationsLikeTheReference() {
        Kit kit = kit();
        Transcript missing = kit.transcript("pure-success").withExpect(Map.of());
        assertEquals("diverges", replayStandin(missing, "pure-success").status(), "absent terminal expectation conformed");
        Map<String, Json> expect = new TreeMap<>(kit.transcript("unknown-version").expect());
        expect.remove("terminal_frames_max");
        Transcript unbounded = kit.transcript("unknown-version").withExpect(expect);
        assertEquals("diverges", replayStandin(unbounded, "unknown-version").status(), "missing terminal_frames_max conformed");
    }

    private static Json summaryOf(Map<String, Json> frame) {
        return frame.get("result").asObject().get("diagnostics").asArray().get(0).asObject().get("summary");
    }

    @Test
    void proseDriftIsReportedWithoutNormalizing() {
        Transcript original = kit().transcript("refused-authority");
        List<Map<String, Json>> output = new ArrayList<>(original.coreOutput());
        Map<String, Json> last = output.get(output.size() - 1);
        Map<String, Json> result = new TreeMap<>(last.get("result").asObject());
        List<Json> diagnostics = new ArrayList<>(result.get("diagnostics").asArray());
        Map<String, Json> diagnostic = new TreeMap<>(diagnostics.get(0).asObject());
        diagnostic.put("summary", Json.of(diagnostic.get("summary").asString() + " changed"));
        diagnostics.set(0, Json.of(diagnostic));
        result.put("diagnostics", Json.of(diagnostics));
        output.set(output.size() - 1, with(last, "result", Json.of(result)));
        Replay replay = replayStandin(original.withCoreOutput(output), original.name());
        assertTrue(replay.differences().isEmpty() && replay.proseDrift().size() == 1, replay.differences() + " " + replay.proseDrift());
        List<Map<String, Json>> frames = replay.coreFrames();
        assertEquals(summaryOf(original.coreOutput().get(original.coreOutput().size() - 1)), summaryOf(frames.get(frames.size() - 1)));
    }

    @Test
    void malformedExtraAndNoncanonicalCoreFramesFailClosed() {
        for (String[] pair : new String[][] {{"unknown-field", "E1601"}, {"invalid-hash", "E1601"}, {"extra", "E1608"}}) {
            InvocationResult result = invokeReady(faulty(pair[0], Duration.ofSeconds(10), null));
            assertTrue(result.kind() == ResultKind.PROTOCOL_ERROR && result.code().equals(pair[1]), pair[0] + ": " + result);
        }
    }

    @Test
    void carrierFailuresRetainHostEvidence() {
        for (String mode : List.of("exit", "truncated", "silent", "stall", "nonzero")) {
            boolean expired = mode.equals("silent") || mode.equals("stall");
            // Deadlines leave room for the stand-in JVM to start.
            InvocationResult result = invokeReady(faulty(mode, expired ? Duration.ofSeconds(2) : Duration.ofSeconds(10), null));
            assertTrue(result.kind() == ResultKind.CARRIER_LOSS && result.code().equals("E1611")
                    && Json.of(Host.EVIDENCE_SCHEMA).equals(result.hostEvidence().get("schema")), mode + ": " + result.hostEvidence());
            boolean sawOutcome = result.coreFrames().stream().anyMatch(frame -> Json.of("outcome").equals(frame.get("kind")));
            assertEquals(mode.equals("stall") || mode.equals("nonzero"), sawOutcome, mode + ": outcome observed");
            assertEquals(expired, "deadline".equals(reason(result)), mode + ": " + reason(result));
            InvocationException error = assertThrows(InvocationException.class, result::valueOrThrow);
            assertEquals(expired ? Failure.DEADLINE_EXCEEDED : Failure.WORKER_LOST, error.failure(), mode);
        }
    }

    @Test
    void framesAfterSelectionAreReadUnderTheSelectedLimits() {
        InvocationResult result = invokeReady(faulty("oversized-early", Duration.ofSeconds(10), HARD.with("max_frame_bytes", 1024)));
        assertTrue(result.kind() == ResultKind.PROTOCOL_ERROR && result.code().equals("E1602"), result.toString());
    }

    @Test
    void stderrIsDrainedAndRetentionObeysTheSelectedCeiling() {
        InvocationResult result = invokeReady(faulty("stderr", Duration.ofSeconds(10), HARD.with("max_stderr_bytes", 17)));
        assertTrue(result.ok() && result.stderr().length == 17, result + ", stderr " + result.stderr().length + " bytes");
        assertEquals("x".repeat(17), new String(result.stderr(), StandardCharsets.US_ASCII));
    }

    @Test
    void backpressureSpendsTheSameLifetime() {
        Kit kit = kit();
        BoundaryType text = new BoundaryType.Nominal(kit.identity("text"), List.of());
        Interface spec = new Interface(List.of(text), text, List.of());
        long start = System.nanoTime();
        Host host = faulty("no-read", Duration.ofSeconds(2), null);
        InvocationResult result = host.invoke(kit.identity("kit-ready"), spec, List.of(new Value.Text("x".repeat(220_000))),
                new OperationRegistry(), new Cancel());
        Duration elapsed = Duration.ofNanos(System.nanoTime() - start);
        assertTrue("deadline".equals(reason(result)) && elapsed.compareTo(Duration.ofSeconds(5)) < 0, reason(result) + " after " + elapsed);
        assertEquals(Failure.DEADLINE_EXCEEDED, assertThrows(InvocationException.class, result::valueOrThrow).failure());
    }

    @Test
    void cancellationAbortsWithoutRetry() {
        Cancel cancel = new Cancel();
        Thread trigger = new Thread(() -> {
            try {
                Thread.sleep(300);
            } catch (InterruptedException ignored) {
                return;
            }
            cancel.cancel();
        });
        trigger.start();
        InvocationResult result = faulty("silent", Duration.ofSeconds(10), null)
                .invoke(kit().identity("kit-ready"), unit(), List.of(), new OperationRegistry(), cancel);
        assertEquals("aborted", reason(result));
        assertEquals(Failure.WORKER_LOST, assertThrows(InvocationException.class, result::valueOrThrow).failure());
    }

    @Test
    void interruptionAbortsAndTheInterruptIsRestored() throws Exception {
        Thread caller = Thread.currentThread();
        Thread trigger = new Thread(() -> {
            try {
                Thread.sleep(300);
            } catch (InterruptedException ignored) {
                return;
            }
            caller.interrupt();
        });
        trigger.start();
        long start = System.nanoTime();
        InvocationResult result = invokeReady(faulty("silent", Duration.ofSeconds(10), null));
        boolean restored = Thread.interrupted();
        trigger.join();
        assertEquals("aborted", reason(result), result.toString());
        assertTrue(restored, "the caller's interrupt status was not restored");
        assertTrue(Duration.ofNanos(System.nanoTime() - start).compareTo(Duration.ofSeconds(5)) < 0);
    }

    @Test
    void localRefusalStartsNoWorkerAndInvalidOptionsAreRejected() {
        Options missing = Options.defaults().withCommand(List.of("/no/such/executable"));
        Host refused = new Host("unused", new HostOptions(missing, HARD.with("max_frame_bytes", 20)));
        InvocationResult result = invokeReady(refused);
        assertTrue(result.kind() == ResultKind.HOST_ERROR && result.code().equals("E1602") && result.exitStatus() == null, result.toString());
        assertEquals("invoke_refused_locally", reason(result));
        Host spawn = new Host("unused", new HostOptions(missing, null));
        InvocationResult failed = invokeReady(spawn);
        assertEquals("worker_spawn_failed", reason(failed), failed.toString());
        assertTrue(failed.message().contains("/no/such/executable"), failed.message());
        for (HostOptions options : List.of(
                new HostOptions(Options.defaults().withDeadline(Duration.ZERO), null),
                new HostOptions(Options.defaults().withDeadline(Duration.ofSeconds(-1)), null),
                new HostOptions(Options.defaults().withCommand(List.of("")), null),
                new HostOptions(null, HARD.with("max_arguments", 0)))) {
            assertThrows(IllegalArgumentException.class, () -> new Host("unused", options), "accepted " + options);
        }
    }

    @Test
    void registryRefusesDuplicatesAndSortsEntries() {
        Hash a = hash('a');
        Hash b = hash('b');
        OperationRegistry registry = new OperationRegistry();
        registry.bind(b, a, (context, args) -> Value.UNIT);
        registry.bind(a, b, (context, args) -> Value.UNIT);
        assertThrows(IllegalArgumentException.class, () -> registry.bind(a, b, (context, args) -> Value.UNIT));
        List<OperationEntry> entries = registry.entries();
        assertEquals(a, entries.get(0).effect());
        assertEquals(b, entries.get(1).effect());
    }

    /** Runs one handler against the stand-in replaying {@code one-once-effect-success}. */
    private static Map<String, Json> response(Handler handler) {
        Kit kit = kit();
        BoundaryType text = new BoundaryType.Nominal(kit.identity("text"), List.of());
        Interface spec = new Interface(List.of(text), text, List.of(kit.identity("kit-world")));
        OperationRegistry registry = new OperationRegistry().bind(kit.identity("kit-world"), kit.identity("send"), handler);
        Host host = new Host("unused", new HostOptions(standin("replay", "one-once-effect-success", Duration.ofSeconds(20)), null));
        InvocationResult result = host.invoke(kit.identity("kit-echo"), spec, List.of(new Value.Text("ping")), registry, new Cancel());
        // The stand-in replays its recorded outcome whatever the host answers.
        assertTrue(result.ok(), result.toString());
        assertEquals(3, result.hostFrames().size(), result.hostFrames().toString());
        return result.hostFrames().get(2);
    }

    private static void failure(Map<String, Json> frame, FailureCategory category, String message) {
        assertEquals(Json.of("effect_failure"), frame.get("kind"), frame.toString());
        assertEquals(Json.of(category.wireName()), frame.get("category"), frame.toString());
        assertEquals(Json.of(category.completion()), frame.get("completion"), frame.toString());
        assertEquals(Json.of(message), frame.get("message"), frame.toString());
    }

    @Test
    void handlerAnswersMapToTheirFailureCategories() {
        AtomicReference<List<Value>> seen = new AtomicReference<>();
        Map<String, Json> ok = response((context, args) -> {
            seen.set(args);
            return new Value.Text("pong");
        });
        assertEquals(List.of(new Value.Text("ping")), seen.get());
        assertEquals(Json.of("effect_ok"), ok.get("kind"));
        failure(response((context, args) -> {
            throw HandlerException.refused("no authority");
        }), FailureCategory.REFUSED_AUTHORITY, "no authority");
        failure(response((context, args) -> {
            throw new IllegalStateException("private detail");
        }), FailureCategory.COMPLETION_UNKNOWN, "handler threw an exception");
        failure(response((context, args) -> {
            throw new java.io.IOException("private detail");
        }), FailureCategory.COMPLETION_UNKNOWN, "handler threw an exception");
        failure(response((context, args) -> {
            throw new StackOverflowError();
        }), FailureCategory.COMPLETION_UNKNOWN, "handler threw an exception");
        failure(response((context, args) -> null), FailureCategory.OUTSIDE_FAILURE,
                "handler result has no encoding within the selected limits");
        failure(response((context, args) -> new Value.Int(Long.MAX_VALUE)), FailureCategory.OUTSIDE_FAILURE,
                "handler result has no encoding within the selected limits");
        // Messages are cut to max_host_message_bytes at a code point, and a lone
        // surrogate becomes U+FFFD.
        failure(response((context, args) -> {
            throw HandlerException.outsideFailed("\ud800" + "é".repeat(3000));
        }), FailureCategory.OUTSIDE_FAILURE, "\ufffd" + "é".repeat(2046));
        assertFalse(Host.bounded("😀", 3).contains("\ud83d"));
    }

    /**
     * 256 references to one text at the text ceiling: within the node,
     * collection, and text limits, but over 384 MiB once escaped.
     */
    private static Value hugeResult() {
        return new Value.Tuple(java.util.Collections.nCopies(256, new Value.Text("\u0001".repeat(262_144))));
    }

    @Test
    void aResultFarOverTheFrameCeilingIsRefusedWithoutEncodingItAll() {
        long start = System.nanoTime();
        Map<String, Json> frame = response((context, args) -> hugeResult());
        long elapsed = (System.nanoTime() - start) / 1_000_000;
        failure(frame, FailureCategory.OUTSIDE_FAILURE, "handler result has no encoding within the selected limits");
        assertTrue(elapsed < 3000, "returned after " + elapsed + " ms");
    }

    @Test
    void cancellationWhileAHugeResultIsEncodedEndsTheCallPromptly() {
        Kit kit = kit();
        BoundaryType text = new BoundaryType.Nominal(kit.identity("text"), List.of());
        Interface spec = new Interface(List.of(text), text, List.of(kit.identity("kit-world")));
        Cancel cancel = new Cancel();
        OperationRegistry registry = new OperationRegistry().bind(kit.identity("kit-world"), kit.identity("send"), (context, args) -> {
            // Cancel from elsewhere once the result is being encoded. A
            // bounded encoder has finished by then; an unbounded one is still
            // encoding seconds later, and only then sees the cancellation.
            Thread.ofPlatform().daemon().start(() -> {
                try {
                    Thread.sleep(100);
                } catch (InterruptedException ignored) {
                    return;
                }
                cancel.cancel();
            });
            return hugeResult();
        });
        Host host = new Host("unused", new HostOptions(standin("replay", "one-once-effect-success", Duration.ofSeconds(20)), null));
        long start = System.nanoTime();
        InvocationResult result = host.invoke(kit.identity("kit-echo"), spec, List.of(new Value.Text("ping")), registry, cancel);
        long elapsed = (System.nanoTime() - start) / 1_000_000;
        assertTrue(result.ok() || "aborted".equals(reason(result)), result.toString());
        assertTrue(elapsed < 3000, "returned after " + elapsed + " ms");
    }

    /** Whether the caller is decoding a string, which a 256 KiB request makes long. */
    private static boolean decoding(StackTraceElement[] stack) {
        for (StackTraceElement frame : stack) {
            if (frame.getClassName().equals(JsonCodec.class.getName() + "$Decoder") && frame.getMethodName().equals("quoted")) {
                return true;
            }
        }
        return false;
    }

    /**
     * Cancels the call, or interrupts the caller, once its stack matches
     * {@code when}, and checks that the handler then never started. Retries
     * until the stop lands there.
     */
    private static void noHandlerAfterAStop(boolean interrupt, java.util.function.Predicate<StackTraceElement[]> when) throws Exception {
        Kit kit = kit();
        BoundaryType text = new BoundaryType.Nominal(kit.identity("text"), List.of());
        Interface spec = new Interface(List.of(text), text, List.of(kit.identity("kit-world")));
        for (int attempt = 0; attempt < 10; attempt++) {
            Cancel cancel = new Cancel();
            java.util.concurrent.atomic.AtomicLong stoppedAt = new java.util.concurrent.atomic.AtomicLong();
            java.util.concurrent.atomic.AtomicLong startedAt = new java.util.concurrent.atomic.AtomicLong();
            OperationRegistry registry = new OperationRegistry().bind(kit.identity("kit-world"), kit.identity("send"), (context, args) -> {
                startedAt.set(System.nanoTime());
                return Value.UNIT;
            });
            Thread caller = Thread.currentThread();
            java.util.concurrent.atomic.AtomicBoolean done = new java.util.concurrent.atomic.AtomicBoolean();
            Thread watcher = Thread.ofPlatform().daemon().start(() -> {
                while (!done.get()) {
                    if (when.test(caller.getStackTrace())) {
                        stoppedAt.set(System.nanoTime());
                        if (interrupt) {
                            caller.interrupt();
                        } else {
                            cancel.cancel();
                        }
                        return;
                    }
                }
            });
            Host host = new Host("unused", new HostOptions(standin("large-request", "", Duration.ofSeconds(20)), null));
            InvocationResult result = host.invoke(kit.identity("kit-echo"), spec, List.of(new Value.Text("ping")), registry, cancel);
            done.set(true);
            watcher.join();
            boolean interrupted = Thread.interrupted();
            if (stoppedAt.get() == 0) {
                continue;
            }
            assertEquals(0, startedAt.get(), "the handler started " + (startedAt.get() - stoppedAt.get()) / 1_000_000 + " ms after the stop");
            assertEquals("aborted", reason(result), result.toString());
            assertEquals(interrupt, interrupted);
            return;
        }
        throw new AssertionError("the stop never landed where the test aimed it");
    }

    @Test
    void cancellationWhileTheRequestIsDecodedStartsNoHandler() throws Exception {
        noHandlerAfterAStop(false, OfflineTest::decoding);
    }

    @Test
    void interruptionWhileTheRequestIsDecodedStartsNoHandler() throws Exception {
        noHandlerAfterAStop(true, OfflineTest::decoding);
    }

    /**
     * Cancels the call, or interrupts the caller, from the handler thread just
     * before its check, and checks that the handler never ran and the call
     * was aborted.
     */
    private static void noHandlerAfterAStopAtTheHandlerCheck(boolean interrupt) {
        noHandlerAfterAStopAtTheHandlerCheck(interrupt, false);
    }

    /**
     * As above; with {@code consumed}, the check waits until the caller has
     * consumed its interrupt, so only the host's record of it remains.
     */
    private static void noHandlerAfterAStopAtTheHandlerCheck(boolean interrupt, boolean consumed) {
        Kit kit = kit();
        BoundaryType text = new BoundaryType.Nominal(kit.identity("text"), List.of());
        Interface spec = new Interface(List.of(text), text, List.of(kit.identity("kit-world")));
        Cancel cancel = new Cancel();
        Thread caller = Thread.currentThread();
        java.util.concurrent.atomic.AtomicBoolean ran = new java.util.concurrent.atomic.AtomicBoolean();
        java.util.concurrent.atomic.AtomicBoolean stopped = new java.util.concurrent.atomic.AtomicBoolean();
        OperationRegistry registry = new OperationRegistry().bind(kit.identity("kit-world"), kit.identity("send"), (context, args) -> {
            ran.set(true);
            return Value.UNIT;
        });
        Runnable stop = () -> {
            stopped.set(true);
            if (interrupt) {
                caller.interrupt();
                long give = System.nanoTime() + 5_000_000_000L;
                while (consumed && caller.isInterrupted() && System.nanoTime() < give) {
                    Thread.onSpinWait();
                }
            } else {
                cancel.cancel();
            }
        };
        Host.handlerCheckHook = stop;
        InvocationResult result;
        try {
            Host host = new Host("unused", new HostOptions(standin("replay", "one-once-effect-success", Duration.ofSeconds(20)), null));
            result = host.invoke(kit.identity("kit-echo"), spec, List.of(new Value.Text("ping")), registry, cancel);
        } finally {
            Host.handlerCheckHook = null;
        }
        boolean interrupted = Thread.interrupted();
        assertTrue(stopped.get(), "the seam never ran");
        assertFalse(ran.get(), "the handler ran after the stop");
        assertEquals("aborted", reason(result), result.toString());
        assertEquals(interrupt, interrupted);
    }

    @Test
    void aCancellationJustBeforeTheHandlerIsCalledRunsNoHandler() {
        noHandlerAfterAStopAtTheHandlerCheck(false);
    }

    @Test
    void anInterruptionJustBeforeTheHandlerIsCalledRunsNoHandler() {
        noHandlerAfterAStopAtTheHandlerCheck(true);
    }

    @Test
    void anInterruptTheCallerHasAlreadyConsumedStillRunsNoHandler() {
        for (int i = 0; i < 20; i++) {
            noHandlerAfterAStopAtTheHandlerCheck(true, true);
        }
    }

    @Test
    void validatingManyReferencesToALongTextIsBounded() {
        // 4 x 1022 references to one text of 65536 emoji (262144 UTF-8
        // bytes): within the node, collection, and text limits, and about
        // 1 GiB of text to scan if each reference were checked in full.
        Value.Text text = new Value.Text("😀".repeat(65_536));
        Value value = new Value.Tuple(java.util.Collections.nCopies(4, new Value.Tuple(java.util.Collections.nCopies(1022, text))));
        java.util.concurrent.atomic.AtomicLong returned = new java.util.concurrent.atomic.AtomicLong();
        Map<String, Json> frame = response((context, args) -> {
            returned.set(System.nanoTime());
            return value;
        });
        long elapsed = (System.nanoTime() - returned.get()) / 1_000_000;
        failure(frame, FailureCategory.OUTSIDE_FAILURE, "handler result has no encoding within the selected limits");
        assertTrue(elapsed < 500, "answered " + elapsed + " ms after the handler returned");
    }

    @Test
    void aTextFarOverTheTextCeilingIsRefusedWithoutScanningIt() {
        Value.Text text = new Value.Text("é".repeat(32 << 20));
        java.util.concurrent.atomic.AtomicLong returned = new java.util.concurrent.atomic.AtomicLong();
        Map<String, Json> frame = response((context, args) -> {
            returned.set(System.nanoTime());
            return text;
        });
        long elapsed = (System.nanoTime() - returned.get()) / 1_000_000;
        failure(frame, FailureCategory.OUTSIDE_FAILURE, "handler result has no encoding within the selected limits");
        assertTrue(elapsed < 500, "answered " + elapsed + " ms after the handler returned");
    }

    @Test
    void replayStopsReadingAWorkerThatSendsPastTheRecording() {
        Transcript pure = kit().transcripts().get(0);
        Runtime runtime = Runtime.getRuntime();
        System.gc();
        long heapBefore = runtime.totalMemory() - runtime.freeMemory();
        long start = System.nanoTime();
        Replay replay = Replayer.replayOne(pure, "unused", standin("flood-after-terminal", "", Duration.ofSeconds(10)), new Cancel());
        long elapsed = (System.nanoTime() - start) / 1_000_000;
        System.gc();
        long heapAfter = runtime.totalMemory() - runtime.freeMemory();
        assertFalse(replay.matchesRecording(), replay.toString());
        assertTrue(replay.differences().contains("Core sent more frames than the recording"), replay.differences().toString());
        assertTrue(elapsed < 3000, "returned after " + elapsed + " ms");
        assertTrue(heapAfter - heapBefore < 64L << 20, "the heap grew by " + ((heapAfter - heapBefore) >> 20) + " MiB");
    }

    @Test
    void theEncoderStopsAtTheFrameCeiling() throws ProtocolException {
        Map<String, Json> frame = Map.of("k", Json.of("\u0001".repeat(1000)));
        ProtocolException refused = assertThrows(ProtocolException.class, () -> JsonCodec.frameBytes(frame, 6007, 64));
        assertEquals("payload exceeds max_frame_bytes", refused.detail());
        // {"k":"…"} is 8 bytes plus 6 per escaped U+0001: exactly 6008.
        assertEquals(4 + 6008, JsonCodec.frameBytes(frame, 6008, 64).length);
        assertEquals("\"\\u0001\\u001f\"", JsonCodec.stringify(Json.of("\u0001\u001f"), 64));
        // A text that cannot fit is refused by length, before the scan that
        // would find its lone surrogate.
        ProtocolException long_ = assertThrows(ProtocolException.class,
                () -> JsonCodec.frameBytes(Map.of("k", Json.of("\ud800" + "x".repeat(7000))), 6008, 64));
        assertEquals("payload exceeds max_frame_bytes", long_.detail());
    }
}
