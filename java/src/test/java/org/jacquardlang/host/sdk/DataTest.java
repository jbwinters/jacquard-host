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
        // A record's fields count against max_collection_items too, so this one needs three.
        Data fields = fixture(Limits.HARD.with("max_collection_items", 3).with("max_text_bytes", 3));
        assertEquals("value.id: text exceeds max_text_bytes", refusal(() -> fields.encode(INCIDENT, new Incident("abcd", 1, Optional.empty()))));
        assertEquals("value: constructor exceeds max_collection_items", refusal(() -> data.encode(INCIDENT, new Incident("abc", 1, Optional.empty()))));
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
    /** A raw text is charged by its UTF-8 bytes: three two-byte chars exceed a four-byte limit. */
    @Test
    void aRawTextIsChargedByItsUtf8Bytes() throws Exception {
        Data data = fixture(Limits.HARD.with("max_text_bytes", 4));
        assertEquals(new Value.Text("\u00e9\u00e9"), data.encode(Codec.VALUE, new Value.Text("\u00e9\u00e9")));
        assertEquals("value: text exceeds max_text_bytes", refusal(() -> data.encode(Codec.VALUE, new Value.Text("\u00e9\u00e9\u00e9"))));
        assertEquals("value: text contains a lone surrogate, which has no Jacquard encoding",
                refusal(() -> data.encode(Codec.VALUE, new Value.Text("\ud800"))));
    }

    /** A tuple over the collection limit is refused before any item is encoded. */
    @Test
    void anOversizedTupleIsRefusedBeforeItsItemsAreEncoded() {
        Data data = fixture(Limits.HARD.with("max_collection_items", 2));
        java.util.concurrent.atomic.AtomicInteger encodes = new java.util.concurrent.atomic.AtomicInteger();
        Codec<Long> counted = new Codec<>() {
            @Override
            public Value encode(Encoder encoder, Long value) throws JacquardException {
                encodes.incrementAndGet();
                return Codec.INT.encode(encoder, value);
            }

            @Override
            public Long decode(Decoder decoder, Value value) throws JacquardException {
                return Codec.INT.decode(decoder, value);
            }
        };
        Codec<List<Object>> triple = Codec.tuple(counted, counted, counted);
        assertEquals("value: tuple exceeds max_collection_items", refusal(() -> data.encode(triple, List.of(1L, 2L, 3L))));
        assertEquals(0, encodes.get());
    }

    /** A record whose accessors count their reads. */
    record Counted(long a, long b, long c) {
        static final java.util.concurrent.atomic.AtomicInteger READS = new java.util.concurrent.atomic.AtomicInteger();

        @Override
        public long a() {
            READS.incrementAndGet();
            return a;
        }

        @Override
        public long b() {
            READS.incrementAndGet();
            return b;
        }

        @Override
        public long c() {
            READS.incrementAndGet();
            return c;
        }
    }

    /** A record crossing as a tuple over the collection limit is refused before any component is read. */
    @Test
    void anOversizedRecordTupleIsRefusedBeforeItsComponentsAreRead() throws Exception {
        Counted.READS.set(0);
        Codec<Counted> triple = Codec.tuple(Counted.class);
        assertEquals("value: tuple exceeds max_collection_items",
                refusal(() -> fixture(Limits.HARD.with("max_collection_items", 2)).encode(triple, new Counted(1, 2, 3))));
        assertEquals(0, Counted.READS.get());
        fixture(Limits.HARD.with("max_collection_items", 3)).encode(triple, new Counted(1, 2, 3));
        assertEquals(3, Counted.READS.get());
    }

    /**
     * A constructor name longer than any the SDK maps is neither expanded
     * nor copied whole into a message: it names nothing, and the refusal
     * quotes its first 64 chars and its length.
     */
    @Test
    void aHugeConstructorNameIsRefusedWithoutBeingExpanded() throws Exception {
        String huge = "X".repeat(1_000_000);
        assertTrue(huge == Identities.displayToStore(huge), "the name was converted");
        assertEquals("value: unknown constructor " + "X".repeat(64) + "... (1000000 chars)",
                refusal(() -> fixture(Limits.HARD).encode(Codec.DYNAMIC, Dynamic.variant(huge))));
        // A name within the limit is still converted.
        assertEquals("job-ticket", Identities.displayToStore("JobTicket"));
    }

    record Empty() {}

    record Throwing(String id, long severity, Optional<String> owner) {
        @Override
        public long severity() {
            throw new IllegalStateException("X".repeat(1_000_000));
        }
    }

    record Refusing(String id, long severity, Optional<String> owner) {
        Refusing {
            throw new IllegalArgumentException("X".repeat(1_000_000));
        }
    }

    /**
     * Messages quote caller text as an excerpt, and no message is kept past
     * 8,192 chars: a result codec naming a constructor of a million chars,
     * and a codec that throws with a million-char message, each give a short
     * refusal (before the fix the whole name or message was copied into each
     * wrapper, and a 50-million-char name exhausted a 128 MiB heap).
     */
    @Test
    void messagesQuoteCallerTextAsAnExcerpt() throws Exception {
        String huge = "X".repeat(1_000_000);
        Data data = fixture(Limits.HARD);
        Value unit = data.encode(Codec.UNIT, null);
        JacquardException decode = assertThrows(JacquardException.class, () -> data.decode(Codec.record(Empty.class, huge), unit));
        assertTrue(decode.getMessage().length() < 200, decode.getMessage().length() + " chars");
        assertTrue(decode.getMessage().contains("... (1000000 chars)"), decode.getMessage().substring(0, 120));
        Codec<Long> throwing = new Codec<>() {
            @Override
            public Value encode(Encoder encoder, Long value) {
                throw new IllegalStateException(huge);
            }

            @Override
            public Long decode(Decoder decoder, Value value) {
                return 0L;
            }
        };
        JacquardException encode = assertThrows(JacquardException.class, () -> data.encode(throwing, 1L));
        assertEquals(JacquardException.Kind.INVALID_ARGUMENT, encode.kind());
        assertEquals("value: its codec failed: java.lang.IllegalStateException: " + "X".repeat(64) + "... (1000000 chars)", encode.getMessage());
        assertTrue(encode.getCause() instanceof IllegalStateException, "the codec's exception was not kept");
        // Decoding: a codec that throws, and a record constructor that refuses its fields.
        Codec<Long> throwingDecode = new Codec<>() {
            @Override
            public Value encode(Encoder encoder, Long value) throws JacquardException {
                return Codec.INT.encode(encoder, value);
            }

            @Override
            public Long decode(Decoder decoder, Value value) {
                throw new IllegalStateException(huge);
            }
        };
        JacquardException decoded = assertThrows(JacquardException.class, () -> data.decode(throwingDecode, data.encode(Codec.INT, 1L)));
        assertEquals(JacquardException.Kind.BINDING, decoded.kind());
        assertEquals("value: its codec failed: java.lang.IllegalStateException: " + "X".repeat(64) + "... (1000000 chars)", decoded.getMessage());
        assertTrue(decoded.getCause() instanceof IllegalStateException, "the codec's exception was not kept");
        Value incident = data.encode(INCIDENT, new Incident("ok", 1, Optional.empty()));
        Codec<Refusing> refusing = Codec.record(Refusing.class, "incident", Codec.TEXT, Codec.INT, Codec.optional(Codec.TEXT));
        JacquardException refused = assertThrows(JacquardException.class, () -> data.decode(refusing, incident));
        assertEquals("Refusing refused the decoded fields: java.lang.IllegalArgumentException: " + "X".repeat(64) + "... (1000000 chars)",
                refused.getMessage());
        assertTrue(refused.getCause() instanceof IllegalArgumentException, "the constructor's refusal was not kept");
        // A record accessor that throws while encoding.
        JacquardException accessor = assertThrows(JacquardException.class, () -> data.encode(Codec.record(Throwing.class, "incident"), new Throwing("ok", 1, Optional.empty())));
        assertEquals("value: cannot read Throwing.severity: java.lang.IllegalStateException: " + "X".repeat(64) + "... (1000000 chars)", accessor.getMessage());
        assertTrue(accessor.getCause() instanceof IllegalStateException, "the accessor's exception is not the cause");
        // A caller's field name in a path.
        Codec<Long> named = new Codec<>() {
            @Override
            public Value encode(Encoder encoder, Long value) throws JacquardException {
                return encoder.field(huge, Codec.TEXT, null);
            }

            @Override
            public Long decode(Decoder decoder, Value value) {
                return 0L;
            }
        };
        String path = assertThrows(JacquardException.class, () -> data.encode(named, 1L)).getMessage();
        assertTrue(path.startsWith("value." + "X".repeat(64) + "... (1000000 chars): null has no Jacquard value"), path.substring(0, 100));
        String bounded = JacquardException.binding(huge).getMessage();
        assertEquals(JacquardException.MESSAGE_LIMIT, bounded.length(), "the cap is exact, its suffix included");
        assertTrue(bounded.endsWith("X... (1000000 chars)"), bounded.substring(bounded.length() - 40));
        // A caller's message through the public helpers is bounded before it is joined to a path.
        Codec<Long> invalidating = new Codec<>() {
            @Override
            public Value encode(Encoder encoder, Long value) throws JacquardException {
                throw encoder.invalid(huge);
            }

            @Override
            public Long decode(Decoder decoder, Value value) throws JacquardException {
                throw decoder.mapping(huge);
            }
        };
        String invalid = assertThrows(JacquardException.class, () -> data.encode(invalidating, 1L)).getMessage();
        assertTrue(invalid.length() <= JacquardException.MESSAGE_LIMIT, invalid.length() + " chars");
        assertTrue(invalid.startsWith("value: XXXX"), invalid.substring(0, 20));
        // Joined to its path once, the whole length noted: the path's 7 chars and the million.
        assertTrue(invalid.endsWith("X... (1000007 chars)"), invalid.substring(invalid.length() - 40));
        String mapping = assertThrows(JacquardException.class, () -> data.decode(invalidating, data.encode(Codec.INT, 1L))).getMessage();
        assertEquals(JacquardException.MESSAGE_LIMIT, mapping.length());
        assertTrue(mapping.endsWith("X... (1000000 chars)"), mapping.substring(mapping.length() - 40));
        // A note larger than any string is not the SDK's, so it is not trusted.
        String forged = "Y".repeat(JacquardException.MESSAGE_LIMIT - 31) + "... (9223372036854775807 chars)";
        assertEquals(JacquardException.MESSAGE_LIMIT, forged.length());
        String unforged = JacquardException.joined("p: ", forged);
        assertTrue(unforged.endsWith("... (" + (forged.length() + 3) + " chars)"), unforged.substring(unforged.length() - 40));
        // A null message is joined as "null", not dereferenced.
        assertEquals("p: null", JacquardException.joined("p: ", null));
        // A cut that dropped a high surrogate at the boundary is still recognised as cut.
        // The suffix "... (1008173 chars)" is 19 chars, so the cut lands at 8,173, inside the emoji.
        String emoji = "X".repeat(JacquardException.MESSAGE_LIMIT - 20) + "\uD83D\uDE00" + "X".repeat(1_000_000);
        String cut = JacquardException.bounded(emoji);
        assertEquals(JacquardException.MESSAGE_LIMIT - 1, cut.length(), "the cut did not land on the surrogate");
        String recut = JacquardException.joined("batch 1: ", cut);
        assertTrue(recut.endsWith("... (" + (emoji.length() + 9) + " chars)"), recut.substring(recut.length() - 30));
        // A cut message given a further prefix keeps its noted length, plus the prefix.
        String again = JacquardException.joined("batch 1: ", mapping);
        assertEquals(JacquardException.MESSAGE_LIMIT, again.length());
        assertTrue(again.startsWith("batch 1: XXX") && again.endsWith("X... (1000009 chars)"), again.substring(again.length() - 40));
    }

    record One() {}

    sealed interface Shape permits Circle, Square {}

    record Circle() implements Shape {}

    record Square() implements Shape {}

    /** An explicit union whose first case is a narrower union: encoding asks the flattened cases in decoding's order, so both agree. */
    @Test
    void encodingAndDecodingAgreeOnExplicitNestedUnions() throws Exception {
        Data data = fixture(Limits.HARD);
        Codec<Shape> circle = Codec.union(Shape.class, List.of(Codec.record(Circle.class, "routed")));
        Codec<Shape> outer = Codec.union(Shape.class, List.of(circle, Codec.record(Square.class, "invalid-incident")));
        Value square = data.encode(outer, new Square());
        assertEquals(new Square(), data.decode(outer, square));
        assertEquals(new Circle(), data.decode(outer, data.encode(outer, new Circle())));
    }

    /** A List or Optional nesting deeper than any value is refused with IllegalArgumentException, also for a type that names itself. */
    @Test
    void deepAndSelfReferentialGenericTypesAreRefused() throws Exception {
        java.lang.reflect.Type type = Long.class;
        for (int i = 0; i < 200_000; i++) {
            type = listOf(type);
        }
        java.lang.reflect.Type deep = type;
        assertTrue(assertThrows(IllegalArgumentException.class, () -> Codec.of(deep)).getMessage().contains("deeper than 32 levels"));
        java.lang.reflect.ParameterizedType[] self = new java.lang.reflect.ParameterizedType[1];
        self[0] = new java.lang.reflect.ParameterizedType() {
            @Override
            public java.lang.reflect.Type[] getActualTypeArguments() {
                return new java.lang.reflect.Type[] {self[0]};
            }

            @Override
            public java.lang.reflect.Type getRawType() {
                return List.class;
            }

            @Override
            public java.lang.reflect.Type getOwnerType() {
                return null;
            }
        };
        assertThrows(IllegalArgumentException.class, () -> Codec.of(self[0]));
        java.lang.reflect.Type allowed = Long.class;
        for (int i = 0; i < 32; i++) {
            allowed = listOf(allowed);
        }
        assertTrue(Codec.of(allowed) != null);
    }

    private static java.lang.reflect.ParameterizedType listOf(java.lang.reflect.Type item) {
        return new java.lang.reflect.ParameterizedType() {
            @Override
            public java.lang.reflect.Type[] getActualTypeArguments() {
                return new java.lang.reflect.Type[] {item};
            }

            @Override
            public java.lang.reflect.Type getRawType() {
                return List.class;
            }

            @Override
            public java.lang.reflect.Type getOwnerType() {
                return null;
            }
        };
    }

    /**
     * Nested unions are flattened when built, so decoding asks each distinct
     * case once: 64 layers of a two-case union over one record decode, or are
     * refused, at once (before the fix the work doubled with each layer, 4.2 s
     * at 30 layers, so 64 would not finish).
     */
    @Test
    void nestedUnionsDecodeInTimeLinearInTheirCases() throws Exception {
        Data data = fixture(Limits.HARD);
        Codec<One> codec = Codec.record(One.class, "routed");
        for (int i = 0; i < 64; i++) {
            codec = Codec.union(One.class, List.of(codec, codec));
        }
        Codec<One> nested = codec;
        Value notRouted = data.encode(Codec.INT, 1L);
        JacquardException refused = org.junit.jupiter.api.Assertions.assertTimeoutPreemptively(java.time.Duration.ofSeconds(10),
                () -> assertThrows(JacquardException.class, () -> data.decode(nested, notRouted)));
        assertEquals(JacquardException.Kind.BINDING, refused.kind());
        // Flattening walks the cases iteratively: 100,000 layers neither overflow the stack nor take long.
        Codec<One> deep = Codec.record(One.class, "routed");
        for (int i = 0; i < 100_000; i++) {
            deep = Codec.union(One.class, List.of(deep, deep));
        }
        Codec<One> deepest = deep;
        assertEquals(1, Codecs.unionCases(deepest));
        assertThrows(JacquardException.class, () -> data.decode(deepest, notRouted));
        // Encoding descends the layers in a loop too, so the same depth encodes and round-trips.
        Value encoded = data.encode(deepest, new One());
        assertEquals(new One(), data.decode(deepest, encoded));
    }

    /** A Dynamic value over the hard limit is refused at construction, before its items are copied. */
    @Test
    void anOversizedDynamicIsRefusedBeforeItIsCopied() throws Exception {
        int limit = Limits.HARD.maxCollectionItems();
        java.util.List<Dynamic> many = Collections.nCopies(limit + 1, Dynamic.of(1L));
        assertThrows(IllegalArgumentException.class, () -> new Dynamic.List(many));
        assertThrows(IllegalArgumentException.class, () -> new Dynamic.Tuple(many));
        assertThrows(IllegalArgumentException.class, () -> new Dynamic.Variant("routed", many));
        JacquardException made = assertThrows(JacquardException.class, () -> fixture(Limits.HARD).make("routed", many.toArray(new Dynamic[0])));
        assertEquals(JacquardException.Kind.BINDING, made.kind());
        assertEquals(limit, new Dynamic.List(Collections.nCopies(limit, Dynamic.of(1L))).items().size());
    }

    /** A codec building a constructor by name has its fields counted before they are copied. */
    @Test
    void aConstructorWithTooManyFieldsIsRefusedBeforeItIsBuilt() throws Exception {
        Data data = fixture(Limits.HARD);
        Value one = data.encode(Codec.INT, 1L);
        Codec<Long> wide = new Codec<>() {
            @Override
            public Value encode(Encoder encoder, Long value) throws JacquardException {
                return encoder.constructor("routed", Collections.nCopies(value.intValue(), one));
            }

            @Override
            public Long decode(Decoder decoder, Value value) {
                return 0L;
            }
        };
        assertEquals("value: constructor exceeds max_collection_items", refusal(() -> data.encode(wide, 20_000_000L)));
        assertEquals("value: constructor exceeds max_collection_items", refusal(() -> data.encode(wide, (long) Limits.HARD.maxCollectionItems() + 1)));
    }

    @Test
    void anOversizedRecordIsRefusedBeforeItsComponentsAreRead() throws Exception {
        Hash counted = id('c');
        Codec<Counted> record = Codec.record(Counted.class);
        Counted.READS.set(0);
        Data narrow = new Data(Map.of("counted", counted), Map.of("counted", counted), List.of(), Limits.HARD.with("max_collection_items", 2));
        assertEquals("value: constructor exceeds max_collection_items", refusal(() -> narrow.encode(record, new Counted(1, 2, 3))));
        assertEquals(0, Counted.READS.get());
        Data wide = new Data(Map.of("counted", counted), Map.of("counted", counted), List.of(), Limits.HARD.with("max_collection_items", 3));
        wide.encode(record, new Counted(1, 2, 3));
        assertEquals(3, Counted.READS.get());
    }

    private static String nested(int depth) {
        // depth - 1 Options around an Int, printed as Core prints them (no
        // parentheses around the innermost Int): a type of the given depth.
        return "(" + "Option (".repeat(depth - 2) + "Option Int" + ")".repeat(depth - 2) + ") ->{} Int";
    }

    /**
     * A signature's types may nest 64 levels, the protocol's value depth, and
     * no deeper. Nesting is refused before the parser recurses past it, so
     * 5,000 parentheses, which overflowed the stack before, and 100,000 are
     * refused with a binding failure rather than an Error.
     */
    @Test
    void signatureNestingIsBounded() throws Exception {
        Map<String, Hash> names = Map.of("int", id('1'), "option", id('7'));
        assertEquals(64, Identities.SIGNATURE_DEPTH);
        BoundaryType type = Identities.parseSignature(nested(64), names).parameters().get(0);
        int depth = 1;
        while (type instanceof BoundaryType.Nominal nominal && !nominal.arguments().isEmpty()) {
            type = nominal.arguments().get(0);
            depth++;
        }
        assertEquals(64, depth);
        String deep = "signature nests types deeper than 64 levels, the protocol's value-depth limit (max_json_depth)";
        for (String text : List.of(nested(65), nested(2500), "(" + "(".repeat(5000) + "Int" + ")".repeat(5000) + ") ->{} Int")) {
            JacquardException error = assertThrows(JacquardException.class, () -> Identities.parseSignature(text, names));
            assertEquals(JacquardException.Kind.BINDING, error.kind());
            assertEquals(deep, error.getMessage());
        }
        JacquardException huge = assertThrows(JacquardException.class,
                () -> Identities.parseSignature("(" + "Option (".repeat(100_000) + "Int" + ")".repeat(100_000) + ") ->{} Int", names));
        assertEquals(JacquardException.Kind.BINDING, huge.kind());
        assertEquals("signature is longer than " + Identities.SIGNATURE_TOKENS + " tokens", huge.getMessage());
        // A wide signature within the depth is refused by its type count.
        String wide = "(" + "Int, ".repeat(Identities.SIGNATURE_NODES) + "Int) ->{} Int";
        assertEquals("signature names more than 4096 types",
                assertThrows(JacquardException.class, () -> Identities.parseSignature(wide, names)).getMessage());
    }
}
