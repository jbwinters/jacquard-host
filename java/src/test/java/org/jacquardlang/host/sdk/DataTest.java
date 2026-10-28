package org.jacquardlang.host.sdk;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.util.AbstractList;
import java.util.ArrayList;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Optional;

import org.jacquardlang.host.json.Json;
import org.jacquardlang.host.protocol.BoundaryType;
import org.jacquardlang.host.protocol.Hash;
import org.jacquardlang.host.protocol.Interface;
import org.jacquardlang.host.protocol.Limits;
import org.jacquardlang.host.protocol.Value;
import org.junit.jupiter.api.Test;

/** Signatures, identity groups, and native values through checked types, with synthetic identities. */
class DataTest {
    static Hash id(char letter) {
        return new Hash(String.valueOf(letter).repeat(64));
    }

    static Hash numbered(int n) {
        return new Hash(String.format("%064x", n));
    }

    static BoundaryType nominal(Hash identity, BoundaryType... arguments) {
        return new BoundaryType.Nominal(identity, List.of(arguments));
    }

    /** Data with synthetic identities for the builtins, an Incident record, an Urgency enum, and a Triage union. */
    static Data fixture(Limits limits) {
        Map<String, Hash> names = new LinkedHashMap<>();
        names.put("int", id('1'));
        names.put("real", id('2'));
        names.put("text", id('3'));
        names.put("bool", id('4'));
        names.put("hash", id('5'));
        names.put("list", id('6'));
        names.put("option", id('7'));
        names.put("incident", id('8'));
        names.put("urgency", id('9'));
        names.put("triage", numbered(10));
        Map<String, Hash> constructors = new LinkedHashMap<>();
        constructors.put("true", id('a'));
        constructors.put("false", id('b'));
        constructors.put("some", id('c'));
        constructors.put("none", id('d'));
        constructors.put("cons", id('e'));
        constructors.put("nil", id('f'));
        constructors.put("incident", id('0'));
        constructors.put("page-now", numbered(1));
        constructors.put("watch-only", numbered(2));
        constructors.put("routed", numbered(3));
        constructors.put("invalid-incident", numbered(4));
        List<List<Identities.Entry>> groups = new ArrayList<>();
        for (List<String> group : List.of(List.of("incident", "incident"), List.of("urgency", "page-now", "watch-only"),
                List.of("triage", "routed", "invalid-incident"))) {
            List<Identities.Entry> entries = new ArrayList<>();
            entries.add(new Identities.Entry(group.get(0), names.get(group.get(0))));
            for (String member : group.subList(1, group.size())) {
                entries.add(new Identities.Entry(member, constructors.get(member)));
            }
            groups.add(entries);
        }
        return new Data(names, constructors, groups, limits);
    }

    record Incident(String id, long severity, Optional<String> owner) {}

    enum Urgency {
        PAGE_NOW,
        WATCH_ONLY
    }

    sealed interface Triage {}

    record Routed(String id, Urgency urgency) implements Triage {}

    record InvalidIncident(String id) implements Triage {}

    static final Codec<Incident> INCIDENT = Codec.record(Incident.class);
    static final Codec<Triage> TRIAGE = Codec.union(Triage.class);

    private static String refusal(Executable body) {
        JacquardException error = assertThrows(JacquardException.class, body::run);
        return error.getMessage();
    }

    @FunctionalInterface
    interface Executable {
        void run() throws Exception;
    }

