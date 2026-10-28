package org.jacquardlang.host.sdk;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.nio.file.Path;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import java.util.concurrent.atomic.AtomicInteger;

import org.jacquardlang.host.Host;
import org.jacquardlang.host.HostOptions;
import org.jacquardlang.host.OperationRegistry;
import org.jacquardlang.host.carrier.Cancel;
import org.jacquardlang.host.carrier.Options;
import org.jacquardlang.host.json.Json;
import org.jacquardlang.host.protocol.BoundaryType;
import org.jacquardlang.host.protocol.Hash;
import org.jacquardlang.host.protocol.Interface;
import org.jacquardlang.host.protocol.Limits;
import org.jacquardlang.host.protocol.Value;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

/**
 * {@link Function#mapBatches} preflight with synthetic identities and no
 * Core: a stop ends validation between batches and after the last, there is
 * no validation deadline, and no batch's encoding is kept once it is
 * checked. Rows go through a codec that counts its encodings, so each bound
 * is an exact count. The worker command does not exist, so a call that gets
 * past validation fails at batch 0.
 */
class BatchesTest {
    @TempDir
    Path scratch;

    @AfterEach
    void clearInterrupt() {
        Thread.interrupted();
        Function.conformHook = null;
    }

    /** {@code ints : (List Int) ->{} List Int} over the {@link DataTest} fixture. */
    private Function ints() {
        Data data = DataTest.fixture(Limits.HARD);
        BoundaryType list = DataTest.nominal(data.name("list"), DataTest.nominal(data.name("int")));
        Interface signature = new Interface(List.of(list), list, List.of());
        Host host = new Host(scratch.resolve("store").toString(),
                new HostOptions(new Options(List.of(scratch.resolve("no-such-jacquard").toString()), null, null), Limits.HARD));
        return new Function("ints", signature, DataTest.numbered(99), new OperationRegistry(), host, data);
    }

    /** Encodes Ints, counting each encoding and running {@code each} with the count first. */
    private static final class Counting implements Codec<Long> {
        final AtomicInteger encodes = new AtomicInteger();
        final java.util.function.IntFunction<Runnable> each;

        Counting(java.util.function.IntFunction<Runnable> each) {
            this.each = each;
        }

        @Override
        public Value encode(Encoder encoder, Long value) throws JacquardException {
            Runnable action = each.apply(encodes.incrementAndGet());
            if (action != null) {
                action.run();
            }
            return Codec.INT.encode(encoder, value);
        }

        @Override
        public Long decode(Decoder decoder, Value value) throws JacquardException {
            return Codec.INT.decode(decoder, value);
        }
    }

    private static List<Argument<Long>> rows(Counting codec, int count) {
        return Collections.nCopies(count, codec.arg(1L));
    }

    /**
     * The argument count is checked from the count alone: a million
     * arguments to a one-parameter function, or a million leading arguments
     * to a batch, are refused before one is copied or encoded.
     */
    @Test
    void theWrongArgumentCountIsRefusedBeforeAnyArgumentIsEncoded() throws Exception {
        Counting codec = new Counting(n -> null);
        Object[] many = Collections.nCopies(1_000_000, codec.arg(1L)).toArray();
        JacquardException call = assertThrows(JacquardException.class, () -> ints().invoke(many));
        assertEquals(JacquardException.Kind.INVALID_ARGUMENT, call.kind());
        assertEquals("ints: expected 1 arguments, received 1000000", call.getMessage());
        JacquardException batches = assertThrows(JacquardException.class, () -> ints().mapBatches(Codec.INT, rows(codec, 10), 5, many));
        assertEquals("ints: expected 1 arguments, received 1000001", batches.getMessage());
        assertEquals(0, codec.encodes.get(), "arguments were encoded before the count was checked");
    }

    @Test
    void anAlreadyCancelledCallEncodesNoBatch() throws Exception {
        Counting codec = new Counting(n -> null);
        Cancel cancel = new Cancel();
        cancel.cancel();
        JacquardException error = assertThrows(JacquardException.class, () -> ints().mapBatches(cancel, Codec.INT, rows(codec, 10_000), 10));
        assertEquals(0, codec.encodes.get(), "rows were encoded after the stop");
        assertEquals(-1, error.batchIndex());
        assertEquals(JacquardException.Kind.INVOCATION, error.kind(), error.getMessage());
        assertEquals(Json.of("aborted"), error.result().hostEvidence().get("reason"));
        assertTrue(error.completed().isEmpty());
    }

    @Test
    void anInterruptedCallEncodesNoBatchAndKeepsTheInterrupt() throws Exception {
        Counting codec = new Counting(n -> null);
        Thread.currentThread().interrupt();
        JacquardException error = assertThrows(JacquardException.class,
                () -> ints().mapBatches(new Cancel(), Codec.INT, rows(codec, 10_000), 10));
        assertTrue(Thread.interrupted(), "the interrupt status was cleared");
        assertEquals(0, codec.encodes.get(), "rows were encoded after the interrupt");
        assertEquals(-1, error.batchIndex());
        assertEquals(Json.of("aborted"), error.result().hostEvidence().get("reason"));
    }

    @Test
    void aCancelDuringValidationStopsAtTheNextBatch() throws Exception {
        Cancel cancel = new Cancel();
        // Cancelled while batch 2 (rows 21 to 30) is being encoded.
        Counting codec = new Counting(n -> n == 25 ? cancel::cancel : null);
        JacquardException error = assertThrows(JacquardException.class, () -> ints().mapBatches(cancel, Codec.INT, rows(codec, 10_000), 10));
        assertEquals(30, codec.encodes.get());
        assertEquals(-1, error.batchIndex());
        assertEquals(Json.of("aborted"), error.result().hostEvidence().get("reason"));
    }

