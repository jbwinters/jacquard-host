package org.jacquardlang.host.sdk;

import java.util.ArrayList;
import java.util.List;

import org.jacquardlang.host.json.Json;
import org.jacquardlang.host.json.JsonCodec;
import org.jacquardlang.host.protocol.Value;

/**
 * Encodes one value at a location. Every encoding is bounded by the selected
 * limits as it goes, so no value, however it is built (one long text referenced
 * many times, say), costs more than about one frame of work and memory: the
 * JSON depth Core's wire form will need, {@code max_collection_items} for each
 * list and tuple, {@code max_value_nodes} values, and {@code max_text_bytes}
 * for each text (in UTF-8 bytes) and {@code max_frame_bytes} for all of them
 * together. A text longer in chars than either is refused before it is
 * scanned.
 *
 * <p>Only what goes through an encoder is charged as it is built:
 * {@link Codec#VALUE} and {@link Value}s passed as arguments are walked within
 * the same bounds, but a custom codec may return a {@link Value} it built
 * itself. Such a value is not unchecked: before its type is checked, and so
 * before any worker starts, a function call measures every argument value
 * again under one shared budget (nodes, JSON depth, collection items, and
 * text bytes, alone and together), whatever codec built it; the invoke frame
 * is then measured as it is rendered, and the host measures a handler's
 * result the same way before sending it. A value returned by
 * {@link Data#encode} is not measured again.
 */
public final class Encoder {
    /** What one encoding has used, shared by every location in it. */
    static final class Budget {
        int nodes;
        long textBytes;
    }

    private final Data data;
    private final String path;
    private final int depth;
    private final Budget budget;

    private Encoder(Data data, String path, int depth, Budget budget) {
        this.data = data;
        this.path = path;
        this.depth = depth;
        this.budget = budget;
    }

    /** A root encoder at {@code path}, with a fresh budget. */
    static Encoder root(Data data, String path) throws JacquardException {
        return root(data, path, new Budget());
    }

    /** A root encoder at {@code path} sharing {@code budget}, as one frame's arguments do. */
    static Encoder root(Data data, String path, Budget budget) throws JacquardException {
        Encoder encoder = new Encoder(data, path, 3, budget);
        encoder.checkDepth(encoder.depth);
        return encoder;
    }

    private void checkDepth(long needed) throws JacquardException {
        if (needed > data.limits().maxJsonDepth()) {
            throw invalid("value exceeds the v0 JSON depth; use smaller independent batches");
        }
    }

    /** The program's data. */
    public Data data() {
        return data;
    }

    /** The location being encoded, such as {@code triage.batch.argument[1][0].service}. */
    public String path() {
        return path;
    }

    /** An invalid-argument failure at this location. */
    public JacquardException invalid(String message) {
        return JacquardException.invalidAt(path, message);
    }

    /** An invalid-argument failure at this location, caused by {@code cause}. */
    JacquardException invalid(String message, Throwable cause) {
        return JacquardException.invalidAt(path, message, cause);
    }

    private Encoder child(String segment, int extra) throws JacquardException {
        Encoder child = new Encoder(data, path + segment, depth + extra, budget);
        child.checkDepth(child.depth);
        return child;
    }

    /** Charges one value node. */
    private Value node(Value value) throws JacquardException {
        if (budget.nodes >= data.limits().maxValueNodes()) {
            throw invalid("value exceeds max_value_nodes; use smaller independent batches");
        }
        budget.nodes++;
        return value;
    }

    /** Encodes {@code value} with {@code codec} here. */
    <T> Value encode(Codec<T> codec, T value) throws JacquardException {
        if (value == null && !(codec instanceof Codecs.Unit)) {
            throw invalid("null has no Jacquard value; use Optional.empty() for None or Codec.UNIT for unit");
        }
        return codec.encode(this, value);
    }

    /** Encodes a named field of a record or variant. */
    public <T> Value field(String name, Codec<T> codec, T value) throws JacquardException {
        return child("." + Identities.excerpt(name), 2).encode(codec, value);
    }

    /** Encodes a positional item of a tuple. */
    public <T> Value item(int index, Codec<T> codec, T value) throws JacquardException {
        return child("[" + index + "]", 2).encode(codec, value);
    }

    /**
     * A constructor value by public name, such as {@code "Job"} or {@code "job"}.
     * Bool, List, and Option constructors are refused: use {@link Codec#BOOL},
     * {@link Codec#list}, and {@link Codec#optional}.
     */
    public Value constructor(String name, List<Value> fields) throws JacquardException {
        // Counted before the fields are copied into the value.
        checkCount(fields.size(), "constructor");
        if (Data.BUILTINS.contains(Identities.displayToStore(name))) {
            throw invalid("use the native form of builtin constructor " + Identities.excerpt(name));
        }
        return builtin(name, fields);
    }

