package org.jacquardlang.host.sdk;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.lang.ref.WeakReference;
import java.nio.file.Path;
import java.time.Duration;
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
import org.jacquardlang.host.protocol.Interface;
import org.jacquardlang.host.protocol.Limits;
import org.jacquardlang.host.protocol.Value;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

/**
 * {@link Function#mapBatches} preflight with synthetic identities and no
 * Core: a stop or the deadline ends validation between batches, and each
 * batch's encoding is released once it is checked. Rows go through a codec
 * that counts its encodings, so each bound is an exact count. The worker
 * command does not exist; no test here reaches a worker.
 */
class BatchesTest {
    @TempDir
    Path scratch;

    @AfterEach
    void clearInterrupt() {
        Thread.interrupted();
    }

    /** {@code ints : (List Int) ->{} List Int} over the {@link DataTest} fixture. */
    private Function ints(Duration deadline) {
        Data data = DataTest.fixture(Limits.HARD);
        BoundaryType list = DataTest.nominal(data.name("list"), DataTest.nominal(data.name("int")));
        Interface signature = new Interface(List.of(list), list, List.of());
        Host host = new Host(scratch.resolve("store").toString(),
                new HostOptions(new Options(List.of(scratch.resolve("no-such-jacquard").toString()), null, null), Limits.HARD));
        return new Function("ints", signature, DataTest.numbered(99), new OperationRegistry(), host, data, deadline);
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

    @Test
    void anAlreadyCancelledCallEncodesNoBatch() throws Exception {
        Counting codec = new Counting(n -> null);
        Cancel cancel = new Cancel();
        cancel.cancel();
        JacquardException error = assertThrows(JacquardException.class, () -> ints(null).mapBatches(cancel, Codec.INT, rows(codec, 10_000), 10));
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
                () -> ints(null).mapBatches(new Cancel(), Codec.INT, rows(codec, 10_000), 10));
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
        JacquardException error = assertThrows(JacquardException.class, () -> ints(null).mapBatches(cancel, Codec.INT, rows(codec, 10_000), 10));
        assertEquals(30, codec.encodes.get());
        assertEquals(-1, error.batchIndex());
        assertEquals(Json.of("aborted"), error.result().hostEvidence().get("reason"));
    }

    @Test
    void validationOutlivingTheDeadlineIsRefused() throws Exception {
        Counting codec = new Counting(n -> n == 15 ? () -> {
            try {
                Thread.sleep(200);
            } catch (InterruptedException interrupted) {
                Thread.currentThread().interrupt();
            }
        } : null);
        JacquardException error = assertThrows(JacquardException.class,
                () -> ints(Duration.ofMillis(50)).mapBatches(new Cancel(), Codec.INT, rows(codec, 10_000), 10));
        assertEquals(20, codec.encodes.get());
        assertEquals(JacquardException.Kind.INVALID_ARGUMENT, error.kind());
        assertEquals(-1, error.batchIndex());
        assertTrue(error.getMessage().contains("outlived the deadline"), error.getMessage());
    }

    /**
     * While batch 2 is encoded, nothing holds batch 0's encoding: a weak
     * reference to its first value is cleared by a collection. Keeping every
     * batch's encoding until the first runs would hold it.
     */
    @Test
    void eachBatchEncodingIsReleasedOnceChecked() throws Exception {
        Cancel cancel = new Cancel();
        Object[] first = new Object[1];
        boolean[] released = new boolean[1];
        Codec<Long> codec = new Codec<>() {
            int encodes;

            @Override
            public Value encode(Encoder encoder, Long value) throws JacquardException {
                encodes++;
                if (encodes == 21) {
                    @SuppressWarnings("unchecked")
                    WeakReference<Value> reference = (WeakReference<Value>) first[0];
                    for (int i = 0; i < 20 && reference.get() != null; i++) {
                        System.gc();
                    }
                    released[0] = reference.get() == null;
                    cancel.cancel();
                }
                Value encoded = Codec.INT.encode(encoder, value);
                if (encodes == 1) {
                    first[0] = new WeakReference<>(encoded);
                }
                return encoded;
            }

            @Override
            public Long decode(Decoder decoder, Value value) throws JacquardException {
                return Codec.INT.decode(decoder, value);
            }
        };
        assertThrows(JacquardException.class, () -> ints(null).mapBatches(cancel, Codec.INT, Collections.nCopies(1_000, codec.arg(1L)), 10));
        assertTrue(released[0], "batch 0's encoding was still held while batch 2 was validated");
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
