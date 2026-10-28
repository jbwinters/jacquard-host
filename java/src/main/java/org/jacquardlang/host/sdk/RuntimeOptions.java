package org.jacquardlang.host.sdk;

import java.nio.file.Path;
import java.time.Duration;

import org.jacquardlang.host.protocol.Limits;

/**
 * Options for Core discovery and invocation bounds. A null or empty value
 * means unset.
 *
 * @param binary the Core executable; defaults to {@code $JACQUARD_BIN}, then {@code jacquard}
 * @param prelude Core's prelude directory; defaults to {@code $JACQUARD_PRELUDE}
 * @param deadline bounds each invocation's worker lifetime (default 30 seconds)
 * @param limits the session limits; null selects the hard limits
 */
public record RuntimeOptions(String binary, Path prelude, Duration deadline, Limits limits) {
    /** Every option unset. */
    public static RuntimeOptions defaults() {
        return new RuntimeOptions(null, null, null, null);
    }

    /** These options with another Core executable. */
    public RuntimeOptions withBinary(String value) {
        return new RuntimeOptions(value, prelude, deadline, limits);
    }

    /** These options with another prelude directory. */
    public RuntimeOptions withPrelude(Path value) {
        return new RuntimeOptions(binary, value, deadline, limits);
    }

    /** These options with another worker lifetime. */
    public RuntimeOptions withDeadline(Duration value) {
        return new RuntimeOptions(binary, prelude, value, limits);
    }

    /** These options with other session limits. */
    public RuntimeOptions withLimits(Limits value) {
        return new RuntimeOptions(binary, prelude, deadline, value);
    }
}
