package org.jacquardlang.host.sdk;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeMap;

import org.jacquardlang.host.protocol.BoundaryType;
import org.jacquardlang.host.protocol.Hash;
import org.jacquardlang.host.protocol.Limits;
import org.jacquardlang.host.protocol.Value;

/**
 * The prepared program's public names and identities, used to map native
 * values. It is immutable once the program is prepared.
 */
public final class Data {
    static final Set<String> BUILTINS = Set.of("true", "false", "some", "none", "cons", "nil");

    private final Map<String, Hash> names;
    private final Map<String, Hash> constructors;
    private final Map<Hash, String> labels = new HashMap<>();
    private final Map<Hash, String> primitives = new HashMap<>();
    /** Each declaration group's head identity and its member identities. */
    private final Map<Hash, List<Hash>> members = new HashMap<>();
    private final Limits limits;

    Data(Map<String, Hash> names, Map<String, Hash> constructors, List<List<Identities.Entry>> groups, Limits limits) {
        this.names = Map.copyOf(names);
        this.constructors = Map.copyOf(constructors);
        this.limits = limits;
        for (Map.Entry<String, Hash> entry : new TreeMap<>(constructors).entrySet()) {
            labels.put(entry.getValue(), entry.getKey());
        }
        for (String name : List.of("int", "real", "text", "bool", "hash", "list", "option")) {
            Hash identity = names.get(name);
            if (identity != null) {
                primitives.put(identity, name);
            }
        }
        for (List<Identities.Entry> group : groups) {
            if (!group.isEmpty()) {
                List<Hash> rest = new ArrayList<>();
                for (Identities.Entry entry : group.subList(1, group.size())) {
                    rest.add(entry.hash());
                }
                members.put(group.get(0).hash(), List.copyOf(rest));
            }
        }
    }

    /** The session limits that bound mapped values. */
    public Limits limits() {
        return limits;
    }

    /**
     * The exact identity of a public type, constructor, effect, or term name
     * in the prepared program and its discovered prelude.
     *
     * @throws JacquardException a binding failure for an unknown name
     */
    public Hash identity(String name) throws JacquardException {
        Hash identity = names.get(Identities.displayToStore(name));
        if (identity == null) {
            throw JacquardException.binding("unknown name " + Identities.excerpt(name));
        }
        return identity;
    }

    /** The public name of a constructor or operation identity, or null. */
    public String label(Hash identity) {
        return labels.get(identity);
    }

    Hash name(String name) {
        return names.get(name);
    }

    Hash constructorId(String name) {
        return constructors.get(Identities.displayToStore(name));
    }

    /**
     * A {@link Dynamic.Variant} after checking that the constructor exists.
     *
     * @throws JacquardException a binding failure for an unknown constructor
     */
    public Dynamic make(String name, Dynamic... fields) throws JacquardException {
        if (constructorId(name) == null) {
            throw JacquardException.binding("unknown constructor " + Identities.excerpt(name));
        }
        return new Dynamic.Variant(Identities.displayToStore(name), Arrays.asList(fields));
    }

    /**
     * Encodes a native value, bounded by the selected limits.
     *
     * @throws JacquardException an invalid argument naming the value's location
     */
    public <T> Value encode(Codec<T> codec, T value) throws JacquardException {
        return Encoder.root(this, "value").encode(codec, value);
    }

    /**
     * Decodes a boundary value into a native value. The value is first
     * measured, within the selected {@code max_value_nodes} and nesting
     * limits, so a shared or deep value cannot make decoding unbounded; every
     * value Core sends is within them.
     *
     * @throws JacquardException a binding failure when it does not decode
     */
    public <T> T decode(Codec<T> codec, Value value) throws JacquardException {
        measure(value, 0, new int[1]);
        return codec.decode(new Decoder(this), value);
    }

    /** Refuses a value with more nodes than {@code max_value_nodes}, or nested past the JSON depth. */
    private void measure(Value value, int level, int[] nodes) throws JacquardException {
        if (++nodes[0] > limits.maxValueNodes()) {
            throw JacquardException.binding("value exceeds max_value_nodes");
        }
        // A value at the top of a frame's payload is at JSON depth 3, and each level adds 2.
        if (3L + 2L * level > limits.maxJsonDepth()) {
            throw JacquardException.binding("value exceeds the v0 JSON depth");
        }
        List<Value> children = switch (value) {
            case Value.Tuple tuple -> tuple.items();
            case Value.Constructor constructor -> constructor.arguments();
            default -> List.of();
        };
        for (Value child : children) {
            measure(child, level + 1, nodes);
        }
    }

