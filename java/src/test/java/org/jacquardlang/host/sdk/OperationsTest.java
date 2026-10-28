package org.jacquardlang.host.sdk;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.io.IOException;
import java.util.List;
import java.util.Optional;
import java.util.concurrent.atomic.AtomicInteger;

import org.jacquardlang.host.HandlerContext;
import org.jacquardlang.host.HandlerException;
import org.jacquardlang.host.carrier.Cancel;
import org.jacquardlang.host.protocol.FailureCategory;
import org.jacquardlang.host.protocol.Limits;
import org.jacquardlang.host.protocol.Value;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.Test;

/** Java handlers adapted to operations: decoding, results, failures, and the stop check before a handler starts. */
class OperationsTest {
    private final Data data = DataTest.fixture(Limits.HARD);
    private final HandlerContext context = HandlerContext.of(new Cancel());

    @AfterEach
    void clearHook() {
        Operations.afterDecodeHook = null;
    }

    private static Value text(String text) {
        return new Value.Text(text);
    }

    private Value call(Operations service, String name, HandlerContext context, Value... arguments) throws Exception {
        return service.handlers().get(name).handle(data, context, List.of(arguments));
    }

    private HandlerException failure(Operations service, String name, Value... arguments) {
        return assertThrows(HandlerException.class, () -> call(service, name, context, arguments));
    }

    @Test
    void handlersAdaptToOperations() throws Exception {
        AtomicInteger calls = new AtomicInteger();
        Operations service = new Operations()
                .on("read-document", Codec.TEXT, Codec.TEXT, (HandlerContext ctx, String key) -> {
                    calls.incrementAndGet();
                    if (ctx.isCancelled()) {
                        throw HandlerException.completionUnknown("cancelled");
                    }
                    if (key.equals("example")) {
                        return "text";
                    }
                    throw HandlerException.refused("unknown document");
                })
                .on("forget", Codec.TEXT, Codec.UNIT, key -> {
                    throw new IOException("private detail");
                })
                .on("lookup_stock", Codec.TEXT, Codec.INT, sku -> (long) sku.length())
                .on("pair", Codec.tuple(Codec.INT, Codec.INT), () -> List.<Object>of(3L, 4L))
                .on("route", DataTest.INCIDENT, DataTest.TRIAGE,
                        incident -> new DataTest.Routed(incident.id(), DataTest.Urgency.WATCH_ONLY))
                .on("huge", Codec.INT, () -> Long.MAX_VALUE)
                .on("nothing", Codec.TEXT, () -> null);
        assertEquals(text("text"), call(service, "read-document", context, text("example")));
        assertEquals(1, calls.get());
        assertEquals(FailureCategory.REFUSED_AUTHORITY, failure(service, "read-document", text("other")).category());
        HandlerException arity = failure(service, "read-document");
        assertEquals(FailureCategory.OUTSIDE_FAILURE, arity.category());
        assertEquals("handler parameter count differs from the request", arity.getMessage());
        HandlerException mapping = failure(service, "read-document", new Value.Int(1));
        assertEquals("handler argument cannot be mapped", mapping.getMessage());
        assertEquals(2, calls.get(), "a handler ran with unmappable arguments");
        // Any other exception reaches the host, which reports completion unknown without its message.
        assertThrows(IOException.class, () -> call(service, "forget", context, text("x")));
        assertEquals(new Value.Int(3), call(service, "lookup-stock", context, text("abc")));
        assertEquals(new Value.Tuple(List.of(new Value.Int(3), new Value.Int(4))), call(service, "pair", context));
        Value incident = data.encode(DataTest.INCIDENT, new DataTest.Incident("i", 1, Optional.empty()));
        assertEquals(new DataTest.Routed("i", DataTest.Urgency.WATCH_ONLY), data.decode(DataTest.TRIAGE, call(service, "route", context, incident)));
        for (String unrepresentable : List.of("huge", "nothing")) {
            HandlerException result = failure(service, unrepresentable);
            assertEquals(FailureCategory.OUTSIDE_FAILURE, result.category());
            assertEquals("handler result cannot be represented", result.getMessage());
        }
        Operations replaced = new Operations().on("op", Codec.INT, () -> 1L).on("op", Codec.INT, () -> 2L);
        assertEquals(new Value.Int(2), call(replaced, "op", context));
    }

    /**
     * Decoding a request takes time. A stop that lands after the arguments are
     * decoded, here through the test seam, must keep the handler from
     * starting; the handler is never called.
     */
    @Test
    void noHandlerStartsOnceTheCallStoppedDuringDecoding() throws Exception {
        Cancel cancel = new Cancel();
        AtomicInteger calls = new AtomicInteger();
        Operations service = new Operations().on("act", Codec.TEXT, Codec.UNIT, key -> {
            calls.incrementAndGet();
            return null;
        });
        Operations.afterDecodeHook = cancel::cancel;
        HandlerException stopped = assertThrows(HandlerException.class, () -> call(service, "act", HandlerContext.of(cancel), text("x")));
        assertEquals("handler was not started", stopped.getMessage());
        assertEquals(0, calls.get(), "the handler started after the call stopped");
        Operations.afterDecodeHook = null;
        assertTrue(call(service, "act", context, text("x")) instanceof Value.Tuple);
        assertEquals(1, calls.get());
    }
}
