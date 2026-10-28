package org.jacquardlang.host.sdk;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.junit.jupiter.api.Assertions.fail;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.attribute.PosixFilePermission;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.Set;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.stream.Stream;

import org.jacquardlang.host.Failure;
import org.jacquardlang.host.HandlerContext;
import org.jacquardlang.host.InvocationResult;
import org.jacquardlang.host.carrier.Cancel;
import org.jacquardlang.host.json.Json;
import org.jacquardlang.host.json.JsonCodec;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

/**
 * Live SDK tests against the real Core named by {@code JACQUARD_BIN} and
 * {@code JACQUARD_PRELUDE}. Each is skipped without them, and fails instead
 * when {@code JACQUARD_REQUIRE_LIVE} is set, as CI does.
 */
class SdkLiveTest {
    @TempDir
    Path root;

    private Runtime runtime;

    static void requireLive() {
        String bin = System.getenv("JACQUARD_BIN");
        String prelude = System.getenv("JACQUARD_PRELUDE");
        if (bin == null || bin.isEmpty() || prelude == null || prelude.isEmpty()) {
            if (System.getenv("JACQUARD_REQUIRE_LIVE") != null) {
                fail("live tests need JACQUARD_BIN and JACQUARD_PRELUDE");
            }
            assumeTrue(false, "live tests need JACQUARD_BIN and JACQUARD_PRELUDE");
        }
    }

    @BeforeEach
    void live() throws Exception {
        requireLive();
        runtime = new Runtime(RuntimeOptions.defaults());
    }

    @AfterEach
    void clearHook() {
        Operations.afterDecodeHook = null;
    }

    static Path example(String project) {
        return Path.of("..", "examples", project, "rules.jac").toAbsolutePath().normalize();
    }

    private PrepareOptions options() {
        return new PrepareOptions(root.resolve("cache"));
    }

    private Program prepare(Path source) throws JacquardException {
        return runtime.prepare(List.of(source), options());
    }

    private Path source(String name, String text) throws Exception {
        return Files.writeString(root.resolve(name), text, StandardCharsets.UTF_8);
    }

    @Test
    void quickstartCallsInvocationsAndSavedFrames() throws Exception {
        Function shipping = prepare(example("quickstart")).function("shipping-cost");
        assertEquals(5L, shipping.call(Codec.INT, 35));
        assertEquals(0L, shipping.call(Codec.INT, 75L));
        Invocation invocation = shipping.invoke(35);
        assertEquals(Dynamic.of(5), invocation.value());
        assertEquals("shipping-cost: ok", invocation.render());
        Path path = root.resolve("inspection.json");
        invocation.save(path);
        byte[] raw = Files.readAllBytes(path);
        assertEquals('\n', raw[raw.length - 1]);
        Map<String, Json> saved = JsonCodec.decodeObject(java.util.Arrays.copyOf(raw, raw.length - 1), 128);
        assertEquals(Json.of("jacquard-host-inspection-v1"), saved.get("schema"));
        assertEquals(invocation.raw().coreFrames().size(), saved.get("core_frames").asArray().size());
        assertEquals(Set.of(PosixFilePermission.OWNER_READ, PosixFilePermission.OWNER_WRITE), Files.getPosixFilePermissions(path));
    }

