package org.jacquardlang.host.protocol;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Map;
import java.util.Set;

import org.jacquardlang.host.Compat;
import org.jacquardlang.host.ProtocolException;
import org.jacquardlang.host.json.Json;
import org.jacquardlang.host.json.JsonCodec;

/**
 * Frozen v0 value and type codecs, the invoke frame, and Core frame
 * validation. Descriptors are checked in Core's order: kind, exact fields,
 * one node, identity, then counts.
 */
public final class Protocol {
    private Protocol() {}

    /** The only invocation identity a v0 host session uses. */
    public static final String INVOCATION_ID = "0000000000000000";

    private static final Set<String> TERMINALS = Set.of("ok", "diagnostic", "cancelled", "host_failure", "completion_unknown");
    private static final Set<String> CATEGORIES = Set.of(
            "ok", "unsupported_operation", "refused_authority", "outside_failure", "completion_unknown", "cancelled", "timeout",
            "host_shutdown");
    private static final Set<String> COMPLETIONS = Set.of("completed", "not_started", "failed", "unknown");

    static Map<String, Json> asObject(Json value) throws ProtocolException {
        Map<String, Json> fields = value.asObject();
        if (fields == null) {
            throw ProtocolException.malformed("expected a JSON object");
        }
        return fields;
    }

    /** The object's fields, refusing any key set other than exactly {@code keys}. */
    public static Map<String, Json> exact(Json value, String... keys) throws ProtocolException {
        Map<String, Json> fields = asObject(value);
        boolean ok = fields.size() == keys.length;
        for (String key : keys) {
            ok &= fields.containsKey(key);
        }
        if (!ok) {
            throw ProtocolException.malformed("expected exactly: " + String.join(", ", keys));
        }
        return fields;
    }

    private static String asStr(Json value) throws ProtocolException {
        String text = value.asString();
        if (text == null) {
            throw ProtocolException.malformed("expected a string");
        }
        return text;
    }

    private static List<Json> asArray(Json value) throws ProtocolException {
        List<Json> items = value.asArray();
        if (items == null) {
            throw ProtocolException.malformed("expected an array");
        }
        return items;
    }

    private static String kind(Json value) throws ProtocolException {
        Map<String, Json> fields = asObject(value);
        Json kind = fields.get("kind");
        if (kind == null || kind.asString() == null) {
            throw ProtocolException.malformed("a boundary descriptor needs one kind string");
        }
        return kind.asString();
    }

    private static List<Json> counted(Json value, Budget budget, boolean arguments) throws ProtocolException {
        List<Json> items = asArray(value);
        budget.count(items.size(), arguments);
        return items;
    }

    /** Parses one wire boundary type. */
    public static BoundaryType decodeType(Json value, Budget budget) throws ProtocolException {
        String kind = kind(value);
        switch (kind) {
            case "nominal" -> {
                Map<String, Json> fields = exact(value, "arguments", "identity", "kind");
                budget.consume();
                Hash identity = Hash.fromJson(fields.get("identity"));
                List<BoundaryType> arguments = new ArrayList<>();
                for (Json item : counted(fields.get("arguments"), budget, false)) {
                    arguments.add(decodeType(item, budget));
                }
                return new BoundaryType.Nominal(identity, arguments);
            }
            case "tuple" -> {
                Map<String, Json> fields = exact(value, "items", "kind");
                budget.consume();
                List<BoundaryType> items = new ArrayList<>();
                for (Json item : counted(fields.get("items"), budget, false)) {
                    items.add(decodeType(item, budget));
                }
                return new BoundaryType.Tuple(items);
            }
            default -> throw ProtocolException.unsupported("unsupported boundary type kind");
        }
    }

    /** Renders a boundary type, charging the budget. */
    public static Json encodeType(BoundaryType type, Budget budget) throws ProtocolException {
        return encodeType(type, budget, 1);
    }