    @Test
    void signaturesAndGroups() throws Exception {
        Map<String, Hash> names = Map.of("list", id('6'), "line", id('8'), "int", id('1'), "option", id('7'), "text", id('3'),
                "inventory", id('9'), "dispatch", id('a'));
        Interface spec = Identities.parseSignature("(List Line, Int) ->{Inventory, Dispatch} Option Text", names);
        assertEquals(List.of(nominal(id('6'), nominal(id('8'))), nominal(id('1'))), spec.parameters());
        assertEquals(List.of(id('9'), id('a')), spec.effects());
        assertEquals(nominal(id('7'), nominal(id('3'))), spec.result());
        assertEquals(new BoundaryType.Tuple(List.of()), Identities.parseSignature("() ->{} ()", names).result());
        assertEquals(nominal(id('1')), Identities.parseSignature("((Int, Text)) ->{} (Int)", names).result());
        assertEquals(List.of(new BoundaryType.Tuple(List.of(nominal(id('1')), nominal(id('3'))))),
                Identities.parseSignature("((Int, Text)) ->{} Int", names).parameters());
        for (String text : List.of("forall a | e. (List a) ->{| e} Bool", "(Missing) ->{} Int", "(Int) ->{} Int extra)", "(Int) ->{} Int $")) {
            assertThrows(JacquardException.class, () -> Identities.parseSignature(text, names), text);
        }
        String output = "0 " + id('1') + "\n0:line " + id('1') + "\n0:line " + id('2') + "\nnoise\n1:other " + id('3') + "\n";
        List<List<Identities.Entry>> groups = Identities.hashGroups(output);
        assertEquals(2, groups.size());
        assertEquals(id('2'), groups.get(0).get(1).hash());
        assertEquals(JacquardException.Kind.BINDING, assertThrows(JacquardException.class, () -> Identities.hashGroups("nothing here")).kind());
        Identities.Entry x1 = new Identities.Entry("x", id('1'));
        assertThrows(JacquardException.class, () -> Identities.checkGroups(List.of(List.of(x1), List.of(new Identities.Entry("x", id('2'))))));
        Identities.checkGroups(List.of(List.of(x1), List.of(x1)));
        for (String[] pair : new String[][] {{"WorkToday", "work-today"}, {"PageNow", "page-now"}, {"text", "text"}, {"Inventory", "inventory"}}) {
            assertEquals(pair[1], Identities.displayToStore(pair[0]));
        }
        assertEquals("lookup-stock", Identities.normal("lookup_stock"));
    }

    @Test
    void nativeValuesRoundtripAndConformToCheckedTypes() throws Exception {
        Data data = fixture(Limits.HARD);
        List<Incident> rows = List.of(new Incident("a", 5, Optional.empty()), new Incident("b", 2, Optional.of("ops")));
        BoundaryType list = nominal(id('6'), nominal(id('8')));
        Value value = data.encode(Codec.list(INCIDENT), rows);
        data.conform(value, list, "rows");
        assertEquals(rows, data.decode(Codec.list(INCIDENT), value));
        BoundaryType real = nominal(id('2'));
        for (double number : new double[] {1.0, -0.0, Double.POSITIVE_INFINITY, Double.NaN, Double.longBitsToDouble(0x7ff8000000000123L)}) {
            Value encoded = data.encode(Codec.REAL, number);
            data.conform(encoded, real, "real");
            assertEquals(Double.doubleToRawLongBits(number), Double.doubleToRawLongBits(data.decode(Codec.REAL, encoded)));
        }
        BoundaryType integer = nominal(id('1'));
        assertEquals("value: integer is outside Core's 63-bit Int range", refusal(() -> data.encode(Codec.INT, 1L << 62)));
        assertEquals("value: integer is outside Core's 63-bit Int range", refusal(() -> data.encode(Codec.INT, Long.MIN_VALUE)));
        assertEquals(new Value.Int(Json.INT_MAX), data.encode(Codec.INT, Json.INT_MAX));
        assertEquals(new Value.Int(Json.INT_MIN), data.encode(Codec.INT, Json.INT_MIN));
        assertThrows(JacquardException.class, () -> data.decode(Codec.INT32, new Value.Int(1L << 40)));
        assertEquals(7, data.decode(Codec.INT32, new Value.Int(7)));
        assertEquals("argument[0]: expected an integer", refusal(() -> data.conform(data.encode(Codec.TEXT, "x"), integer, "argument[0]")));
        assertEquals("argument[0]: expected a Real (double)", refusal(() -> data.conform(data.encode(Codec.INT, 3L), real, "argument[0]")));
        assertTrue(refusal(() -> data.conform(data.encode(Codec.of(Urgency.class), Urgency.PAGE_NOW), nominal(id('8')), "argument[0]"))
                .contains("expected a incident value"));
        Value texts = data.encode(Codec.list(Codec.TEXT), List.of("ok", "ok"));
        assertEquals("argument[0][0]: expected an integer", refusal(() -> data.conform(texts, nominal(id('6'), integer), "argument[0]")));
        BoundaryType unit = new BoundaryType.Tuple(List.of());
        data.conform(data.encode(Codec.UNIT, null), unit, "unit");
        assertNull(data.decode(Codec.UNIT, Value.UNIT));
        assertThrows(JacquardException.class, () -> data.conform(data.encode(Codec.tuple(Codec.INT, Codec.INT), List.<Object>of(1L, 2L)), unit, "unit"));
        // A record with an unknown constructor is refused at its location.
        record Unknown(long field) {}
        assertEquals("value: unknown constructor Unknown", refusal(() -> data.encode(Codec.record(Unknown.class), new Unknown(1))));
        // A record over a builtin constructor is refused, as Dynamic is.
        record Wrapped(long inner) {}
        assertTrue(refusal(() -> data.encode(Codec.record(Wrapped.class, "Some"), new Wrapped(1))).contains("native form of builtin constructor Some"));
        // null is no value; Optional and UNIT are.
        assertTrue(refusal(() -> data.encode(Codec.TEXT, null)).startsWith("value: null has no Jacquard value"));
        assertTrue(refusal(() -> data.encode(Codec.TEXT, "\ud800")).contains("lone surrogate"));
    }