    /** The README's SDK examples, compiled from {@code readme/}, against Core. */
    @Test
    void theReadmeExamplesRun() throws Exception {
        Path saved = root.resolve("shipping.json");
        assertEquals(5L, org.jacquardlang.host.readme.Shipping.shipping(example("quickstart"), root.resolve("cache"), saved));
        assertTrue(Files.exists(saved));
        Program documents = prepare(source("documents.jac",
                "once effect Documents where {\n  read-document : (Text) -> Text\n}\n\nsummarize : (Text) ->{Documents} Text\nsummarize(key) = read-document(key)\n"));
        Function summarize = org.jacquardlang.host.readme.Documents.summarize(documents);
        assertEquals("A synthetic document supplied by Java.", summarize.call(Codec.TEXT, "example"));
        JacquardException refused = assertThrows(JacquardException.class, () -> summarize.call(Codec.TEXT, "other"));
        assertEquals(JacquardException.Kind.INVOCATION, refused.kind());
        assertEquals(Failure.OPERATION_FAILED, refused.failure(), refused.getMessage());
        Function route = prepare(example("triage")).function("triage.batch");
        var incident = new org.jacquardlang.host.readme.Incidents.Incident("inc-1", "checkout", 5, 3, true, Optional.empty());
        List<org.jacquardlang.host.readme.Incidents.Triage> routed = route.call(Codec.list(org.jacquardlang.host.readme.Incidents.TRIAGE),
                new TriagePolicy(4, 60), Codec.list(org.jacquardlang.host.readme.Incidents.INCIDENT).arg(List.of(incident)));
        assertEquals(List.of(new org.jacquardlang.host.readme.Incidents.Routed("inc-1", "checkout-oncall",
                org.jacquardlang.host.readme.Incidents.Urgency.PAGE_NOW, List.of("unowned", "customer-impact"))), routed);
    }

    record TriagePolicy(long page, long stale) {}

    record Incident(String id, String service, long severity, long age, boolean visible, Optional<String> owner) {}

    enum Urgency {
        PAGE_NOW,
        WORK_TODAY,
        WATCH_ONLY
    }

    sealed interface Triage {}

    record Routed(String id, String team, Urgency urgency, List<String> reasons) implements Triage {}

    record InvalidIncident(String id) implements Triage {}

    static final Codec<Triage> TRIAGE = Codec.union(Triage.class);

    static Incident incident(long i) {
        return new Incident(Long.toString(i), "api", i % 5 + 1, i * 3, i % 2 == 0, i % 3 != 0 ? Optional.of("ops") : Optional.empty());
    }

    @Test
    void recordsEnumsOptionsAndExplicitBatches() throws Exception {
        Function route = prepare(example("triage")).function("triage.batch");
        TriagePolicy policy = new TriagePolicy(4, 60);
        List<Incident> rows = new ArrayList<>();
        for (long i = 0; i < 64; i++) {
            rows.add(incident(i));
        }
        List<Triage> results = route.mapBatches(TRIAGE, rows, 8, policy);
        assertEquals(rows.size(), results.size());
        // An item codec that throws on the second batch: the failure names that batch, keeps the first, and has the codec's exception as its cause.
        int[] decoded = {0};
        Codec<Triage> failing = new Codec<>() {
            @Override
            public org.jacquardlang.host.protocol.Value encode(Encoder encoder, Triage value) throws JacquardException {
                return TRIAGE.encode(encoder, value);
            }

            @Override
            public Triage decode(Decoder decoder, org.jacquardlang.host.protocol.Value value) throws JacquardException {
                if (++decoded[0] > 8) {
                    throw new IllegalStateException("second batch");
                }
                return TRIAGE.decode(decoder, value);
            }
        };
        JacquardException batch = assertThrows(JacquardException.class, () -> route.mapBatches(failing, rows.subList(0, 16), 8, policy));
        assertEquals(1, batch.batchIndex());
        assertEquals(8, batch.completed().size());
        assertTrue(batch.getCause() instanceof IllegalStateException, "the codec's exception is not the cause: " + batch.getCause());
        for (int i = 0; i < rows.size(); i++) {
            Incident row = rows.get(i);
            boolean page = row.visible() && row.severity() >= 4;
            List<String> reasons = new ArrayList<>();
            if (row.owner().isEmpty()) {
                reasons.add("unowned");
            }
            if (row.age() >= 60) {
                reasons.add("ageing");
            }
            if (page) {
                reasons.add("customer-impact");
            }
            Urgency urgency = page ? Urgency.PAGE_NOW : row.age() >= 60 || row.severity() == 5 ? Urgency.WORK_TODAY : Urgency.WATCH_ONLY;
            assertEquals(new Routed(row.id(), row.owner().orElse("api-oncall"), urgency, reasons), results.get(i), "row " + row.id());
        }
        assertTrue(route.mapBatches(TRIAGE, List.of(), 8, policy).isEmpty());
    }

