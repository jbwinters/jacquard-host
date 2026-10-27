package org.jacquardlang.host.protocol;

import org.jacquardlang.host.ProtocolException;
import org.jacquardlang.host.json.JsonCodec;

/**
 * Counts nodes and checks collection and text sizes for one frame. Text is
 * charged against the frame too, so validating a value scans at most about
 * one frame of text, however many times a long text is referenced.
 */
public final class Budget {
    private final Limits limits;
    private int nodes;
    /** The UTF-8 bytes of every text checked so far. */
    private long textBytes;

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
        // A string's UTF-8 length is at least its length in chars, so these
        // refuse before scanning: the scan below covers at most one text and
        // one frame.
        if (text.length() > limits.maxTextBytes()) {
            throw ProtocolException.exceeded("Text exceeds max_text_bytes");
        }
        if (textBytes + text.length() > limits.maxFrameBytes()) {
            throw ProtocolException.exceeded("payload exceeds max_frame_bytes");
        }
        if (!JsonCodec.isScalar(text)) {
            throw ProtocolException.malformed("a JSON string contains a lone surrogate");
        }
        int length = JsonCodec.utf8Length(text);
        if (length > limits.maxTextBytes()) {
            throw ProtocolException.exceeded("Text exceeds max_text_bytes");
        }
        textBytes += length;
        if (textBytes > limits.maxFrameBytes()) {
            throw ProtocolException.exceeded("payload exceeds max_frame_bytes");
        }
    }
}
