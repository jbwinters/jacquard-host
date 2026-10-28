package org.jacquardlang.host.protocol;

import java.util.List;
import java.util.Objects;

/** The checked callable signature sent with an invocation. */
public record Interface(List<BoundaryType> parameters, BoundaryType result, List<Hash> effects) {
    public Interface {
        parameters = List.copyOf(parameters);
        Objects.requireNonNull(result, "result");
        effects = List.copyOf(effects);
    }
}
