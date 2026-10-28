package org.jacquardlang.host.sdk;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;

import org.jacquardlang.host.ProtocolException;
import org.jacquardlang.host.protocol.BoundaryType;
import org.jacquardlang.host.protocol.Hash;
import org.jacquardlang.host.protocol.Interface;

/** Public names and identities printed by Core's {@code hash} and {@code check --print-sigs} commands. */
final class Identities {
    private Identities() {}

    /** One public name and identity printed by {@code jacquard hash}. */
    record Entry(String name, Hash hash) {}

    /** Parses one {@code N:name hash} line, or returns null. */
    private static Map.Entry<String, Entry> hashLine(String line) {
        int colon = line.indexOf(':');
        if (colon <= 0) {
            return null;
        }
        String index = line.substring(0, colon);
        String rest = line.substring(colon + 1);
        int space = rest.indexOf(' ');
        if (space < 0) {
            return null;
        }
        String name = rest.substring(0, space);
        if (!index.chars().allMatch(c -> c >= '0' && c <= '9') || name.isEmpty() || name.codePoints().anyMatch(Character::isWhitespace)) {
            return null;
        }
        try {
            return Map.entry(index, new Entry(name, Hash.parse(rest.substring(space + 1))));
        } catch (ProtocolException error) {
            return null;
        }
    }

    /**
     * Parses {@code jacquard hash} output into declaration groups: a type or
     * effect identity followed by its constructors or operations, in Core's
     * printed order.
     */
    static List<List<Entry>> hashGroups(String output) throws JacquardException {
        LinkedHashMap<String, List<Entry>> groups = new LinkedHashMap<>();
        for (String line : output.split("\n", -1)) {
            Map.Entry<String, Entry> parsed = hashLine(line.strip());
            if (parsed != null) {
                groups.computeIfAbsent(parsed.getKey(), key -> new ArrayList<>()).add(parsed.getValue());
            }
        }
        if (groups.isEmpty()) {
            throw JacquardException.binding("Core hash command returned no public identities");
        }
        List<List<Entry>> result = new ArrayList<>();
        for (List<Entry> group : groups.values()) {
            result.add(List.copyOf(group));
        }
        return result;
    }

    /** Refuses a public name that denotes different identities in different declaration groups. */
    static void checkGroups(List<List<Entry>> groups) throws JacquardException {
        Map<String, Set<Hash>> seen = new HashMap<>();
        for (List<Entry> group : groups) {
            Map<String, Set<Hash>> current = new LinkedHashMap<>();
            for (Entry entry : group) {
                current.computeIfAbsent(entry.name(), key -> new HashSet<>()).add(entry.hash());
            }
            for (Map.Entry<String, Set<Hash>> names : current.entrySet()) {
                Set<Hash> previous = seen.computeIfAbsent(names.getKey(), key -> new HashSet<>());
                if (!previous.isEmpty() && !previous.containsAll(names.getValue())) {
                    throw JacquardException.binding("ambiguous public name " + names.getKey()
                            + " occurs in different declaration groups; use distinct names or explicit low-level identities");
                }
                previous.addAll(names.getValue());
            }
        }
    }

    /** Converts a display name such as {@code WorkToday} to its store spelling, {@code work-today}; lowercase names are unchanged. */
    static String displayToStore(String name) {
        if (name.isEmpty() || !Character.isUpperCase(name.codePointAt(0))) {
            return name;
        }
        StringBuilder out = new StringBuilder();
        for (int i = 0; i < name.length(); ) {
            int point = name.codePointAt(i);
            if (i > 0 && Character.isUpperCase(point)) {
                out.append('-');
            }
            out.append(new String(Character.toChars(point)).toLowerCase(java.util.Locale.ROOT));
            i += Character.charCount(point);
        }
        return out.toString();
    }

    /** The form used to look up effects and operations: store spelling, with {@code _} accepted for {@code -}. */
    static String normal(String name) {
        return displayToStore(name).replace('_', '-');
    }

    /**
     * The deepest type a signature may nest (a type with no arguments has
     * depth 1): the protocol's value-depth limit
     * ({@code max_json_depth}, 64), since no deeper value could be passed.
     */
    static final int SIGNATURE_DEPTH = 64;
    /** The most types one signature may name. */
    static final int SIGNATURE_NODES = 4096;
    /** The most tokens one signature may have, which bounds the parser's work before any type is counted. */
    static final int SIGNATURE_TOKENS = 4 * SIGNATURE_NODES;