    @Test
    void invalidInputIsRefusedBeforeAnyWorker() throws Exception {
        Program program = prepare(example("triage"));
        // A spawn would now fail as worker_spawn_failed, not as a local refusal.
        program.setWorkerCommand(List.of("/no/such/jacquard"));
        Function route = program.function("triage.batch");
        TriagePolicy policy = new TriagePolicy(4, 60);
        List<Incident> rows = new ArrayList<>();
        for (long i = 0; i < 17; i++) {
            rows.add(incident(i));
        }
        Incident late = rows.get(16);
        rows.set(16, new Incident(late.id(), late.service(), Json.INT_MAX + 1, late.age(), late.visible(), late.owner()));
        JacquardException batch = assertThrows(JacquardException.class, () -> route.mapBatches(TRIAGE, rows, 8, policy));
        assertEquals(-1, batch.batchIndex());
        assertTrue(batch.completed().isEmpty());
        assertEquals(JacquardException.Kind.INVALID_ARGUMENT, batch.kind());
        assertTrue(batch.getMessage().contains("argument[1][0].severity"), batch.getMessage());
        List<Incident> many = Collections.nCopies(64, incident(1));
        assertEquals(JacquardException.Kind.INVALID_ARGUMENT,
                assertThrows(JacquardException.class, () -> route.call(Codec.list(TRIAGE), policy, many)).kind(), "over-deep single call");
        JacquardException notList = assertThrows(JacquardException.class, () -> route.call(Codec.list(TRIAGE), policy, "not a list"));
        assertEquals(JacquardException.Kind.INVALID_ARGUMENT, notList.kind());
        assertTrue(notList.getMessage().contains("argument[1]"), notList.getMessage());
        JacquardException count = assertThrows(JacquardException.class, () -> route.call(Codec.list(TRIAGE), policy));
        assertTrue(count.getMessage().contains("expected 2 arguments"), count.getMessage());
    }

    @Test
    void missingHandlersRefuseBindingAndPureCallsNeedNoServices() throws Exception {
        Program fulfillment = prepare(example("fulfillment"));
        AtomicInteger calls = new AtomicInteger();
        Operations inventory = new Operations().on("booking-status", Codec.DYNAMIC, Codec.DYNAMIC, order -> {
            calls.incrementAndGet();
            return Dynamic.UNIT;
        });
        JacquardException missing = assertThrows(JacquardException.class,
                () -> fulfillment.function("fulfillment.place", Map.of("Inventory", inventory)));
        assertEquals(JacquardException.Kind.BINDING, missing.kind());
        assertTrue(missing.getMessage().contains("inventory.lookup-stock") && missing.getMessage().contains("dispatch.publish-booking"),
                missing.getMessage());
        assertEquals(0, calls.get(), "a handler ran during binding");
        Operations typo = new Operations().on("lookup_stok", Codec.TEXT, Codec.UNIT, sku -> null);
        JacquardException unknown = assertThrows(JacquardException.class, () -> fulfillment.function("fulfillment.place", Map.of("Inventory", typo)));
        assertTrue(unknown.getMessage().contains("inventory has no operation lookup-stok"), unknown.getMessage());
        fulfillment.provide("Inventory", inventory);
        Function valid = fulfillment.function("order.valid-lines");
        assertTrue(valid.call(Codec.BOOL, Codec.list(Codec.DYNAMIC).arg(List.of()), List.of()));
        assertEquals(JacquardException.Kind.BINDING, assertThrows(JacquardException.class, () -> fulfillment.provide("Unknown", new Operations())).kind());
        assertEquals(JacquardException.Kind.BINDING, assertThrows(JacquardException.class, () -> fulfillment.function("no-such-function")).kind());
    }

