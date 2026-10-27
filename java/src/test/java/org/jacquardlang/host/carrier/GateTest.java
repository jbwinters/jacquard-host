package org.jacquardlang.host.carrier;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.TimeUnit;

import org.junit.jupiter.api.Test;

/** The stdin thread admits each chunk only while input is open and the lifetime is live. */
class GateTest {
    private static final long MINUTE = TimeUnit.MINUTES.toNanos(1);

    private static IOException write(Writer writer, String text) throws Exception {
        return writer.submit(text.getBytes(StandardCharsets.UTF_8)).get(5, TimeUnit.SECONDS);
    }

    @Test
    void aClosedGateAdmitsNoWrite() throws Exception {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        Gate gate = new Gate(System.nanoTime() + MINUTE, new Cancel());
        Writer writer = new Writer(out, gate);
        writer.start();
        assertNull(write(writer, "before"));
        gate.close();
        IOException refused = write(writer, "after");
        assertNotNull(refused);
        assertEquals("worker input closed by the host", refused.getMessage());
        assertTrue(writer.join(TimeUnit.SECONDS.toNanos(5)), "the stdin thread did not stop after a refused write");
        assertEquals("before", out.toString(StandardCharsets.UTF_8));
        // A stopped thread accepts no further job.
        assertNull(writer.submit(new byte[] {1}));
    }

    @Test
    void noWriteStartsAfterTheDeadlineOrCancellation() throws Exception {
        for (boolean cancelled : new boolean[] {false, true}) {
            ByteArrayOutputStream out = new ByteArrayOutputStream();
            Cancel cancel = new Cancel();
            if (cancelled) {
                cancel.cancel();
            }
            Gate gate = new Gate(cancelled ? System.nanoTime() + MINUTE : System.nanoTime(), cancel);
            Writer writer = new Writer(out, gate);
            writer.start();
            assertNotNull(write(writer, "late"));
            assertEquals(0, out.size());
            assertTrue(writer.join(TimeUnit.SECONDS.toNanos(5)));
        }
    }

    @Test
    void aGateClosedDuringAWriteStopsBeforeTheNextChunk() throws Exception {
        Gate gate = new Gate(System.nanoTime() + MINUTE, new Cancel());
        ByteArrayOutputStream out = new ByteArrayOutputStream() {
            @Override
            public synchronized void write(byte[] bytes, int offset, int length) {
                super.write(bytes, offset, length);
                // Close as soon as the first chunk lands, as expiry would.
                gate.close();
            }
        };
        Writer writer = new Writer(out, gate);
        writer.start();
        byte[] frame = new byte[3 * Writer.CHUNK];
        IOException refused = writer.submit(frame).get(5, TimeUnit.SECONDS);
        assertNotNull(refused);
        assertEquals(Writer.CHUNK, out.size());
        assertArrayEquals(new byte[Writer.CHUNK], out.toByteArray());
    }

    @Test
    void closingStopsAnIdleThreadAndClosesTheStream() throws Exception {
        boolean[] closed = {false};
        ByteArrayOutputStream out = new ByteArrayOutputStream() {
            @Override
            public void close() {
                closed[0] = true;
            }
        };
        Writer writer = new Writer(out, new Gate(System.nanoTime() + MINUTE, new Cancel()));
        writer.start();
        writer.close();
        assertTrue(writer.join(TimeUnit.SECONDS.toNanos(5)));
        assertTrue(closed[0]);
        assertFalse(writer.isAlive());
    }
}
