package org.jacquardlang.host.protocol;

import java.util.List;
import java.util.Objects;

/** A closed v0 boundary type. */
public sealed interface BoundaryType permits BoundaryType.Nominal, BoundaryType.Tuple {
    /** A type identity applied to arguments. */
    record Nominal(Hash identity, List<BoundaryType> arguments) implements BoundaryType {
        public Nominal {
            Objects.requireNonNull(identity, "identity");
            arguments = List.copyOf(arguments);
        }
    }

    /** A positional product type. */
    record Tuple(List<BoundaryType> items) implements BoundaryType {
        public Tuple {
            items = List.copyOf(items);
        }
    }
}