    private static Json encodeType(BoundaryType type, Budget budget, int depth) throws ProtocolException {
        if (depth > budget.limits().maxJsonDepth()) {
            throw ProtocolException.exceeded("type nesting exceeds max_json_depth");
        }
        budget.consume();
        List<BoundaryType> children = switch (type) {
            case BoundaryType.Nominal nominal -> nominal.arguments();
            case BoundaryType.Tuple tuple -> tuple.items();
        };
        budget.count(children.size(), false);
        List<Json> items = new ArrayList<>();
        for (BoundaryType child : children) {
            items.add(encodeType(child, budget, depth + 2));
        }
        return switch (type) {
            case BoundaryType.Nominal nominal -> Json.of(Json.object(
                    "arguments", Json.of(items), "identity", Json.of(nominal.identity().hex()), "kind", Json.of("nominal")));
            case BoundaryType.Tuple ignored -> Json.of(Json.object("items", Json.of(items), "kind", Json.of("tuple")));
        };
    }

    private static long canonicalInt(String text) throws ProtocolException {
        String digits = text.startsWith("-") ? text.substring(1) : text;
        boolean canonical = !digits.isEmpty()
                && digits.chars().allMatch(c -> c >= '0' && c <= '9')
                && (digits.equals("0") || !digits.startsWith("0"))
                && !text.equals("-0")
                && text.length() <= 20;
        if (!canonical) {
            throw ProtocolException.malformed("Int must use canonical 63-bit decimal text");
        }
        long value;
        try {
            value = Long.parseLong(text);
        } catch (NumberFormatException error) {
            throw ProtocolException.malformed("Int is outside the 63-bit range");
        }
        if (value < Json.INT_MIN || value > Json.INT_MAX) {
            throw ProtocolException.malformed("Int is outside the 63-bit range");
        }
        return value;
    }

    /** Parses one wire boundary value. */
    public static Value decodeValue(Json value, Budget budget) throws ProtocolException {
        String kind = kind(value);
        switch (kind) {
            case "int", "text", "hash" -> {
                Map<String, Json> fields = exact(value, "kind", "value");
                budget.consume();
                switch (kind) {
                    case "int" -> {
                        return new Value.Int(canonicalInt(asStr(fields.get("value"))));
                    }
                    case "text" -> {
                        String text = asStr(fields.get("value"));
                        budget.text(text);
                        return new Value.Text(text);
                    }
                    default -> {
                        return new Value.HashValue(Hash.fromJson(fields.get("value")));
                    }
                }
            }
            case "real" -> {
                Map<String, Json> fields = exact(value, "bits", "kind");
                budget.consume();
                String bits = asStr(fields.get("bits"));
                if (!Hash.lowerHex(bits, 16)) {
                    throw ProtocolException.malformed("Real must have exactly 16 lowercase hexadecimal digits");
                }
                return new Value.Real(Long.parseUnsignedLong(bits, 16));
            }
            case "tuple" -> {
                Map<String, Json> fields = exact(value, "items", "kind");
                budget.consume();
                List<Value> items = new ArrayList<>();
                for (Json item : counted(fields.get("items"), budget, false)) {
                    items.add(decodeValue(item, budget));
                }
                return new Value.Tuple(items);
            }
            case "constructor" -> {
                Map<String, Json> fields = exact(value, "arguments", "identity", "kind");
                budget.consume();
                Hash identity = Hash.fromJson(fields.get("identity"));
                List<Value> arguments = new ArrayList<>();
                for (Json item : counted(fields.get("arguments"), budget, true)) {
                    arguments.add(decodeValue(item, budget));
                }
                return new Value.Constructor(identity, arguments);
            }
            default -> throw ProtocolException.unsupported("unsupported boundary value kind");
        }
    }

    /** Renders a boundary value, charging the budget. */
    public static Json encodeValue(Value value, Budget budget) throws ProtocolException {
        return encodeValue(value, budget, 1);
    }

