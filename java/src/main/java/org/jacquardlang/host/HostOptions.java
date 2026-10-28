package org.jacquardlang.host;

import org.jacquardlang.host.carrier.Options;
import org.jacquardlang.host.protocol.Limits;

/**
 * Options for a {@link Host}.
 *
 * @param carrier the worker command, environment, and lifetime deadline; null means defaults
 * @param limits the session limits; null selects the hard limits
 */
public record HostOptions(Options carrier, Limits limits) {
    public HostOptions {
        if (carrier == null) {
            carrier = Options.defaults();
        }
    }

    /** Default carrier options and the hard limits. */
    public static HostOptions defaults() {
        return new HostOptions(Options.defaults(), null);
    }
}