    private static Operations warehouse(AtomicInteger reservations) {
        return new Operations()
                .on("booking-status", Codec.DYNAMIC, Codec.DYNAMIC, order -> Dynamic.variant("BookingPending"))
                .on("lookup_stock", Codec.TEXT, Codec.DYNAMIC, sku -> Dynamic.variant("Stock", Dynamic.of(20)))
                .on("reserve-order", Codec.DYNAMIC, Codec.DYNAMIC, (HandlerContext context, Dynamic order) -> {
                    assertFalse(context.isCancelled());
                    reservations.incrementAndGet();
                    return Dynamic.variant("Booked", Dynamic.of("receipt-1"));
                })
                .on("publish-booking", Codec.TEXT, Codec.TEXT, Codec.UNIT, (id, receipt) -> {
                    throw new java.io.IOException("synthetic after commit");
                });
    }

    private static Dynamic order(Data data) throws JacquardException {
        Dynamic line = data.make("Line", Dynamic.of("sensor"), Dynamic.of(2));
        return data.make("Order", Dynamic.of("one"), Dynamic.list(line));
    }

    @Test
    void unknownCompletionHappensOnceWithRawEvidence() throws Exception {
        Program fulfillment = prepare(example("fulfillment"));
        AtomicInteger reservations = new AtomicInteger();
        Operations service = warehouse(reservations);
        Function place = fulfillment.function("fulfillment.place", Map.of("Inventory", service, "Dispatch", service));
        Dynamic order = order(fulfillment.data());
        JacquardException error = assertThrows(JacquardException.class, () -> place.call(Codec.DYNAMIC, order));
        assertEquals(Failure.OPERATION_FAILED, error.failure(), error.getMessage());
        InvocationResult result = error.result();
        assertEquals("E1612", result.code());
        assertTrue(result.hostFrames().stream().anyMatch(frame -> Json.of("effect_ok").equals(frame.get("kind"))));
        assertEquals(1, reservations.get());
        String rendered = place.invoke(order).render();
        assertTrue(rendered.startsWith("fulfillment.place: E1612") && rendered.contains(". publish-booking"), rendered);
    }

    /**
     * In a real invocation, a cancellation that lands after a request's
     * arguments are decoded (through the test seam) keeps the handler from
     * starting, and the invocation reports the abort.
     */
    @Test
    void noHandlerStartsOnceTheInvocationStoppedDuringDecoding() throws Exception {
        Program fulfillment = prepare(example("fulfillment"));
        AtomicInteger started = new AtomicInteger();
        Operations service = new Operations()
                .on("booking-status", Codec.DYNAMIC, Codec.DYNAMIC, order -> {
                    started.incrementAndGet();
                    return Dynamic.variant("BookingPending");
                })
                .on("lookup-stock", Codec.TEXT, Codec.DYNAMIC, sku -> Dynamic.variant("Stock", Dynamic.of(20)))
                .on("reserve-order", Codec.DYNAMIC, Codec.DYNAMIC, order -> Dynamic.variant("Booked", Dynamic.of("r")))
                .on("publish-booking", Codec.TEXT, Codec.TEXT, Codec.UNIT, (id, receipt) -> null);
        Function place = fulfillment.function("fulfillment.place", Map.of("Inventory", service, "Dispatch", service));
        Cancel cancel = new Cancel();
        Operations.afterDecodeHook = cancel::cancel;
        Invocation invocation = place.invoke(cancel, order(fulfillment.data()));
        Operations.afterDecodeHook = null;
        assertEquals(0, started.get(), "the handler started after the invocation stopped");
        assertEquals(Json.of("aborted"), invocation.raw().hostEvidence().get("reason"), invocation.toString());
        assertEquals(1, invocation.raw().requests().size());
    }

    record Job(String id, long effort, long value, long latest) {}

    record Assignment(String id, long slot) {}

