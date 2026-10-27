package org.jacquardlang.host;

import java.time.Duration;
import java.util.List;

import org.jacquardlang.host.carrier.Cancel;
import org.jacquardlang.host.carrier.Options;

/**
 * Runs one shutdown probe in its own JVM, so {@link SetupTest} can put a slow
 * {@code mkfifo} first on this JVM's {@code PATH}. Prints the reason, the
 * elapsed milliseconds, and whether the caller is still interrupted.
 */
public final class SetupProbe {
    private SetupProbe() {}

    public static void main(String[] args) throws Exception {
        String mode = args[0];
        Cancel cancel = new Cancel();
        Thread caller = Thread.currentThread();
        if (!mode.equals("deadline")) {
            Thread.ofPlatform().daemon().start(() -> {
                try {
                    Thread.sleep(100);
                } catch (InterruptedException ignored) {
                    return;
                }
                if (mode.equals("interrupt")) {
                    caller.interrupt();
                } else {
                    cancel.cancel();
                }
            });
        }
        Duration deadline = mode.equals("deadline") ? Duration.ofMillis(500) : Duration.ofSeconds(20);
        // The worker never starts: setup must finish or stop first.
        Options options = new Options(List.of("/nonexistent/jacquard"), null, deadline);
        long start = System.nanoTime();
        InvocationResult result = new Host("unused", new HostOptions(options, null)).shutdownProbe(cancel);
        long elapsed = (System.nanoTime() - start) / 1_000_000;
        System.out.println(Support.reason(result) + " " + elapsed + " " + Thread.interrupted());
    }
}