    /** Tokens of a printed signature, at most a few per type, so the token count is bounded too. */
    private static List<String> tokens(String text) throws JacquardException {
        List<String> tokens = new ArrayList<>();
        int at = 0;
        int end = text.length();
        while (true) {
            while (at < end && Character.isWhitespace(text.charAt(at))) {
                at++;
            }
            if (at == end) {
                return tokens;
            }
            if (tokens.size() >= SIGNATURE_TOKENS) {
                throw JacquardException.binding("signature is longer than " + SIGNATURE_TOKENS + " tokens");
            }
            int length;
            char first = text.charAt(at);
            if (text.startsWith("->{", at)) {
                length = 3;
            } else if ("(),{}".indexOf(first) >= 0) {
                length = 1;
            } else if ((first >= 'a' && first <= 'z') || (first >= 'A' && first <= 'Z')) {
                length = 1;
                while (at + length < end) {
                    char c = text.charAt(at + length);
                    if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || ".?!-".indexOf(c) >= 0)) {
                        break;
                    }
                    length++;
                }
            } else {
                throw JacquardException.binding("unsupported signature near " + text.substring(at, Math.min(at + 12, end)));
            }
            tokens.add(text.substring(at, at + length));
            at += length;
        }
    }

    /**
     * Reads a closed v0 signature printed by {@code check --print-sigs}, such as
     * {@code (List Line, Int) ->{Inventory} Option Text}. Polymorphic and
     * effect-polymorphic signatures are refused, and so is one with more than
     * {@link #SIGNATURE_TOKENS} tokens, one whose types nest deeper than
     * {@link #SIGNATURE_DEPTH} levels, or one with more than
     * {@link #SIGNATURE_NODES} types. The nesting is refused before the parser
     * recurses past it, so no signature can exhaust the stack.
     */
    static Interface parseSignature(String text, Map<String, Hash> names) throws JacquardException {
        return new Parser(tokens(text), names).parse();
    }

    private static JacquardException tooDeep() {
        return JacquardException.binding("signature nests types deeper than " + SIGNATURE_DEPTH
                + " levels, the protocol's value-depth limit (max_json_depth)");
    }

    /** Refuses a type deeper than {@link #SIGNATURE_DEPTH} or, with {@code nodes}, more than {@link #SIGNATURE_NODES} types. */
    private static void measure(BoundaryType type, int level, int[] nodes) throws JacquardException {
        if (level > SIGNATURE_DEPTH) {
            throw tooDeep();
        }
        if (++nodes[0] > SIGNATURE_NODES) {
            throw JacquardException.binding("signature names more than " + SIGNATURE_NODES + " types");
        }
        List<BoundaryType> children = type instanceof BoundaryType.Nominal nominal ? nominal.arguments()
                : ((BoundaryType.Tuple) type).items();
        for (BoundaryType child : children) {
            measure(child, level + 1, nodes);
        }
    }

    private static final class Parser {
        private final List<String> tokens;
        private final Map<String, Hash> names;
        private int index;
        private int frames;

        Parser(List<String> tokens, Map<String, Hash> names) {
            this.tokens = tokens;
            this.names = names;
        }

        private String peek() {
            return index < tokens.size() ? tokens.get(index) : "";
        }

        private String take(String expected) throws JacquardException {
            String token = peek();
            if (token.isEmpty() || (!expected.isEmpty() && !token.equals(expected))) {
                throw JacquardException.binding("expected \"" + expected + "\", found \"" + token + "\"");
            }
            index++;
            return token;
        }

        private Hash identity(String display) throws JacquardException {
            Hash hash = names.get(displayToStore(display));
            if (hash == null) {
                hash = names.get(display);
            }
            if (hash == null) {
                throw JacquardException.binding("no identity for " + display);
            }
            return hash;
        }

        private List<BoundaryType> list(String close) throws JacquardException {
            List<BoundaryType> out = new ArrayList<>();
            if (peek().equals(close)) {
                index++;
                return out;
            }
            while (true) {
                out.add(type());
                if (peek().equals(",")) {
                    index++;
                    if (peek().equals(close)) {
                        index++;
                        return out;
                    }
                    continue;
                }
                take(close);
                return out;
            }
        }

        private BoundaryType atom() throws JacquardException {
            String token = take("");
            if (token.equals("(")) {
                List<BoundaryType> items = list(")");
                if (items.size() == 1 && !tokens.get(index - 2).equals(",")) {
                    return items.get(0);
                }
                return new BoundaryType.Tuple(items);
            }
            if (!Character.isLetter(token.codePointAt(0))) {
                throw JacquardException.binding("unsupported type \"" + token + "\"");
            }
            return new BoundaryType.Nominal(identity(token), List.of());
        }

        private BoundaryType type() throws JacquardException {
            // Each nested parse is inside a type that is one level shallower
            // (Core prints no redundant parentheses), so this bounds the
            // recursion before it goes further.
            if (++frames > SIGNATURE_DEPTH) {
                throw tooDeep();
            }
            BoundaryType head = atom();
            if (head instanceof BoundaryType.Nominal nominal) {
                List<BoundaryType> arguments = new ArrayList<>(nominal.arguments());
                while (!List.of("", ",", ")", "}", "->{").contains(peek())) {
                    arguments.add(atom());
                }
                head = new BoundaryType.Nominal(nominal.identity(), arguments);
            }
            frames--;
            return head;
        }

        Interface parse() throws JacquardException {
            take("(");
            List<BoundaryType> parameters = list(")");
            take("->{");
            List<Hash> effects = new ArrayList<>();
            if (!peek().equals("}")) {
                while (true) {
                    Hash identity = identity(take(""));
                    if (!effects.contains(identity)) {
                        effects.add(identity);
                    }
                    if (!peek().equals(",")) {
                        break;
                    }
                    index++;
                }
            }
            take("}");
            BoundaryType result = type();
            if (!peek().isEmpty()) {
                throw JacquardException.binding("trailing signature tokens");
            }
            effects.sort(null);
            int[] nodes = {0};
            for (BoundaryType parameter : parameters) {
                measure(parameter, 1, nodes);
            }
            measure(result, 1, nodes);
            return new Interface(parameters, result, effects);
        }
    }
}