    record Plan(long score, List<Assignment> assignments) {}

    sealed interface Planning {}

    record Planned(Plan plan) implements Planning {}

    record InvalidPlan(String reason) implements Planning {}

    static long optimum(List<Job> jobs, long[] caps) {
        if (jobs.isEmpty()) {
            return 0;
        }
        Job job = jobs.get(0);
        List<Job> rest = jobs.subList(1, jobs.size());
        long best = optimum(rest, caps);
        for (int slot = 0; slot < Math.min(job.latest(), caps.length); slot++) {
            if (caps[slot] >= job.effort()) {
                long[] next = caps.clone();
                next[slot] -= job.effort();
                best = Math.max(best, job.value() + optimum(rest, next));
            }
        }
        return best;
    }

    @Test
    void plannerMatchesAnIndependentExhaustiveOracle() throws Exception {
        Function solve = prepare(example("planner")).function("planner.plan");
        Codec<Planning> planning = Codec.of(Planning.class);
        for (long seed = 0; seed < 25; seed++) {
            List<Job> jobs = new ArrayList<>();
            for (long i = 0; i < seed % 6 + 1; i++) {
                jobs.add(new Job("job-" + i, (seed + i * 3) % 4 + 1, (seed * 7 + i * 11) % 15, (seed + i) % 3 + 1));
            }
            long[] all = {seed % 5 + 1, seed % 3 + 2, 3};
            long[] caps = java.util.Arrays.copyOf(all, (int) (seed % 3 + 1));
            List<Long> capList = new ArrayList<>();
            for (long cap : caps) {
                capList.add(cap);
            }
            if (!(solve.call(planning, jobs, capList) instanceof Planned(Plan plan))) {
                fail("seed " + seed + ": not planned");
                return;
            }
            assertEquals(optimum(jobs, caps), plan.score(), "seed " + seed);
            long[] loads = new long[caps.length];
            Set<String> seen = new HashSet<>();
            long score = 0;
            for (Assignment assignment : plan.assignments()) {
                Job job = jobs.stream().filter(candidate -> candidate.id().equals(assignment.id())).findFirst().orElseThrow();
                assertTrue(seen.add(assignment.id()) && assignment.slot() >= 1 && assignment.slot() <= Math.min(job.latest(), caps.length),
                        assignment.toString());
                loads[(int) assignment.slot() - 1] += job.effort();
                score += job.value();
            }
            for (int slot = 0; slot < caps.length; slot++) {
                assertTrue(loads[slot] <= caps[slot], "seed " + seed + ": overloaded");
            }
            assertEquals(plan.score(), score, "seed " + seed);
        }
    }

    @Test
    void cacheReuseInvalidationAndCorruption() throws Exception {
        Program quick = prepare(example("quickstart"));
        int commands = runtime.coreCommands();
        Program repeated = prepare(example("quickstart"));
        assertEquals(commands, runtime.coreCommands(), "a warm preparation ran Core");
        assertEquals(quick.directory(), repeated.directory());
        String original = Files.readString(example("quickstart"), StandardCharsets.UTF_8);
        Path changed = source("changed.jac", original.replace("\n", "\r\n") + "\r\n-- changed\r\n");
        Program program = prepare(changed);
        String snapshot = Files.readString(program.directory().resolve("source.jac"), StandardCharsets.UTF_8);
        assertTrue(!program.directory().equals(quick.directory()) && snapshot.contains("\r\n"), "not a byte-exact new entry");
        Files.writeString(program.directory().resolve("store").resolve("unexpected"), "corrupt");
        JacquardException corrupt = assertThrows(JacquardException.class, () -> prepare(changed), "a corrupt cache was accepted");
        assertEquals(JacquardException.Kind.PREPARATION, corrupt.kind());
        assertEquals(JacquardException.Kind.PREPARATION,
                assertThrows(JacquardException.class, () -> prepare(source("rules.txt", ""))).kind());
    }