    String primitive(BoundaryType expected) {
        return expected instanceof BoundaryType.Nominal nominal ? primitives.get(nominal.identity()) : null;
    }

    boolean isNullary(Value value, String name) {
        return value instanceof Value.Constructor constructor && constructor.arguments().isEmpty()
                && name.equals(label(constructor.identity()));
    }

    /** Walks a proper List, bounded by the collection limit; null when it is not one. */
    List<Value> listItems(Value value) throws JacquardException {
        List<Value> items = new ArrayList<>();
        Value cursor = value;
        while (true) {
            if (cursor instanceof Value.Constructor constructor) {
                String label = label(constructor.identity());
                if (constructor.arguments().size() == 2 && "cons".equals(label)) {
                    if (items.size() >= limits.maxCollectionItems()) {
                        throw JacquardException.binding("list exceeds the selected limit");
                    }
                    items.add(constructor.arguments().get(0));
                    cursor = constructor.arguments().get(1);
                    continue;
                }
                if (constructor.arguments().isEmpty() && "nil".equals(label)) {
                    return items;
                }
            }
            throw JacquardException.binding("expected a proper List");
        }
    }

    /**
     * Checks that an encoded value has the checked boundary type, as far as
     * the prepared metadata describes it: primitives, Lists, Options, and
     * tuples exactly, and other data by its constructor's declaration group.
     */
    void conform(Value value, BoundaryType expected, String path) throws JacquardException {
        String primitive = primitive(expected);
        if (expected instanceof BoundaryType.Tuple tuple) {
            if (value instanceof Value.Tuple values && values.items().size() == tuple.items().size()) {
                for (int i = 0; i < values.items().size(); i++) {
                    conform(values.items().get(i), tuple.items().get(i), path + "[" + i + "]");
                }
                return;
            }
            throw refuse(path, "a " + tuple.items().size() + "-item tuple");
        }
        BoundaryType.Nominal nominal = (BoundaryType.Nominal) expected;
        BoundaryType child = nominal.arguments().isEmpty() ? null : nominal.arguments().get(0);
        if (primitive == null) {
            List<Hash> group = members.get(nominal.identity());
            if (value instanceof Value.Constructor constructor && (group == null || group.contains(constructor.identity()))) {
                return;
            }
            throw refuse(path, "a " + labelOfType(nominal.identity()) + " value");
        }
        switch (primitive) {
            case "int" -> {
                if (!(value instanceof Value.Int)) {
                    throw refuse(path, "an integer");
                }
            }
            case "real" -> {
                if (!(value instanceof Value.Real)) {
                    throw refuse(path, "a Real (double)");
                }
            }
            case "text" -> {
                if (!(value instanceof Value.Text)) {
                    throw refuse(path, "text");
                }
            }
            case "hash" -> {
                if (!(value instanceof Value.HashValue)) {
                    throw refuse(path, "a Hash");
                }
            }
            case "bool" -> {
                if (!isNullary(value, "true") && !isNullary(value, "false")) {
                    throw refuse(path, "a bool");
                }
            }
            case "list" -> {
                List<Value> items;
                try {
                    items = listItems(value);
                } catch (JacquardException error) {
                    throw JacquardException.invalidAt(path, "expected a List");
                }
                if (child != null) {
                    for (int i = 0; i < items.size(); i++) {
                        conform(items.get(i), child, path + "[" + i + "]");
                    }
                }
            }
            default -> {
                if (isNullary(value, "none")) {
                    return;
                }
                if (value instanceof Value.Constructor constructor && constructor.arguments().size() == 1
                        && "some".equals(label(constructor.identity()))) {
                    if (child != null) {
                        conform(constructor.arguments().get(0), child, path);
                    }
                    return;
                }
                throw refuse(path, "an Option");
            }
        }
    }

    private static JacquardException refuse(String path, String what) {
        return JacquardException.invalidAt(path, "expected " + what);
    }

    private String labelOfType(Hash identity) {
        for (Map.Entry<String, Hash> entry : new TreeMap<>(names).entrySet()) {
            if (entry.getValue().equals(identity)) {
                return entry.getKey();
            }
        }
        return identity.hex();
    }
}