    @Test
    void optionsEnumsUnionsAndDynamicValues() throws Exception {
        Data data = fixture(Limits.HARD);
        Triage routed = new Routed("r", Urgency.PAGE_NOW);
        Value value = data.encode(TRIAGE, routed);
        assertEquals(new Value.Constructor(numbered(3), List.of(new Value.Text("r"), new Value.Constructor(numbered(1), List.of()))), value);
        assertEquals(routed, data.decode(TRIAGE, value));
        Value invalid = data.encode(TRIAGE, new InvalidIncident("x"));
        assertEquals(new InvalidIncident("x"), data.decode(TRIAGE, invalid));
        assertTrue(assertThrows(JacquardException.class, () -> data.decode(Codec.of(Urgency.class), invalid)).getMessage()
                .contains("unknown Urgency case"));
        Value shortRouted = new Value.Constructor(numbered(3), List.of(new Value.Text("r")));
        assertTrue(assertThrows(JacquardException.class, () -> data.decode(TRIAGE, shortRouted)).getMessage()
                .contains("wrong constructor or field count"));
        BoundaryType option = nominal(id('7'), nominal(id('7'), nominal(id('1'))));
        Codec<Optional<Optional<Long>>> nested = Codec.optional(Codec.optional(Codec.INT));
        List<Optional<Optional<Long>>> cases = List.of(Optional.empty(), Optional.of(Optional.empty()), Optional.of(Optional.of(3L)));
        List<Dynamic> dynamics = List.of(Dynamic.none(), Dynamic.some(Dynamic.none()), Dynamic.some(Dynamic.some(Dynamic.of(3))));
        for (int i = 0; i < cases.size(); i++) {
            Value encoded = data.encode(nested, cases.get(i));
            data.conform(encoded, option, "option");
            assertEquals(cases.get(i), data.decode(nested, encoded));
            assertEquals(dynamics.get(i), data.decode(Codec.DYNAMIC, encoded));
            assertEquals(encoded, data.encode(Codec.DYNAMIC, dynamics.get(i)));
        }
        Dynamic watch = data.make("WatchOnly");
        assertEquals(Dynamic.variant("watch-only"), watch);
        assertEquals(Urgency.WATCH_ONLY, data.decode(Codec.of(Urgency.class), data.encode(Codec.DYNAMIC, watch)));
        assertEquals(JacquardException.Kind.BINDING, assertThrows(JacquardException.class, () -> data.make("Missing")).kind());
        assertThrows(JacquardException.class, () -> data.encode(Codec.DYNAMIC, Dynamic.variant("Some", Dynamic.of(1))), "a builtin constructor by name");
        Dynamic dynamic = Dynamic.list(Dynamic.tuple(Dynamic.of(true), Dynamic.of("x"), Dynamic.of(2.5)), new Dynamic.HashValue(id('5')));
        assertEquals(dynamic, data.decode(Codec.DYNAMIC, data.encode(Codec.DYNAMIC, dynamic)));
        assertTrue(assertThrows(JacquardException.class, () -> data.decode(Codec.DYNAMIC, new Value.Constructor(id('9'), List.of())))
                .getMessage().contains("unmapped constructor"));
        List<Void> units = Collections.nCopies(40, null);
        assertTrue(refusal(() -> data.encode(Codec.list(Codec.UNIT), units)).contains("JSON depth"), "a list deeper than the JSON depth");
        assertEquals(id('9'), data.identity("Urgency"));
        assertThrows(JacquardException.class, () -> data.identity("missing"));
    }

