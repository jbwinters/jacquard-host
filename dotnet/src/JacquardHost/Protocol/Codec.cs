using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JacquardLang.Host.Protocol;

/// <summary>
/// Frozen v0 value and type codecs, the invoke frame, and Core frame
/// validation. Descriptors are checked in Core's order: kind, exact fields,
/// one node, identity, then counts.
/// </summary>
public static class Codec
{
    /// <summary>The only invocation identity a v0 host session uses.</summary>
    public const string InvocationId = "0000000000000000";

    private static readonly HashSet<string> Terminals = new(StringComparer.Ordinal) { "ok", "diagnostic", "cancelled", "host_failure", "completion_unknown" };

    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal)
    {
        "ok", "unsupported_operation", "refused_authority", "outside_failure", "completion_unknown", "cancelled", "timeout", "host_shutdown",
    };

    private static readonly HashSet<string> Completions = new(StringComparer.Ordinal) { "completed", "not_started", "failed", "unknown" };

    private static readonly string[] DiagnosticKeys = { "cause", "code", "domain", "next_step", "schema", "severity", "span", "summary" };

    private static readonly string[] DiagnosticKeysWithContrast = DiagnosticKeys.Append("contrast").ToArray();

    internal static JsonObject AsObject(Json? value) => value as JsonObject ?? throw ProtocolException.Malformed("expected a JSON object");

    /// <summary>The object's fields, refusing any key set other than exactly <paramref name="keys"/>.</summary>
    /// <exception cref="ProtocolException">E1601 for a non-object or another key set.</exception>
    public static JsonObject Exact(Json? value, params string[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        JsonObject fields = AsObject(value);
        bool ok = fields.Count == keys.Length;
        foreach (string key in keys)
        {
            ok &= fields.ContainsKey(key);
        }

        if (!ok)
        {
            throw ProtocolException.Malformed("expected exactly: " + string.Join(", ", keys));
        }

        return fields;
    }

    private static string AsStr(Json? value) => value?.AsString() ?? throw ProtocolException.Malformed("expected a string");

    private static IReadOnlyList<Json> AsArray(Json? value) => value?.AsArray() ?? throw ProtocolException.Malformed("expected an array");

    private static string Kind(Json value)
    {
        JsonObject fields = AsObject(value);
        return fields.Get("kind")?.AsString() ?? throw ProtocolException.Malformed("a boundary descriptor needs one kind string");
    }

    private static IReadOnlyList<Json> Counted(Json? value, Budget budget, bool arguments)
    {
        IReadOnlyList<Json> items = AsArray(value);
        budget.Count(items.Count, arguments);
        return items;
    }

    /// <summary>Parses one wire boundary type, charging <paramref name="budget"/>.</summary>
    /// <exception cref="ProtocolException">E1601, E1602, or E1604 as Core refuses the descriptor.</exception>
    public static BoundaryType DecodeType(Json value, Budget budget)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(budget);
        switch (Kind(value))
        {
            case "nominal":
            {
                JsonObject fields = Exact(value, "arguments", "identity", "kind");
                budget.Consume();
                Hash identity = Hash.FromJson(fields["identity"]);
                var arguments = new List<BoundaryType>();
                foreach (Json item in Counted(fields["arguments"], budget, false))
                {
                    arguments.Add(DecodeType(item, budget));
                }

                return new NominalType(identity, arguments);
            }

            case "tuple":
            {
                JsonObject fields = Exact(value, "items", "kind");
                budget.Consume();
                var items = new List<BoundaryType>();
                foreach (Json item in Counted(fields["items"], budget, false))
                {
                    items.Add(DecodeType(item, budget));
                }

                return new TupleType(items);
            }

            default:
                throw ProtocolException.Unsupported("unsupported boundary type kind");
        }
    }

    /// <summary>Renders a boundary type, charging <paramref name="budget"/>.</summary>
    /// <exception cref="ProtocolException">E1602 when a limit is exceeded.</exception>
    public static Json EncodeType(BoundaryType type, Budget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        return EncodeTypeAt(type, budget, 1);
    }

    private static JsonObject EncodeTypeAt(BoundaryType type, Budget budget, int depth)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (depth > budget.Limits.MaxJsonDepth)
        {
            throw ProtocolException.Exceeded("type nesting exceeds max_json_depth");
        }

        budget.Consume();
        IReadOnlyList<BoundaryType> children = type switch
        {
            NominalType nominal => nominal.Arguments,
            TupleType tuple => tuple.Items,
            _ => throw ProtocolException.Unsupported("unsupported boundary type kind"),
        };
        budget.Count(children.Count, false);
        var items = new List<Json>(children.Count);
        foreach (BoundaryType child in children)
        {
            items.Add(EncodeTypeAt(child, budget, depth + 2));
        }

        return type is NominalType named
            ? Json.Object(("arguments", Json.Of(items)), ("identity", Json.Of(named.Identity.Hex)), ("kind", Json.Of("nominal")))
            : Json.Object(("items", Json.Of(items)), ("kind", Json.Of("tuple")));
    }

    private static long CanonicalInt(string text)
    {
        string digits = text.StartsWith('-') ? text[1..] : text;
        bool canonical = digits.Length > 0
            && digits.All(c => c >= '0' && c <= '9')
            && (digits == "0" || !digits.StartsWith('0'))
            && text != "-0"
            && text.Length <= 20;
        if (!canonical)
        {
            throw ProtocolException.Malformed("Int must use canonical 63-bit decimal text");
        }

        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value) || value < Json.IntMin || value > Json.IntMax)
        {
            throw ProtocolException.Malformed("Int is outside the 63-bit range");
        }

        return value;
    }

    /// <summary>Parses one wire boundary value, charging <paramref name="budget"/>.</summary>
    /// <exception cref="ProtocolException">E1601, E1602, or E1604 as Core refuses the descriptor.</exception>
    public static Value DecodeValue(Json value, Budget budget)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(budget);
        string kind = Kind(value);
        switch (kind)
        {
            case "int":
            case "text":
            case "hash":
            {
                JsonObject fields = Exact(value, "kind", "value");
                budget.Consume();
                switch (kind)
                {
                    case "int":
                        return new IntValue(CanonicalInt(AsStr(fields["value"])));
                    case "text":
                        string text = AsStr(fields["value"]);
                        budget.Text(text);
                        return new TextValue(text);
                    default:
                        return new HashValue(Hash.FromJson(fields["value"]));
                }
            }

            case "real":
            {
                JsonObject fields = Exact(value, "bits", "kind");
                budget.Consume();
                string bits = AsStr(fields["bits"]);
                if (!Hash.LowerHex(bits, 16))
                {
                    throw ProtocolException.Malformed("Real must have exactly 16 lowercase hexadecimal digits");
                }

                return new RealValue(ulong.Parse(bits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
            }

            case "tuple":
            {
                JsonObject fields = Exact(value, "items", "kind");
                budget.Consume();
                var items = new List<Value>();
                foreach (Json item in Counted(fields["items"], budget, false))
                {
                    items.Add(DecodeValue(item, budget));
                }

                return new TupleValue(items);
            }

            case "constructor":
            {
                JsonObject fields = Exact(value, "arguments", "identity", "kind");
                budget.Consume();
                Hash identity = Hash.FromJson(fields["identity"]);
                var arguments = new List<Value>();
                foreach (Json item in Counted(fields["arguments"], budget, true))
                {
                    arguments.Add(DecodeValue(item, budget));
                }

                return new ConstructorValue(identity, arguments);
            }

            default:
                throw ProtocolException.Unsupported("unsupported boundary value kind");
        }
    }

    /// <summary>Renders a boundary value, charging <paramref name="budget"/>.</summary>
    /// <exception cref="ProtocolException">E1601 for a lone surrogate, E1602 when a limit is exceeded, E1604 for null or an Int outside the 63-bit range.</exception>
    public static Json EncodeValue(Value? value, Budget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        return EncodeValueAt(value, budget, 1);
    }

    private static JsonObject EncodeValueAt(Value? value, Budget budget, int depth)
    {
        if (value is null)
        {
            throw ProtocolException.Unsupported("null is not a boundary value");
        }

        if (depth > budget.Limits.MaxJsonDepth)
        {
            throw ProtocolException.Exceeded("value nesting exceeds max_json_depth");
        }

        budget.Consume();
        switch (value)
        {
            case IntValue number:
                if (number.Value < Json.IntMin || number.Value > Json.IntMax)
                {
                    throw ProtocolException.Unsupported("Int is outside the 63-bit range");
                }

                return Json.Object(("kind", Json.Of("int")), ("value", Json.Of(number.Value.ToString(CultureInfo.InvariantCulture))));
            case TextValue text:
                budget.Text(text.Value);
                return Json.Object(("kind", Json.Of("text")), ("value", Json.Of(text.Value)));
            case RealValue real:
                return Json.Object(("bits", Json.Of(real.Bits.ToString("x16", CultureInfo.InvariantCulture))), ("kind", Json.Of("real")));
            case HashValue hash:
                return Json.Object(("kind", Json.Of("hash")), ("value", Json.Of(hash.Hash.Hex)));
            case TupleValue tuple:
            {
                budget.Count(tuple.Items.Count, false);
                var items = new List<Json>(tuple.Items.Count);
                foreach (Value item in tuple.Items)
                {
                    items.Add(EncodeValueAt(item, budget, depth + 2));
                }

                return Json.Object(("items", Json.Of(items)), ("kind", Json.Of("tuple")));
            }

            case ConstructorValue constructor:
            {
                budget.Count(constructor.Arguments.Count, true);
                var items = new List<Json>(constructor.Arguments.Count);
                foreach (Value item in constructor.Arguments)
                {
                    items.Add(EncodeValueAt(item, budget, depth + 2));
                }

                return Json.Object(("arguments", Json.Of(items)), ("identity", Json.Of(constructor.Identity.Hex)), ("kind", Json.Of("constructor")));
            }

            default:
                throw ProtocolException.Unsupported("unsupported boundary value kind");
        }
    }

    private static long StringBytes(Json value) => value switch
    {
        JsonString text => JsonCodec.Utf8Length(text.Value),
        JsonArray array => array.Items.Sum(StringBytes),
        JsonObject obj => obj.Values.Sum(StringBytes),
        _ => 0,
    };

    private static List<Diagnostic> Diagnostics(Json? value, Limits limits)
    {
        IReadOnlyList<Json> entries = AsArray(value);
        if (entries.Count == 0)
        {
            throw ProtocolException.Malformed("diagnostics must be nonempty");
        }

        if (entries.Count > limits.MaxDiagnostics || StringBytes(value!) > limits.MaxDiagnosticBytes)
        {
            throw ProtocolException.Exceeded("diagnostics exceed selected limits");
        }

        var parsed = new List<Diagnostic>(entries.Count);
        foreach (Json entry in entries)
        {
            JsonObject fields = AsObject(entry);
            Exact(entry, fields.ContainsKey("contrast") ? DiagnosticKeysWithContrast : DiagnosticKeys);
            if (!Json.Of("jacquard-diagnostic-v1").Equals(fields["schema"]))
            {
                throw ProtocolException.Malformed("unknown diagnostic schema");
            }

            Json codeField = fields["code"];
            string code = codeField is JsonNull ? string.Empty : AsStr(codeField);
            if (fields["span"] is not JsonNull)
            {
                AsObject(fields["span"]);
            }

            Json? contrast = fields.Get("contrast");
            if (contrast is not null && contrast is not JsonNull)
            {
                JsonObject pair = Exact(contrast, "intended", "mistaken");
                AsStr(pair["intended"]);
                AsStr(pair["mistaken"]);
            }

            parsed.Add(new Diagnostic(
                code,
                AsStr(fields["domain"]),
                AsStr(fields["severity"]),
                AsStr(fields["summary"]),
                AsStr(fields["cause"]),
                AsStr(fields["next_step"]),
                fields));
        }

        return parsed;
    }

    private static List<Hash> SortedHashes(Json? value, int limit)
    {
        var hashes = new List<Hash>();
        foreach (Json item in AsArray(value))
        {
            hashes.Add(Hash.FromJson(item));
        }

        if (hashes.Count > limit)
        {
            throw ProtocolException.Exceeded("effect set exceeds selected limits");
        }

        if (!StrictlySorted(hashes))
        {
            throw ProtocolException.Malformed("effects must be a strictly sorted set");
        }

        return hashes;
    }

    private static bool StrictlySorted(IReadOnlyList<Hash> hashes)
    {
        for (int i = 1; i < hashes.Count; i++)
        {
            if (hashes[i].CompareTo(hashes[i - 1]) <= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static JsonArray HashList(IEnumerable<Hash> hashes) => Json.Of(hashes.Select(hash => (Json)Json.Of(hash.Hex)));

    /// <summary>Validates and renders the single v0 invoke frame.</summary>
    /// <exception cref="ProtocolException">E1601, E1602, or E1604 when the invocation cannot be encoded within <paramref name="limits"/>.</exception>
    public static JsonObject InvokeFrame(Hash target, CallInterface spec, IReadOnlyList<Value> args, IReadOnlyList<OperationEntry> operations, Limits limits)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(limits);
        if (args.Count != spec.Parameters.Count)
        {
            throw ProtocolException.Malformed("argument count differs from parameter count");
        }

        var budget = new Budget(limits);
        budget.Count(args.Count, true);
        if (spec.Effects.Count > limits.MaxEffects)
        {
            throw ProtocolException.Exceeded("effect set exceeds selected limits");
        }

        if (!StrictlySorted(spec.Effects))
        {
            throw ProtocolException.Malformed("effects must be a strictly sorted set");
        }

        if (operations.Count > limits.MaxOperations)
        {
            throw ProtocolException.Exceeded("registry exceeds max_operations");
        }

        OperationEntry[] entries = operations.ToArray();
        foreach (OperationEntry entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry, nameof(operations));
        }

        Array.Sort(entries);
        var granted = new List<Json>(entries.Length);
        for (int index = 0; index < entries.Length; index++)
        {
            OperationEntry entry = entries[index];
            if (!spec.Effects.Contains(entry.Effect))
            {
                throw ProtocolException.Malformed("operation effect is outside the interface row");
            }

            if (index > 0 && entries[index - 1].Equals(entry))
            {
                throw ProtocolException.Malformed("duplicate operation entry");
            }

            granted.Add(Json.Object(("effect", Json.Of(entry.Effect.Hex)), ("mode", Json.Of("once")), ("operation", Json.Of(entry.Operation.Hex))));
        }

        var arguments = new List<Json>(args.Count);
        foreach (Value arg in args)
        {
            arguments.Add(EncodeValue(arg, budget));
        }

        var parameters = new List<Json>(spec.Parameters.Count);
        foreach (BoundaryType parameter in spec.Parameters)
        {
            parameters.Add(EncodeType(parameter, budget));
        }

        Json result = EncodeType(spec.Result, budget);
        return Json.Object(
            ("arguments", Json.Of(arguments)),
            ("capabilities", Json.Object(("effects", HashList(spec.Effects)), ("operations", Json.Of(granted)))),
            ("interface", Json.Object(("effects", HashList(spec.Effects)), ("parameters", Json.Of(parameters)), ("result", result))),
            ("invocation_id", Json.Of(InvocationId)),
            ("kind", Json.Of("invoke")),
            ("protocol", Json.Of(Compat.Protocol)),
            ("target", Json.Object(("callable", Json.Of(target.Hex)), ("kind", Json.Of("store-term-v0")))));
    }

    private static void Hashes(JsonObject fields, params string[] keys)
    {
        foreach (string key in keys)
        {
            Hash.FromJson(fields[key]);
        }
    }

    private static (JsonObject Evidence, string Terminal) ValidateEvidence(Json? value, Limits limits, Budget budget)
    {
        JsonObject evidence = Exact(value, "core", "host_observations");
        JsonObject core = Exact(evidence["core"], "capabilities", "effect_requests", "interface", "invocation_id", "schema", "target", "terminal");
        string? terminal = core["terminal"].AsString();
        if (terminal is null || !Terminals.Contains(terminal))
        {
            throw ProtocolException.Malformed("unknown core evidence schema or terminal");
        }

        if (!Json.Of("jacquard-host-core-evidence-v0").Equals(core["schema"]))
        {
            throw ProtocolException.Malformed("unknown core evidence schema or terminal");
        }

        if (!Json.Of(InvocationId).Equals(core["invocation_id"]))
        {
            throw ProtocolException.Order("wrong evidence invocation");
        }

        JsonObject target = Exact(core["target"], "callable", "kind");
        Hash.FromJson(target["callable"]);
        if (!Json.Of("store-term-v0").Equals(target["kind"]))
        {
            throw ProtocolException.Malformed("unknown target kind");
        }

        JsonObject spec = Exact(core["interface"], "effects", "parameters", "result");
        SortedHashes(spec["effects"], limits.MaxEffects);
        IReadOnlyList<Json> parameters = AsArray(spec["parameters"]);
        budget.Count(parameters.Count, true);
        foreach (Json parameter in parameters)
        {
            DecodeType(parameter, budget);
        }

        DecodeType(spec["result"], budget);
        JsonObject capabilities = Exact(core["capabilities"], "effects", "operations");
        SortedHashes(capabilities["effects"], limits.MaxEffects);
        IReadOnlyList<Json> operations = AsArray(capabilities["operations"]);
        if (operations.Count > limits.MaxOperations)
        {
            throw ProtocolException.Exceeded("too many evidence operations");
        }

        foreach (Json operation in operations)
        {
            JsonObject entry = Exact(operation, "effect", "mode", "operation");
            Hashes(entry, "effect", "operation");
            if (!Json.Of("once").Equals(entry["mode"]))
            {
                throw ProtocolException.Malformed("unknown operation mode");
            }
        }

        IReadOnlyList<Json> requests = AsArray(core["effect_requests"]);
        if (requests.Count > limits.MaxEffectRequests)
        {
            throw ProtocolException.Exceeded("too many evidence requests");
        }

        for (int index = 0; index < requests.Count; index++)
        {
            JsonObject entry = Exact(requests[index], "effect", "operation", "ordinal");
            Hashes(entry, "effect", "operation");
            if (!Json.Of(index + 1L).Equals(entry["ordinal"]))
            {
                throw ProtocolException.Malformed("evidence ordinals must be consecutive");
            }
        }

        JsonObject observations = Exact(evidence["host_observations"], "responses", "schema");
        if (!Json.Of("jacquard-host-observations-v0").Equals(observations["schema"]))
        {
            throw ProtocolException.Malformed("unknown host observations schema");
        }

        foreach (Json response in AsArray(observations["responses"]))
        {
            JsonObject entry = Exact(response, "category", "completion", "ordinal");
            string? category = entry["category"].AsString();
            if (category is null || !Categories.Contains(category))
            {
                throw ProtocolException.Malformed("unknown observation category");
            }

            string? completion = entry["completion"].AsString();
            if (completion is null || !Completions.Contains(completion))
            {
                throw ProtocolException.Malformed("unknown observation completion");
            }

            long? ordinal = entry["ordinal"].AsInt();
            if (ordinal is null || ordinal < 1 || ordinal > requests.Count)
            {
                throw ProtocolException.Malformed("invalid observation ordinal");
            }
        }

        return (evidence, terminal);
    }

    private static void Invocation(JsonObject fields)
    {
        if (!string.Equals(AsStr(fields["invocation_id"]), InvocationId, StringComparison.Ordinal))
        {
            throw ProtocolException.Order("frame names the wrong invocation");
        }
    }

    /// <summary>Validates one Core frame after <c>core_hello</c>.</summary>
    /// <exception cref="ProtocolException">The frozen code Core's frame earns.</exception>
    public static CoreFrame ParseCoreFrame(JsonObject frame, Limits limits)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(limits);
        string kind = frame.Get("kind")?.AsString() ?? string.Empty;
        if (kind == "core_hello")
        {
            throw ProtocolException.Order("core_hello may only be the first frame");
        }

        if (!Json.Of(Compat.Protocol).Equals(frame.Get("protocol")))
        {
            throw ProtocolException.Malformed("unknown protocol");
        }

        switch (kind)
        {
            case "fatal":
                Exact(frame, "diagnostics", "kind", "protocol");
                return new Fatal(Diagnostics(frame["diagnostics"], limits), frame);
            case "shutdown_ack":
                Exact(frame, "kind", "protocol");
                return new ShutdownAck(frame);
            case "effect_request":
                return ParseRequest(frame, limits);
            case "outcome":
                return ParseOutcome(frame, limits);
            default:
                throw ProtocolException.Malformed("unknown Core frame kind");
        }
    }

    private static EffectRequest ParseRequest(JsonObject frame, Limits limits)
    {
        Exact(frame, "arguments", "effect", "invocation_id", "kind", "mode", "operation", "protocol", "request_id");
        Invocation(frame);
        if (!Json.Of("once").Equals(frame["mode"]))
        {
            throw ProtocolException.Malformed("unknown operation mode");
        }

        string id = AsStr(frame["request_id"]);
        if (!Hash.LowerHex(id, 16))
        {
            throw ProtocolException.Malformed("request_id must have 16 lowercase hexadecimal digits");
        }

        ulong ordinal = ulong.Parse(id, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        if (ordinal == 0)
        {
            throw ProtocolException.Order("request ordinals start at one");
        }

        if (ordinal > Limits.MaxSafeInteger)
        {
            throw ProtocolException.Order("request ordinal is not consecutive");
        }

        var budget = new Budget(limits);
        IReadOnlyList<Json> items = Counted(frame["arguments"], budget, true);
        Hash effect = Hash.FromJson(frame["effect"]);
        Hash operation = Hash.FromJson(frame["operation"]);
        var arguments = new List<Value>(items.Count);
        foreach (Json item in items)
        {
            arguments.Add(DecodeValue(item, budget));
        }

        return new EffectRequest((long)ordinal, effect, operation, arguments, frame);
    }

    private static Outcome ParseOutcome(JsonObject frame, Limits limits)
    {
        Exact(frame, "evidence", "invocation_id", "kind", "protocol", "result");
        Invocation(frame);

        // Core encodes evidence types and the result value against one budget.
        var budget = new Budget(limits);
        (JsonObject evidence, string terminal) = ValidateEvidence(frame["evidence"], limits, budget);
        JsonObject result = AsObject(frame["result"]);
        string? name = result.Get("kind")?.AsString();
        if (name == "ok")
        {
            Exact(result, "kind", "value");
            if (terminal != "ok")
            {
                throw ProtocolException.Malformed("ok result has a non-ok terminal");
            }

            Value decoded = DecodeValue(result["value"], budget);
            return new Outcome(decoded, Array.Empty<Diagnostic>(), terminal, evidence, frame);
        }

        if (name == "error")
        {
            Exact(result, "diagnostics", "kind");
            if (terminal == "ok")
            {
                throw ProtocolException.Malformed("error result has an ok terminal");
            }

            List<Diagnostic> parsed = Diagnostics(result["diagnostics"], limits);
            return new Outcome(null, parsed, terminal, evidence, frame);
        }

        throw ProtocolException.Malformed("unknown result kind");
    }
}
