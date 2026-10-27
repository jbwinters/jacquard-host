package org.jacquardlang.host.protocol;

import org.jacquardlang.host.ProtocolException;
import org.jacquardlang.host.json.JsonCodec;

/** Counts nodes and checks collection and text sizes for one frame. */
public final class Budget {
    private final Limits limits;
    private int nodes;

    /** A fresh budget under the given limits. */
    public Budget(Limits limits) {
        this.limits = limits;
    }

    /** The limits this budget charges against. */
    public Limits limits() {
        return limits;
    }

    void consume() throws ProtocolException {
        if (nodes >= limits.maxValueNodes()) {
            throw ProtocolException.exceeded("frame exceeds max_value_nodes");
        }
        nodes++;
    }

    void count(int n, boolean arguments) throws ProtocolException {
        if (n > limits.maxCollectionItems() || (arguments && n > limits.maxArguments())) {
            throw ProtocolException.exceeded("collection exceeds selected limits");
        }
    }

    void text(String text) throws ProtocolException {
        if (!JsonCodec.isScalar(text)) {
            throw ProtocolException.malformed("a JSON string contains a lone surrogate");
        }
        if (JsonCodec.utf8Length(text) > limits.maxTextBytes()) {
            throw ProtocolException.exceeded("Text exceeds max_text_bytes");
        }
    }
}
