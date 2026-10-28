package org.jacquardlang.host.protocol;

import java.util.Objects;

/** An exact effect operation granted for the invocation. */
public record OperationEntry(Hash effect, Hash operation) implements Comparable<OperationEntry> {
    public OperationEntry {
        Objects.requireNonNull(effect, "effect");
        Objects.requireNonNull(operation, "operation");
    }

    @Override
    public int compareTo(OperationEntry other) {
        int order = effect.compareTo(other.effect);
        return order != 0 ? order : operation.compareTo(other.operation);
    }
}