    private static String hex16(long bits) {
        String hex = Long.toHexString(bits);
        return "0".repeat(16 - hex.length()) + hex;
    }

    private static Json encodeValue(Value value, Budget budget, int depth) throws ProtocolException {
        if (value == null) {
            throw ProtocolException.unsupported("null is not a boundary value");
        }
        if (depth > budget.limits().maxJsonDepth()) {
            throw ProtocolException.exceeded("value nesting exceeds max_json_depth");
        }
        budget.consume();
        return Json.of(switch (value) {
            case Value.Int number -> {
                if (number.value() < Json.INT_MIN || number.value() > Json.INT_MAX) {
                    throw ProtocolException.unsupported("Int is outside the 63-bit range");
                }
                yield Json.object("kind", Json.of("int"), "value", Json.of(Long.toString(number.value())));
            }
            case Value.Text text -> {
                budget.text(text.value());
                yield Json.object("kind", Json.of("text"), "value", Json.of(text.value()));
            }
            case Value.Real real -> Json.object("bits", Json.of(hex16(real.bits())), "kind", Json.of("real"));
            case Value.HashValue hash -> Json.object("kind", Json.of("hash"), "value", Json.of(hash.hash().hex()));
            case Value.Tuple tuple -> {
                budget.count(tuple.items().size(), false);
                List<Json> items = new ArrayList<>();
                for (Value item : tuple.items()) {
                    items.add(encodeValue(item, budget, depth + 2));
                }
                yield Json.object("items", Json.of(items), "kind", Json.of("tuple"));
            }
            case Value.Constructor constructor -> {
                budget.count(constructor.arguments().size(), true);
                List<Json> items = new ArrayList<>();
                for (Value item : constructor.arguments()) {
                    items.add(encodeValue(item, budget, depth + 2));
                }
                yield Json.object(
                        "arguments", Json.of(items), "identity", Json.of(constructor.identity().hex()), "kind", Json.of("constructor"));
            }
        });
    }

    private static int stringBytes(Json value) {
        return switch (value) {
            case Json.Str text -> JsonCodec.utf8Length(text.value());
            case Json.Arr array -> array.items().stream().mapToInt(Protocol::stringBytes).sum();
            case Json.Obj object -> object.fields().values().stream().mapToInt(Protocol::stringBytes).sum();
            default -> 0;
        };
    }

    private static List<Diagnostic> diagnostics(Json value, Limits limits) throws ProtocolException {
        List<Json> entries = asArray(value);
        if (entries.isEmpty()) {
            throw ProtocolException.malformed("diagnostics must be nonempty");
        }
        if (entries.size() > limits.maxDiagnostics() || stringBytes(value) > limits.maxDiagnosticBytes()) {
            throw ProtocolException.exceeded("diagnostics exceed selected limits");
        }
        List<Diagnostic> parsed = new ArrayList<>();
        for (Json entry : entries) {
            Map<String, Json> fields = asObject(entry);
            List<String> keys = new ArrayList<>(List.of("cause", "code", "domain", "next_step", "schema", "severity", "span", "summary"));
            if (fields.containsKey("contrast")) {
                keys.add("contrast");
            }
            exact(entry, keys.toArray(String[]::new));
            if (!Json.of("jacquard-diagnostic-v1").equals(fields.get("schema"))) {
                throw ProtocolException.malformed("unknown diagnostic schema");
            }
            Json codeField = fields.get("code");
            String code = codeField instanceof Json.Null ? "" : asStr(codeField);
            if (!(fields.get("span") instanceof Json.Null)) {
                asObject(fields.get("span"));
            }
            Json contrast = fields.get("contrast");
            if (contrast != null && !(contrast instanceof Json.Null)) {
                Map<String, Json> pair = exact(contrast, "intended", "mistaken");
                asStr(pair.get("intended"));
                asStr(pair.get("mistaken"));
            }
            parsed.add(new Diagnostic(
                    code,
                    asStr(fields.get("domain")),
                    asStr(fields.get("severity")),
                    asStr(fields.get("summary")),
                    asStr(fields.get("cause")),
                    asStr(fields.get("next_step")),
                    fields));
        }
        return parsed;
    }