    @Test
    void explicitAndDerivedCodecsAgree() throws Exception {
        Data data = fixture(Limits.HARD);
        Codec<Incident> explicit = Codec.record(Incident.class, "incident", Codec.TEXT, Codec.INT, Codec.optional(Codec.TEXT));
        Incident incident = new Incident("i", 3, Optional.of("ops"));
        assertEquals(data.encode(INCIDENT, incident), data.encode(explicit, incident));
        assertEquals(data.encode(INCIDENT, incident), data.encode(Codecs.RUNTIME, incident));
        Codec<Urgency> named = Codec.enumeration(Urgency.class, Map.of(Urgency.PAGE_NOW, "PageNow", Urgency.WATCH_ONLY, "WatchOnly"));
        assertEquals(data.encode(Codec.of(Urgency.class), Urgency.WATCH_ONLY), data.encode(named, Urgency.WATCH_ONLY));
        assertThrows(IllegalArgumentException.class, () -> Codec.enumeration(Urgency.class, Map.of(Urgency.PAGE_NOW, "PageNow")));
        Codec<Triage> cases = Codec.union(Triage.class, List.of(Codec.record(Routed.class), Codec.record(InvalidIncident.class)));
        assertEquals(new Routed("a", Urgency.PAGE_NOW), data.decode(cases, data.encode(TRIAGE, new Routed("a", Urgency.PAGE_NOW))));
        assertThrows(IllegalArgumentException.class, () -> Codec.record(Incident.class, "incident", Codec.TEXT));
        assertThrows(IllegalArgumentException.class, () -> Codec.of(Object.class));
        assertThrows(IllegalArgumentException.class, () -> Codec.union(Object.class));
        record Pair(long left, String right) {}
        Codec<Pair> pair = Codec.tuple(Pair.class);
        Value tuple = data.encode(pair, new Pair(1, "x"));
        assertEquals(new Value.Tuple(List.of(new Value.Int(1), new Value.Text("x"))), tuple);
        assertEquals(new Pair(1, "x"), data.decode(pair, tuple));
        assertEquals(List.of(1L, "x"), data.decode(Codec.tuple(Codec.INT, Codec.TEXT), tuple));
        assertThrows(JacquardException.class, () -> data.decode(Codec.tuple(Codec.INT), tuple));
    }

    /** A recursive declaration derives without looping, through a placeholder. */
    record Tree(String label, List<Tree> children) {}

    @Test
    void recursiveRecordsDerive() throws Exception {
        Map<String, Hash> names = Map.of("list", id('6'), "text", id('3'));
        Map<String, Hash> constructors = Map.of("cons", id('e'), "nil", id('f'), "tree", id('0'));
        Data data = new Data(names, constructors, List.of(), Limits.HARD);
        Tree tree = new Tree("root", List.of(new Tree("leaf", List.of()), new Tree("branch", List.of(new Tree("leaf", List.of())))));
        assertEquals(tree, data.decode(Codec.of(Tree.class), data.encode(Codec.of(Tree.class), tree)));
    }

    @Test
    void argumentsEncodeByRuntimeClassOrExplicitCodec() throws Exception {
        Data data = fixture(Limits.HARD);
        Encoder.Budget budget = new Encoder.Budget();
        Value implicit = Encoder.root(data, "f.argument[0]", budget).encode(Codecs.RUNTIME, List.of(35, "x", Optional.of(true)));
        assertTrue(implicit instanceof Value.Constructor);
        assertEquals(data.encode(Codec.INT, 35L), data.encode(Codecs.RUNTIME, 35));
        assertEquals(data.encode(Codec.optional(Codec.BOOL), Optional.of(true)), data.encode(Codecs.RUNTIME, Optional.of(true)));
        assertEquals(data.encode(Codec.list(INCIDENT), List.of()), data.encode(Codecs.RUNTIME, Codec.list(INCIDENT).arg(List.of())));
        assertEquals("f.argument[3]: integer is outside Core's 63-bit Int range",
                refusal(() -> Encoder.root(data, "f.argument[3]").encode(Codecs.RUNTIME, Long.MAX_VALUE)));
        assertEquals("value: no Jacquard mapping for java.lang.Object; pass codec.arg(value)", refusal(() -> data.encode(Codecs.RUNTIME, new Object())));
        assertTrue(refusal(() -> data.encode(Codecs.RUNTIME, null)).startsWith("value: null has no Jacquard value"));
        assertEquals("value[1]: no Jacquard mapping for java.lang.Object; pass codec.arg(value)",
                refusal(() -> data.encode(Codecs.RUNTIME, List.of("a", new Object()))));
    }

    @Test
    void selectedLimitsBoundMappedValues() throws Exception {
        Limits limits = Limits.HARD.with("max_collection_items", 2).with("max_text_bytes", 3);
        Data data = fixture(limits);
        assertTrue(refusal(() -> data.encode(Codec.list(Codec.INT), List.of(1L, 2L, 3L))).contains("max_collection_items"));
        assertTrue(refusal(() -> data.encode(Codec.TEXT, "four")).contains("max_text_bytes"));
        Value longList = fixture(Limits.HARD).encode(Codec.list(Codec.INT), List.of(1L, 2L, 3L));
        assertTrue(assertThrows(JacquardException.class, () -> data.decode(Codec.list(Codec.INT), longList)).getMessage().contains("selected limit"));
        assertEquals("value.id: text exceeds max_text_bytes", refusal(() -> data.encode(INCIDENT, new Incident("abcd", 1, Optional.empty()))));
    }