    /**
     * Core accepts a parameter nesting 2,500 {@code Option}s, which overflowed
     * the signature parser's stack on both the cold and warm paths. That
     * function is now unavailable, refused with a binding failure that names
     * the depth limit, while {@code safe} works on both paths.
     */
    @Test
    void aSignatureTooDeepToBindLeavesTheOtherFunctionsWorking() throws Exception {
        int depth = 2500;
        Path source = source("deep.jac", "deep : (" + "Option (".repeat(depth) + "Int" + ")".repeat(depth) + ") ->{} Int\ndeep(x) = 0\n"
                + "safe : () ->{} Int\nsafe() = 1\n");
        int afterCold = -1;
        for (String path : List.of("cold", "warm")) {
            Program program = prepare(source);
            if (afterCold < 0) {
                afterCold = runtime.coreCommands();
            } else {
                assertEquals(afterCold, runtime.coreCommands(), "the warm path ran Core");
            }
            assertEquals(1L, program.function("safe").call(Codec.INT), path);
            JacquardException refused = assertThrows(JacquardException.class, () -> program.function("deep"), path);
            assertEquals(JacquardException.Kind.BINDING, refused.kind(), path);
            assertTrue(refused.getMessage().contains("deeper than 64 levels, the protocol's value-depth limit"), refused.getMessage());
            JacquardException bound = assertThrows(JacquardException.class, () -> program.function("deep", Map.of("Unused", new Operations())), path);
            assertEquals(JacquardException.Kind.BINDING, bound.kind(), path);
            assertTrue(bound.getMessage().contains("deeper than 64 levels"), bound.getMessage());
        }
    }

    /** Pointing {@code first} at {@code second}'s identity in program.json would make it return 2. */
    @Test
    void aRedirectedNameInProgramJsonIsRefused() throws Exception {
        Path source = source("pair.jac", "first : () ->{} Int\nfirst() = 1\n\nsecond : () ->{} Int\nsecond() = 2\n");
        Program program = prepare(source);
        assertEquals(1L, program.function("first").call(Codec.INT));
        Path metadata = program.directory().resolve("program.json");
        String original = Files.readString(metadata);
        String from = "[\"first\",\"" + program.data().identity("first").hex() + "\"]";
        assertTrue(original.contains(from));
        Files.writeString(metadata, original.replace(from, "[\"first\",\"" + program.data().identity("second").hex() + "\"]"));
        JacquardException error = assertThrows(JacquardException.class, () -> prepare(source).function("first").call(Codec.INT));
        assertEquals(JacquardException.Kind.PREPARATION, error.kind(), error.getMessage());
        assertTrue(error.getMessage().contains("program.json bindings differ"), error.getMessage());
        Files.writeString(metadata, original);
        assertEquals(1L, prepare(source).function("first").call(Codec.INT));
    }

    @Test
    void fullIntRangeRealsAndNestedOptions() throws Exception {
        Program program = prepare(source("values.jac", "value.int : (Int) ->{} Int\nvalue.int(x) = x\n"
                + "value.real : (Real) ->{} Real\nvalue.real(x) = x\n"
                + "value.option : (Option (Option Int)) ->{} Option (Option Int)\nvalue.option(x) = x\n"));
        Function integer = program.function("value.int");
        for (long value : new long[] {Json.INT_MIN, Json.INT_MAX}) {
            assertEquals(value, integer.call(Codec.INT, value));
        }
        assertEquals(JacquardException.Kind.INVALID_ARGUMENT, assertThrows(JacquardException.class, () -> integer.call(Codec.INT, Json.INT_MAX + 1)).kind());
        assertEquals(JacquardException.Kind.INVALID_ARGUMENT, assertThrows(JacquardException.class, () -> integer.call(Codec.INT, 1.5)).kind());
        JacquardException narrow = assertThrows(JacquardException.class, () -> integer.call(Codec.INT32, 1L << 40));
        assertEquals(JacquardException.Kind.BINDING, narrow.kind());
        assertNotNull(narrow.result());
        Function real = program.function("value.real");
        for (double value : new double[] {1.0, -0.0, Double.POSITIVE_INFINITY, Double.NaN}) {
            assertEquals(Double.doubleToRawLongBits(value), Double.doubleToRawLongBits(real.call(Codec.REAL, value)));
        }
        Function option = program.function("value.option");
        Codec<Optional<Optional<Long>>> nested = Codec.optional(Codec.optional(Codec.INT));
        for (Optional<Optional<Long>> value : List.of(Optional.<Optional<Long>>empty(), Optional.of(Optional.<Long>empty()), Optional.of(Optional.of(3L)))) {
            assertEquals(value, option.call(nested, nested.arg(value)));
        }
        Dynamic inner = Dynamic.some(Dynamic.none());
        assertEquals(inner, option.call(Codec.DYNAMIC, inner));
    }