    private static List<Hash> sortedHashes(Json value, int limit) throws ProtocolException {
        List<Hash> hashes = new ArrayList<>();
        for (Json item : asArray(value)) {
            hashes.add(Hash.fromJson(item));
        }
        if (hashes.size() > limit) {
            throw ProtocolException.exceeded("effect set exceeds selected limits");
        }
        if (!strictlySorted(hashes)) {
            throw ProtocolException.malformed("effects must be a strictly sorted set");
        }
        return hashes;
    }

    private static boolean strictlySorted(List<Hash> hashes) {
        for (int i = 1; i < hashes.size(); i++) {
            if (hashes.get(i).compareTo(hashes.get(i - 1)) <= 0) {
                return false;
            }
        }
        return true;
    }

    private static Json hashList(List<Hash> hashes) {
        List<Json> items = new ArrayList<>();
        for (Hash hash : hashes) {
            items.add(Json.of(hash.hex()));
        }
        return Json.of(items);
    }

    /** Validates and renders the single v0 invoke frame. */
    public static Map<String, Json> invokeFrame(Hash target, Interface spec, List<Value> args, List<OperationEntry> operations,
            Limits limits) throws ProtocolException {
        if (args.size() != spec.parameters().size()) {
            throw ProtocolException.malformed("argument count differs from parameter count");
        }
        Budget budget = new Budget(limits);
        budget.count(args.size(), true);
        if (spec.effects().size() > limits.maxEffects()) {
            throw ProtocolException.exceeded("effect set exceeds selected limits");
        }
        if (!strictlySorted(spec.effects())) {
            throw ProtocolException.malformed("effects must be a strictly sorted set");
        }
        if (operations.size() > limits.maxOperations()) {
            throw ProtocolException.exceeded("registry exceeds max_operations");
        }
        List<OperationEntry> entries = new ArrayList<>(operations);
        Collections.sort(entries);
        List<Json> granted = new ArrayList<>();
        for (int index = 0; index < entries.size(); index++) {
            OperationEntry entry = entries.get(index);
            if (!spec.effects().contains(entry.effect())) {
                throw ProtocolException.malformed("operation effect is outside the interface row");
            }
            if (index > 0 && entries.get(index - 1).equals(entry)) {
                throw ProtocolException.malformed("duplicate operation entry");
            }
            granted.add(Json.of(Json.object(
                    "effect", Json.of(entry.effect().hex()), "mode", Json.of("once"), "operation", Json.of(entry.operation().hex()))));
        }
        List<Json> arguments = new ArrayList<>();
        for (Value arg : args) {
            arguments.add(encodeValue(arg, budget));
        }
        List<Json> parameters = new ArrayList<>();
        for (BoundaryType parameter : spec.parameters()) {
            parameters.add(encodeType(parameter, budget));
        }
        Json result = encodeType(spec.result(), budget);
        return Json.object(
                "arguments", Json.of(arguments),
                "capabilities", Json.of(Json.object("effects", hashList(spec.effects()), "operations", Json.of(granted))),
                "interface", Json.of(Json.object(
                        "effects", hashList(spec.effects()), "parameters", Json.of(parameters), "result", result)),
                "invocation_id", Json.of(INVOCATION_ID),
                "kind", Json.of("invoke"),
                "protocol", Json.of(Compat.PROTOCOL),
                "target", Json.of(Json.object("callable", Json.of(target.hex()), "kind", Json.of("store-term-v0"))));
    }

    private static void hashes(Map<String, Json> fields, String... keys) throws ProtocolException {
        for (String key : keys) {
            Hash.fromJson(fields.get(key));
        }
    }