    /**
     * A cancel while the last batch is validated is observed before any
     * worker starts: the refusal is the host's aborted result at batch index
     * -1, and no batch is encoded again to run.
     */
    @Test
    void aStopDuringTheLastBatchsValidationStartsNoWorker() throws Exception {
        Cancel cancel = new Cancel();
        Counting codec = new Counting(n -> n == 30 ? cancel::cancel : null);
        JacquardException error = assertThrows(JacquardException.class, () -> ints().mapBatches(cancel, Codec.INT, rows(codec, 30), 10));
        assertEquals(30, codec.encodes.get(), "a batch was encoded again after the stop");
        assertEquals(-1, error.batchIndex());
        assertEquals(Json.of("aborted"), error.result().hostEvidence().get("reason"));
    }

    /**
     * Validation has no deadline of its own (as in the other SDKs), so it
     * does not depend on the machine's speed: every batch is validated, and
     * the call goes on to the worker (here a missing executable, reported at
     * batch 0), encoding batch 0 again to run it.
     */
    @Test
    void everyBatchIsValidatedAndTheFirstEncodedAgainToRun() throws Exception {
        Counting codec = new Counting(n -> null);
        JacquardException error = assertThrows(JacquardException.class, () -> ints().mapBatches(new Cancel(), Codec.INT, rows(codec, 30), 10));
        assertEquals(0, error.batchIndex(), error.getMessage());
        assertEquals(40, codec.encodes.get());
    }

    /**
     * Retention is structurally impossible: the validation step returns
     * nothing, so no caller can keep a batch's encoding, and no field of a
     * function can hold one.
     */
    @Test
    void validationKeepsNoEncoding() throws Exception {
        java.lang.reflect.Method validate = Function.class.getDeclaredMethod("validate", Object[].class, List.class);
        assertEquals(void.class, validate.getReturnType());
        for (java.lang.reflect.Field field : Function.class.getDeclaredFields()) {
            assertTrue(!java.util.Collection.class.isAssignableFrom(field.getType()) && !Value.class.isAssignableFrom(field.getType())
                    && !field.getType().isArray(), "a field could hold encodings: " + field);
        }
    }

    /** A codec that ignores its input and returns {@code shared} in constant time. */
    private static Codec<Long> constant(Value shared) {
        return new Codec<>() {
            @Override
            public Value encode(Encoder encoder, Long ignored) {
                return shared;
            }

            @Override
            public Long decode(Decoder decoder, Value ignored) {
                return 0L;
            }
        };
    }

    /** Invokes a one-parameter function of {@code type} with {@code value}, failing the call if the type check is reached. */
    private JacquardException refusedBeforeTheTypeCheck(Data data, BoundaryType type, Value value) {
        Host host = new Host(scratch.resolve("store").toString(),
                new HostOptions(new Options(List.of(scratch.resolve("no-such-jacquard").toString()), null, null), Limits.HARD));
        Function function = new Function("deep", new Interface(List.of(type), type, List.of()), DataTest.numbered(98),
                new OperationRegistry(), host, data);
        boolean[] conformed = {false};
        Function.conformHook = () -> {
            conformed[0] = true;
            throw new IllegalStateException("the type check ran");
        };
        JacquardException error = assertThrows(JacquardException.class, () -> function.invoke(constant(value).arg(0L)));
        assertTrue(!conformed[0], "the type check ran before the value was measured");
        assertEquals(JacquardException.Kind.INVALID_ARGUMENT, error.kind(), error.getMessage());
        return error;
    }

    /**
     * A constant-time custom codec returns a small shared graph standing for
     * 2^28 leaves, and it is measured before its type is checked, under the
     * shared budgets, so the type check never runs (a test seam fails the call
     * if it is reached; on the old order the check walked the whole graph).
     * As 28 levels of a two-item tuple (wire depth 59, within 64) it is
     * refused by the node budget; as the reviewer's 28 levels of a two-item
     * list it is refused by the JSON depth bound first.
     */
    @Test
    void aSharedGraphFromACustomCodecIsMeasuredBeforeItsTypeIsChecked() throws Exception {
        Data data = DataTest.fixture(Limits.HARD);
        BoundaryType tupleType = DataTest.nominal(data.name("int"));
        Value tuple = new Value.Int(1);
        for (int i = 0; i < 28; i++) {
            tupleType = new BoundaryType.Tuple(List.of(tupleType, tupleType));
            tuple = new Value.Tuple(List.of(tuple, tuple));
        }
        assertTrue(refusedBeforeTheTypeCheck(data, tupleType, tuple).getMessage().endsWith("value exceeds max_value_nodes; use smaller independent batches"));

        Hash list = data.name("list");
        Hash cons = data.constructorId("cons");
        Value nil = new Value.Constructor(data.constructorId("nil"), List.of());
        BoundaryType listType = DataTest.nominal(data.name("int"));
        Value lists = new Value.Int(1);
        for (int i = 0; i < 28; i++) {
            listType = DataTest.nominal(list, listType);
            lists = new Value.Constructor(cons, List.of(lists, new Value.Constructor(cons, List.of(lists, nil))));
        }
        assertTrue(refusedBeforeTheTypeCheck(data, listType, lists).getMessage().endsWith("value exceeds the v0 JSON depth; use smaller independent batches"));
    }

    @Test
    void completedUnitResultsKeepTheirNulls() {
        JacquardException error = JacquardException.batch(1, Arrays.asList((Object) null, null), JacquardException.invalid("later"));
        assertEquals(1, error.batchIndex());
        assertEquals(Arrays.asList((Object) null, null), error.completed());
        assertNull(error.completed().get(0));
        assertThrows(UnsupportedOperationException.class, () -> error.completed().add(1L));
    }
}