    @Test
    void spawnFailuresAndCancellation() throws Exception {
        Program quick = prepare(example("quickstart"));
        Function shipping = quick.function("shipping-cost");
        quick.setWorkerCommand(List.of("/no/such/jacquard"));
        JacquardException spawn = assertThrows(JacquardException.class, () -> quick.function("shipping-cost").call(Codec.INT, 10));
        assertEquals(Failure.WORKER_LOST, spawn.failure(), spawn.getMessage());
        assertEquals(Json.of("worker_spawn_failed"), spawn.result().hostEvidence().get("reason"));
        Cancel cancel = new Cancel();
        cancel.cancel();
        Invocation invocation = shipping.invoke(cancel, 10);
        assertEquals(Json.of("aborted"), invocation.raw().hostEvidence().get("reason"));
        assertNotNull(invocation.error());
    }

    @Test
    void outputOverflowFailsOnceWithoutAdaptiveBatching() throws Exception {
        Function route = prepare(example("triage")).function("triage.batch");
        List<Incident> rows = new ArrayList<>();
        for (int i = 0; i < 29; i++) {
            rows.add(new Incident(Integer.toString(i), "api", 5, 90, true, Optional.empty()));
        }
        JacquardException error = assertThrows(JacquardException.class, () -> route.mapBatches(TRIAGE, rows, 29, new TriagePolicy(4, 60)));
        assertEquals(0, error.batchIndex(), error.getMessage());
        assertTrue(error.completed().isEmpty());
        assertEquals("E1602", error.result().code());
    }

    /** Batch 0 decodes to {@code [null]} (unit); batch 1's result exceeds the limits (E1602). */
    @Test
    void aLaterBatchFailureKeepsEarlierUnitResults() throws Exception {
        String many = String.join(",", Collections.nCopies(40, "()"));
        Function units = prepare(source("units.jac", "units : (List Int) ->{} List ()\n"
                + "units(xs) = match xs { | Cons(n, _) -> if eq(n, 1) then [()] else [" + many + "] | Nil -> [] }\n")).function("units");
        assertEquals(java.util.Arrays.asList((Object) null), units.mapBatches(Codec.UNIT, List.of(1L), 1));
        JacquardException error = assertThrows(JacquardException.class, () -> units.mapBatches(Codec.UNIT, List.of(1L, 2L), 1));
        assertEquals(1, error.batchIndex(), error.getMessage());
        assertEquals(java.util.Arrays.asList((Object) null), error.completed());
        assertEquals(JacquardException.Kind.INVOCATION, error.kind());
        assertEquals("E1602", error.result().code());
    }