    private Value builtin(String name, List<Value> fields) throws JacquardException {
        var identity = data.constructorId(name);
        if (identity == null) {
            throw invalid("unknown constructor " + Identities.excerpt(name));
        }
        return node(new Value.Constructor(identity, fields));
    }

    /** A tuple of already encoded items. */
    Value tuple(List<Value> items) throws JacquardException {
        return node(new Value.Tuple(items));
    }

    /** Refuses more than {@code max_collection_items} items, before any is encoded. */
    void checkCount(int count, String what) throws JacquardException {
        if (count > data.limits().maxCollectionItems()) {
            throw invalid(what + " exceeds max_collection_items");
        }
    }

    Value bool(boolean value) throws JacquardException {
        return builtin(value ? "true" : "false", List.of());
    }

    Value integer(long value) throws JacquardException {
        if (value < Json.INT_MIN || value > Json.INT_MAX) {
            throw invalid("integer is outside Core's 63-bit Int range");
        }
        return node(new Value.Int(value));
    }

    Value real(double value) throws JacquardException {
        return node(Value.real(value));
    }

    Value text(String text) throws JacquardException {
        chargeText(text);
        return node(new Value.Text(text));
    }

    /** Charges a text by its UTF-8 bytes, refusing before it is scanned when its length in chars already exceeds a limit. */
    private void chargeText(String text) throws JacquardException {
        int limit = data.limits().maxTextBytes();
        // A text's UTF-8 length is at least its length in chars, so these
        // refuse before it is scanned.
        if (text.length() > limit) {
            throw invalid("text exceeds max_text_bytes");
        }
        if (budget.textBytes + text.length() > data.limits().maxFrameBytes()) {
            throw invalid("value exceeds max_frame_bytes; use smaller independent batches");
        }
        if (!JsonCodec.isScalar(text)) {
            throw invalid("text contains a lone surrogate, which has no Jacquard encoding");
        }
        int length = JsonCodec.utf8Length(text);
        if (length > limit) {
            throw invalid("text exceeds max_text_bytes");
        }
        budget.textBytes += length;
        if (budget.textBytes > data.limits().maxFrameBytes()) {
            throw invalid("value exceeds max_frame_bytes; use smaller independent batches");
        }
    }

    Value hash(org.jacquardlang.host.protocol.Hash hash) throws JacquardException {
        return node(new Value.HashValue(hash));
    }

    /** A List: one {@code cons} per item, nested on the wire. */
    <T> Value list(Codec<T> codec, List<? extends T> items) throws JacquardException {
        checkCount(items.size(), "list");
        // A linked list nests one constructor per element on the wire.
        checkDepth((long) depth + 2L * items.size());
        List<Value> encoded = new ArrayList<>(items.size());
        for (int index = items.size() - 1; index >= 0; index--) {
            encoded.add(child("[" + index + "]", 2 * (index + 1)).encode(codec, items.get(index)));
        }
        Value result = builtin("nil", List.of());
        for (Value item : encoded) {
            result = builtin("cons", List.of(item, result));
        }
        return result;
    }

    /** {@code None}, or {@code Some} of {@code value} one level deeper. */
    <T> Value option(Codec<T> codec, T value) throws JacquardException {
        if (value == null) {
            return builtin("none", List.of());
        }
        Value inner = child("", 2).encode(codec, value);
        return builtin("some", List.of(inner));
    }

    /**
     * Charges a raw boundary value passed through unchanged, walking it
     * within the same bounds; a value shared many times is counted each time
     * it occurs.
     */
    Value raw(Value value) throws JacquardException {
        charge(value, 0);
        return value;
    }

    private void charge(Value value, int level) throws JacquardException {
        if ((long) depth + 2L * level > data.limits().maxJsonDepth()) {
            throw invalid("value exceeds the v0 JSON depth; use smaller independent batches");
        }
        node(value);
        switch (value) {
            case Value.Text text -> chargeText(text.value());
            case Value.Tuple tuple -> {
                checkCount(tuple.items().size(), "tuple");
                for (Value item : tuple.items()) {
                    charge(item, level + 1);
                }
            }
            case Value.Constructor constructor -> {
                checkCount(constructor.arguments().size(), "constructor");
                for (Value argument : constructor.arguments()) {
                    charge(argument, level + 1);
                }
            }
            default -> {
                // Int, Real, and Hash are one node each.
            }
        }
    }
}
