package org.jacquardlang.host;

import java.util.ArrayList;
import java.util.List;
import java.util.Objects;
import java.util.TreeMap;

import org.jacquardlang.host.protocol.EffectRequest;
import org.jacquardlang.host.protocol.Hash;
import org.jacquardlang.host.protocol.OperationEntry;

/** A closed map from exact operation identities to handlers. */
public final class OperationRegistry {
    private final TreeMap<OperationEntry, Handler> handlers = new TreeMap<>();

    /** An empty registry. */
    public OperationRegistry() {}

    /**
     * Binds a handler to one effect operation identity.
     *
     * @throws IllegalArgumentException if the identity is already bound
     */
    public synchronized OperationRegistry bind(Hash effect, Hash operation, Handler handler) {
        OperationEntry key = new OperationEntry(effect, operation);
        Objects.requireNonNull(handler, "handler");
        if (handlers.containsKey(key)) {
            throw new IllegalArgumentException("operation identity is already bound");
        }
        handlers.put(key, handler);
        return this;
    }

    /** The bound operations in protocol order. */
    public synchronized List<OperationEntry> entries() {
        return new ArrayList<>(handlers.keySet());
    }

    synchronized Handler handler(EffectRequest request) {
        return handlers.get(new OperationEntry(request.effect(), request.operation()));
    }
}