    private record Evidence(Map<String, Json> evidence, String terminal) {}

    private static Evidence validateEvidence(Json value, Limits limits, Budget budget) throws ProtocolException {
        Map<String, Json> evidence = exact(value, "core", "host_observations");
        Map<String, Json> core = exact(evidence.get("core"),
                "capabilities", "effect_requests", "interface", "invocation_id", "schema", "target", "terminal");
        String terminal = core.get("terminal").asString();
        if (terminal == null || !TERMINALS.contains(terminal)) {
            throw ProtocolException.malformed("unknown core evidence schema or terminal");
        }
        if (!Json.of("jacquard-host-core-evidence-v0").equals(core.get("schema"))) {
            throw ProtocolException.malformed("unknown core evidence schema or terminal");
        }
        if (!Json.of(INVOCATION_ID).equals(core.get("invocation_id"))) {
            throw ProtocolException.order("wrong evidence invocation");
        }
        Map<String, Json> target = exact(core.get("target"), "callable", "kind");
        Hash.fromJson(target.get("callable"));
        if (!Json.of("store-term-v0").equals(target.get("kind"))) {
            throw ProtocolException.malformed("unknown target kind");
        }
        Map<String, Json> spec = exact(core.get("interface"), "effects", "parameters", "result");
        sortedHashes(spec.get("effects"), limits.maxEffects());
        List<Json> parameters = asArray(spec.get("parameters"));
        budget.count(parameters.size(), true);
        for (Json parameter : parameters) {
            decodeType(parameter, budget);
        }
        decodeType(spec.get("result"), budget);
        Map<String, Json> capabilities = exact(core.get("capabilities"), "effects", "operations");
        sortedHashes(capabilities.get("effects"), limits.maxEffects());
        List<Json> operations = asArray(capabilities.get("operations"));
        if (operations.size() > limits.maxOperations()) {
            throw ProtocolException.exceeded("too many evidence operations");
        }
        for (Json operation : operations) {
            Map<String, Json> entry = exact(operation, "effect", "mode", "operation");
            hashes(entry, "effect", "operation");
            if (!Json.of("once").equals(entry.get("mode"))) {
                throw ProtocolException.malformed("unknown operation mode");
            }
        }
        List<Json> requests = asArray(core.get("effect_requests"));
        if (requests.size() > limits.maxEffectRequests()) {
            throw ProtocolException.exceeded("too many evidence requests");
        }
        for (int index = 0; index < requests.size(); index++) {
            Map<String, Json> entry = exact(requests.get(index), "effect", "operation", "ordinal");
            hashes(entry, "effect", "operation");
            if (!Json.of(index + 1L).equals(entry.get("ordinal"))) {
                throw ProtocolException.malformed("evidence ordinals must be consecutive");
            }
        }
        Map<String, Json> observations = exact(evidence.get("host_observations"), "responses", "schema");
        if (!Json.of("jacquard-host-observations-v0").equals(observations.get("schema"))) {
            throw ProtocolException.malformed("unknown host observations schema");
        }
        for (Json response : asArray(observations.get("responses"))) {
            Map<String, Json> entry = exact(response, "category", "completion", "ordinal");
            String category = entry.get("category").asString();
            if (category == null || !CATEGORIES.contains(category)) {
                throw ProtocolException.malformed("unknown observation category");
            }
            String completion = entry.get("completion").asString();
            if (completion == null || !COMPLETIONS.contains(completion)) {
                throw ProtocolException.malformed("unknown observation completion");
            }
            Long ordinal = entry.get("ordinal").asInt();
            if (ordinal == null || ordinal < 1 || ordinal > requests.size()) {
                throw ProtocolException.malformed("invalid observation ordinal");
            }
        }
        return new Evidence(evidence, terminal);
    }