    /** A result codec that throws with a huge message gives a short binding failure that keeps the raw result and the exception. */
    @Test
    void aThrowingResultCodecKeepsTheResultAndQuotesAnExcerpt() throws Exception {
        Function shipping = prepare(example("quickstart")).function("shipping-cost");
        Invocation invocation = shipping.invoke(35);
        assertTrue(invocation.ok());
        Codec<Long> throwing = new Codec<>() {
            @Override
            public org.jacquardlang.host.protocol.Value encode(Encoder encoder, Long value) throws JacquardException {
                return Codec.INT.encode(encoder, value);
            }

            @Override
            public Long decode(Decoder decoder, org.jacquardlang.host.protocol.Value value) {
                throw new IllegalStateException("X".repeat(1_000_000));
            }
        };
        JacquardException error = assertThrows(JacquardException.class, () -> invocation.decode(throwing));
        assertEquals(JacquardException.Kind.BINDING, error.kind());
        assertTrue(error.getMessage().length() < 400, error.getMessage().length() + " chars");
        assertTrue(error.getMessage().endsWith("... (1000000 chars)"), error.getMessage().substring(0, 120));
        assertTrue(error.result().ok(), "the raw result was not kept");
        assertTrue(error.getCause() instanceof IllegalStateException, "the codec's exception is not the cause: " + error.getCause());
        JacquardException called = assertThrows(JacquardException.class, () -> shipping.call(throwing, 35));
        assertTrue(called.getCause() instanceof IllegalStateException, "call: the codec's exception is not the cause: " + called.getCause());
    }

    @Test
    void resultMappingFailureRetainsTheSuccessfulResult() throws Exception {
        Invocation invocation = prepare(example("quickstart")).function("shipping-cost").invoke(35);
        assertTrue(invocation.ok());
        assertNull(invocation.error());
        JacquardException error = assertThrows(JacquardException.class, () -> invocation.decode(Codec.TEXT));
        assertEquals(JacquardException.Kind.BINDING, error.kind());
        assertTrue(error.result().ok(), error.getMessage());
    }

    private static void copyTree(Path from, Path to) throws Exception {
        Files.createDirectories(to);
        try (Stream<Path> entries = Files.list(from)) {
            for (Path entry : entries.toList()) {
                Path target = to.resolve(entry.getFileName().toString());
                if (Files.isDirectory(entry)) {
                    copyTree(entry, target);
                } else {
                    Files.copy(entry, target);
                }
            }
        }
    }

    @Test
    void preludeContentsKeyTheCacheAndConcurrentPreparationPublishesOnce() throws Exception {
        Program quick = prepare(example("quickstart"));
        Path copy = root.resolve("prelude");
        copyTree(Path.of(System.getenv("JACQUARD_PRELUDE")), copy);
        Runtime copied = new Runtime(RuntimeOptions.defaults().withPrelude(copy));
        Program before = copied.prepare(List.of(example("quickstart")), options());
        assertEquals(quick.directory(), before.directory(), "an identical prelude copy should share the entry");
        Path license = copy.resolve("LICENSE.md");
        Files.writeString(license, Files.readString(license) + "\n");
        Program after = copied.prepare(List.of(example("quickstart")), options());
        assertNotEquals(before.directory(), after.directory(), "a prelude change did not change the cache key");
        Path source = source("concurrent.jac", "shipping-cost : (Int) ->{} Int\nshipping-cost(total) = if lt(total, 50) then 5 else 0\n-- concurrent\n");
        List<CompletableFuture<Program>> workers = new ArrayList<>();
        for (int i = 0; i < 2; i++) {
            workers.add(CompletableFuture.supplyAsync(() -> {
                try {
                    return runtime.prepare(List.of(source), options());
                } catch (JacquardException error) {
                    throw new IllegalStateException(error);
                }
            }));
        }
        Program first = workers.get(0).get(120, TimeUnit.SECONDS);
        Program second = workers.get(1).get(120, TimeUnit.SECONDS);
        assertEquals(first.directory(), second.directory());
        assertEquals(5L, second.function("shipping-cost").call(Codec.INT, 35));
        try (Stream<Path> entries = Files.list(root.resolve("cache"))) {
            for (Path entry : entries.toList()) {
                assertFalse(entry.getFileName().toString().startsWith("prepare-"), "staging directory left behind: " + entry);
            }
        }
    }
}