    /**
     * A small Java value can denote a huge one: three levels of 64
     * references to one tuple are 64^3 integers. Encoding stops at the 4097th
     * node, so it costs no more than one frame; without the node budget it
     * builds all 266,305 nodes and returns them. (At 1024 references per
     * level the same Java value denotes 2^30 integers.)
     */
    @Test
    void sharedValuesAreBoundedByTheNodeBudget() {
        Data data = fixture(Limits.HARD);
        Dynamic leaf = new Dynamic.Tuple(Collections.nCopies(64, Dynamic.of(1)));
        Dynamic middle = new Dynamic.Tuple(Collections.nCopies(64, leaf));
        Dynamic top = new Dynamic.Tuple(Collections.nCopies(64, middle));
        // Sixty-three tuples of 65 nodes each, then the first integer of the next.
        assertEquals("value[0][63][1]: value exceeds max_value_nodes; use smaller independent batches",
                refusal(() -> data.encode(Codec.DYNAMIC, top)));
        Value rawLeaf = new Value.Tuple(Collections.nCopies(64, new Value.Int(1)));
        Value rawTop = new Value.Tuple(Collections.nCopies(64, new Value.Tuple(Collections.nCopies(64, rawLeaf))));
        assertEquals("value: value exceeds max_value_nodes; use smaller independent batches", refusal(() -> data.encode(Codec.VALUE, rawTop)));
        // Decoding a supplied value is measured first, within the same bounds.
        JacquardException decoded = assertThrows(JacquardException.class, () -> data.decode(Codec.DYNAMIC, rawTop));
        assertEquals("value exceeds max_value_nodes", decoded.getMessage());
        Value deep = new Value.Int(1);
        for (int i = 0; i < 100_000; i++) {
            deep = new Value.Tuple(List.of(deep));
        }
        Value nested = deep;
        assertEquals("value exceeds the v0 JSON depth", assertThrows(JacquardException.class, () -> data.decode(Codec.DYNAMIC, nested)).getMessage());
    }

    /**
     * Texts are charged against the frame as they are encoded: four
     * references to one maximal text fill it, and the fifth is refused before
     * it is scanned. Without the charge, each of the 1000 references would be
     * scanned in full.
     */
    @Test
    void repeatedTextsAreChargedAgainstTheFrame() {
        Data data = fixture(Limits.HARD);
        Dynamic text = Dynamic.of("x".repeat(Limits.HARD.maxTextBytes()));
        Dynamic many = new Dynamic.Tuple(Collections.nCopies(1000, text));
        assertEquals("value[4]: value exceeds max_frame_bytes; use smaller independent batches", refusal(() -> data.encode(Codec.DYNAMIC, many)));
        Value rawText = new Value.Text("x".repeat(Limits.HARD.maxTextBytes()));
        Value raw = new Value.Tuple(Collections.nCopies(1000, rawText));
        assertEquals("value: value exceeds max_frame_bytes; use smaller independent batches", refusal(() -> data.encode(Codec.VALUE, raw)));
    }

    /** A text too long in chars is refused before it is scanned: the lone surrogate at its end is never reached. */
    @Test
    void anOverlongTextIsRefusedBeforeItIsScanned() {
        Data data = fixture(Limits.HARD);
        String text = "x".repeat(Limits.HARD.maxTextBytes()) + "\ud800";
        assertEquals("value: text exceeds max_text_bytes", refusal(() -> data.encode(Codec.TEXT, text)));
    }

    /** A list over the collection limit is refused before any element is read. */
    @Test
    void anOversizedListIsRefusedBeforeItsElementsAreRead() {
        Data data = fixture(Limits.HARD);
        List<Long> huge = new AbstractList<>() {
            @Override
            public Long get(int index) {
                throw new AssertionError("element " + index + " was read");
            }

            @Override
            public int size() {
                return 1 << 30;
            }
        };
        assertEquals("value: list exceeds max_collection_items", refusal(() -> data.encode(Codec.list(Codec.INT), huge)));
        assertEquals("value: list exceeds max_collection_items", refusal(() -> data.encode(Codecs.RUNTIME, huge)));
    }
}