    private static void invocation(Map<String, Json> fields) throws ProtocolException {
        if (!asStr(fields.get("invocation_id")).equals(INVOCATION_ID)) {
            throw ProtocolException.order("frame names the wrong invocation");
        }
    }

    /** Validates one Core frame after {@code core_hello}. */
    public static CoreFrame parseCoreFrame(Map<String, Json> frame, Limits limits) throws ProtocolException {
        Json kindField = frame.get("kind");
        String kind = kindField == null || kindField.asString() == null ? "" : kindField.asString();
        if (kind.equals("core_hello")) {
            throw ProtocolException.order("core_hello may only be the first frame");
        }
        if (!Json.of(Compat.PROTOCOL).equals(frame.get("protocol"))) {
            throw ProtocolException.malformed("unknown protocol");
        }
        Json value = Json.of(frame);
        switch (kind) {
            case "fatal" -> {
                exact(value, "diagnostics", "kind", "protocol");
                return new Terminal.Fatal(diagnostics(frame.get("diagnostics"), limits), frame);
            }
            case "shutdown_ack" -> {
                exact(value, "kind", "protocol");
                return new Terminal.ShutdownAck(frame);
            }
            case "effect_request" -> {
                exact(value, "arguments", "effect", "invocation_id", "kind", "mode", "operation", "protocol", "request_id");
                invocation(frame);
                if (!Json.of("once").equals(frame.get("mode"))) {
                    throw ProtocolException.malformed("unknown operation mode");
                }
                String id = asStr(frame.get("request_id"));
                if (!Hash.lowerHex(id, 16)) {
                    throw ProtocolException.malformed("request_id must have 16 lowercase hexadecimal digits");
                }
                long ordinal = Long.parseUnsignedLong(id, 16);
                if (ordinal == 0) {
                    throw ProtocolException.order("request ordinals start at one");
                }
                if (Long.compareUnsigned(ordinal, Limits.MAX_SAFE_INTEGER) > 0) {
                    throw ProtocolException.order("request ordinal is not consecutive");
                }
                Budget budget = new Budget(limits);
                List<Json> items = counted(frame.get("arguments"), budget, true);
                Hash effect = Hash.fromJson(frame.get("effect"));
                Hash operation = Hash.fromJson(frame.get("operation"));
                List<Value> arguments = new ArrayList<>();
                for (Json item : items) {
                    arguments.add(decodeValue(item, budget));
                }
                return new EffectRequest(ordinal, effect, operation, arguments, frame);
            }
            case "outcome" -> {
                exact(value, "evidence", "invocation_id", "kind", "protocol", "result");
                invocation(frame);
                // Core encodes evidence types and the result value against one budget.
                Budget budget = new Budget(limits);
                Evidence evidence = validateEvidence(frame.get("evidence"), limits, budget);
                Map<String, Json> result = asObject(frame.get("result"));
                Json resultKind = result.get("kind");
                String name = resultKind == null ? null : resultKind.asString();
                if ("ok".equals(name)) {
                    exact(frame.get("result"), "kind", "value");
                    if (!evidence.terminal().equals("ok")) {
                        throw ProtocolException.malformed("ok result has a non-ok terminal");
                    }
                    Value decoded = decodeValue(result.get("value"), budget);
                    return new Terminal.Outcome(decoded, List.of(), evidence.terminal(), evidence.evidence(), frame);
                }
                if ("error".equals(name)) {
                    exact(frame.get("result"), "diagnostics", "kind");
                    if (evidence.terminal().equals("ok")) {
                        throw ProtocolException.malformed("error result has an ok terminal");
                    }
                    List<Diagnostic> parsed = diagnostics(result.get("diagnostics"), limits);
                    return new Terminal.Outcome(null, parsed, evidence.terminal(), evidence.evidence(), frame);
                }
                throw ProtocolException.malformed("unknown result kind");
            }
            default -> throw ProtocolException.malformed("unknown Core frame kind");
        }
    }
}
