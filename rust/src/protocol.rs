//! Frozen v0 values, boundary types, limits, envelopes, and the
//! carrier-independent host session state machine. Descriptors are checked
//! in Core's order: kind, exact fields, one node, identity, then counts.

use crate::compat::{CARRIER, PROTOCOL};
use crate::error::ProtocolError;
use crate::json::{INT_MAX, INT_MIN, Json, Object, frame_bytes, object};

type Result<T> = std::result::Result<T, ProtocolError>;

/// The largest integer the other adapters accept exactly in limits and ordinals.
const MAX_SAFE_INTEGER: i64 = (1 << 53) - 1;

/// The v0 ceilings a host selects; each must lie in `1..=` its hard value.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Limits {
    pub max_arguments: usize,
    pub max_collection_items: usize,
    pub max_diagnostic_bytes: usize,
    pub max_diagnostics: usize,
    pub max_effect_requests: usize,
    pub max_effects: usize,
    pub max_frame_bytes: usize,
    pub max_host_message_bytes: usize,
    pub max_json_depth: usize,
    pub max_operations: usize,
    pub max_stderr_bytes: usize,
    pub max_text_bytes: usize,
    pub max_value_nodes: usize,
}

/// The frozen v0 ceilings Core advertises in `core_hello`.
pub const HARD_LIMITS: Limits = Limits {
    max_arguments: 64,
    max_collection_items: 1024,
    max_diagnostic_bytes: 65536,
    max_diagnostics: 32,
    max_effect_requests: 1024,
    max_effects: 64,
    max_frame_bytes: 1_048_576,
    max_host_message_bytes: 4096,
    max_json_depth: 64,
    max_operations: 256,
    max_stderr_bytes: 65536,
    max_text_bytes: 262_144,
    max_value_nodes: 4096,
};

impl Limits {
    fn fields(&self) -> [(&'static str, usize, usize); 13] {
        let h = HARD_LIMITS;
        [
            ("max_arguments", self.max_arguments, h.max_arguments),
            ("max_collection_items", self.max_collection_items, h.max_collection_items),
            ("max_diagnostic_bytes", self.max_diagnostic_bytes, h.max_diagnostic_bytes),
            ("max_diagnostics", self.max_diagnostics, h.max_diagnostics),
            ("max_effect_requests", self.max_effect_requests, h.max_effect_requests),
            ("max_effects", self.max_effects, h.max_effects),
            ("max_frame_bytes", self.max_frame_bytes, h.max_frame_bytes),
            ("max_host_message_bytes", self.max_host_message_bytes, h.max_host_message_bytes),
            ("max_json_depth", self.max_json_depth, h.max_json_depth),
            ("max_operations", self.max_operations, h.max_operations),
            ("max_stderr_bytes", self.max_stderr_bytes, h.max_stderr_bytes),
            ("max_text_bytes", self.max_text_bytes, h.max_text_bytes),
            ("max_value_nodes", self.max_value_nodes, h.max_value_nodes),
        ]
    }

    /// E1602 for a limit outside `1..=` its hard ceiling.
    pub fn validate(&self) -> Result<()> {
        for (name, value, hard) in self.fields() {
            if value < 1 || value > hard {
                return Err(ProtocolError::exceeded(format!("limit {name} exceeds its bounds")));
            }
        }
        Ok(())
    }

    /// The wire object for `host_select`.
    pub fn to_json(&self) -> Object {
        self.fields().iter().map(|(name, value, _)| ((*name).to_owned(), Json::Int(*value as i64))).collect()
    }

    /// Parses and validates an exact limits object.
    pub fn from_json(value: &Json) -> Result<Limits> {
        let names: Vec<&str> = HARD_LIMITS.fields().iter().map(|field| field.0).collect();
        let fields = exact(value, &names)?;
        let mut parsed = [0usize; 13];
        for (index, (name, _, hard)) in HARD_LIMITS.fields().iter().enumerate() {
            let number = match fields[*name] {
                Json::Int(number) if (-MAX_SAFE_INTEGER..=MAX_SAFE_INTEGER).contains(&number) => number,
                _ => return Err(ProtocolError::malformed(format!("limit {name} must be an integer"))),
            };
            if number < 1 || number > *hard as i64 {
                return Err(ProtocolError::exceeded(format!("limit {name} exceeds its bounds")));
            }
            parsed[index] = number as usize;
        }
        let [a, b, c, d, e, f, g, h, i, j, k, l, m] = parsed;
        Ok(Limits {
            max_arguments: a,
            max_collection_items: b,
            max_diagnostic_bytes: c,
            max_diagnostics: d,
            max_effect_requests: e,
            max_effects: f,
            max_frame_bytes: g,
            max_host_message_bytes: h,
            max_json_depth: i,
            max_operations: j,
            max_stderr_bytes: k,
            max_text_bytes: l,
            max_value_nodes: m,
        })
    }
}

/// A HASH_V0 identity: exactly 64 lowercase hexadecimal digits.
#[derive(Clone, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct Hash(String);

impl Hash {
    /// Validates a HASH_V0 identity.
    pub fn parse(text: &str) -> Result<Hash> {
        if text.len() == 64 && text.bytes().all(|byte| matches!(byte, b'0'..=b'9' | b'a'..=b'f')) {
            Ok(Hash(text.to_owned()))
        } else {
            Err(ProtocolError::malformed("HASH_V0 must be exactly 64 lowercase hexadecimal digits"))
        }
    }

    /// The hexadecimal text.
    pub fn as_str(&self) -> &str {
        &self.0
    }

    fn from_json(value: &Json) -> Result<Hash> {
        match value {
            Json::Str(text) => Hash::parse(text),
            _ => Err(ProtocolError::malformed("HASH_V0 must be exactly 64 lowercase hexadecimal digits")),
        }
    }
}

impl std::fmt::Display for Hash {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

/// One v0 boundary value.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Value {
    /// Core's signed 63-bit integer.
    Int(i64),
    /// A Unicode scalar string.
    Text(String),
    /// Binary64 bits, preserving NaN payloads and signed zero.
    Real(u64),
    /// An explicit `Hash`.
    Hash(Hash),
    /// A positional product; the empty tuple is unit.
    Tuple(Vec<Value>),
    /// A constructor identity and its positional arguments.
    Constructor(Hash, Vec<Value>),
}

impl Value {
    /// The `Real` with the bits of an `f64`.
    pub fn real(value: f64) -> Value {
        Value::Real(value.to_bits())
    }
}

/// A closed v0 boundary type.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum BoundaryType {
    /// A type identity applied to arguments.
    Nominal(Hash, Vec<BoundaryType>),
    /// A positional product type.
    Tuple(Vec<BoundaryType>),
}

/// The checked callable signature sent with an invocation.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Interface {
    pub parameters: Vec<BoundaryType>,
    pub result: BoundaryType,
    pub effects: Vec<Hash>,
}

/// Counts nodes and checks collection and text sizes for one frame.
#[derive(Debug)]
pub struct Budget {
    pub limits: Limits,
    nodes: usize,
}

impl Budget {
    /// A fresh budget under the given limits.
    pub fn new(limits: Limits) -> Budget {
        Budget { limits, nodes: 0 }
    }

    fn consume(&mut self) -> Result<()> {
        if self.nodes >= self.limits.max_value_nodes {
            return Err(ProtocolError::exceeded("frame exceeds max_value_nodes"));
        }
        self.nodes += 1;
        Ok(())
    }

    fn count(&self, n: usize, arguments: bool) -> Result<()> {
        if n > self.limits.max_collection_items || (arguments && n > self.limits.max_arguments) {
            return Err(ProtocolError::exceeded("collection exceeds selected limits"));
        }
        Ok(())
    }

    fn text(&self, text: &str) -> Result<()> {
        if text.len() > self.limits.max_text_bytes {
            return Err(ProtocolError::exceeded("Text exceeds max_text_bytes"));
        }
        Ok(())
    }
}

pub(crate) fn as_object(value: &Json) -> Result<&Object> {
    value.as_object().ok_or_else(|| ProtocolError::malformed("expected a JSON object"))
}

pub(crate) fn exact<'a>(value: &'a Json, keys: &[&str]) -> Result<&'a Object> {
    let fields = as_object(value)?;
    if fields.len() != keys.len() || keys.iter().any(|key| !fields.contains_key(*key)) {
        return Err(ProtocolError::malformed(format!("expected exactly: {}", keys.join(", "))));
    }
    Ok(fields)
}

fn as_str(value: &Json) -> Result<&str> {
    value.as_str().ok_or_else(|| ProtocolError::malformed("expected a string"))
}

fn as_array(value: &Json) -> Result<&[Json]> {
    value.as_array().ok_or_else(|| ProtocolError::malformed("expected an array"))
}

fn kinded(value: &Json) -> Result<(&Object, &str)> {
    let fields = as_object(value)?;
    match fields.get("kind") {
        Some(Json::Str(kind)) => Ok((fields, kind)),
        _ => Err(ProtocolError::malformed("a boundary descriptor needs one kind string")),
    }
}

fn counted<'a>(value: &'a Json, budget: &Budget, arguments: bool) -> Result<&'a [Json]> {
    let items = as_array(value)?;
    budget.count(items.len(), arguments)?;
    Ok(items)
}

/// Parses one wire boundary type.
pub fn decode_type(value: &Json, budget: &mut Budget) -> Result<BoundaryType> {
    let (fields, kind) = kinded(value)?;
    match kind {
        "nominal" => {
            exact(value, &["arguments", "identity", "kind"])?;
            budget.consume()?;
            let identity = Hash::from_json(&fields["identity"])?;
            let items = counted(&fields["arguments"], budget, false)?;
            let arguments = items.iter().map(|item| decode_type(item, budget)).collect::<Result<_>>()?;
            Ok(BoundaryType::Nominal(identity, arguments))
        }
        "tuple" => {
            exact(value, &["items", "kind"])?;
            budget.consume()?;
            let items = counted(&fields["items"], budget, false)?;
            Ok(BoundaryType::Tuple(items.iter().map(|item| decode_type(item, budget)).collect::<Result<_>>()?))
        }
        _ => Err(ProtocolError::unsupported("unsupported boundary type kind")),
    }
}

/// Renders a boundary type, charging the budget.
pub fn encode_type(t: &BoundaryType, budget: &mut Budget) -> Result<Json> {
    encode_type_at(t, budget, 1)
}

fn encode_type_at(t: &BoundaryType, budget: &mut Budget, depth: usize) -> Result<Json> {
    if depth > budget.limits.max_json_depth {
        return Err(ProtocolError::exceeded("type nesting exceeds max_json_depth"));
    }
    budget.consume()?;
    let (kind, field, children, identity) = match t {
        BoundaryType::Nominal(identity, arguments) => ("nominal", "arguments", arguments, Some(identity)),
        BoundaryType::Tuple(items) => ("tuple", "items", items, None),
    };
    budget.count(children.len(), false)?;
    let items = children.iter().map(|child| encode_type_at(child, budget, depth + 2)).collect::<Result<Vec<_>>>()?;
    let mut fields = object([("kind", kind.into()), (field, Json::Array(items))]);
    if let Some(identity) = identity {
        fields.insert("identity".into(), identity.as_str().into());
    }
    Ok(Json::Object(fields))
}

fn canonical_int(text: &str) -> Result<i64> {
    let digits = text.strip_prefix('-').unwrap_or(text);
    let canonical = !digits.is_empty()
        && digits.bytes().all(|byte| byte.is_ascii_digit())
        && (digits == "0" || !digits.starts_with('0'))
        && text != "-0"
        && text.len() <= 20;
    if !canonical {
        return Err(ProtocolError::malformed("Int must use canonical 63-bit decimal text"));
    }
    text.parse::<i64>()
        .ok()
        .filter(|value| (INT_MIN..=INT_MAX).contains(value))
        .ok_or_else(|| ProtocolError::malformed("Int is outside the 63-bit range"))
}

/// Parses one wire boundary value.
pub fn decode_value(value: &Json, budget: &mut Budget) -> Result<Value> {
    let (fields, kind) = kinded(value)?;
    match kind {
        "int" | "text" | "hash" => {
            exact(value, &["kind", "value"])?;
            budget.consume()?;
            match kind {
                "int" => {
                    let text = as_str(&fields["value"])?;
                    canonical_int(text).map(Value::Int)
                }
                "text" => {
                    let text = as_str(&fields["value"])?;
                    budget.text(text)?;
                    Ok(Value::Text(text.to_owned()))
                }
                _ => Ok(Value::Hash(Hash::from_json(&fields["value"])?)),
            }
        }
        "real" => {
            exact(value, &["bits", "kind"])?;
            budget.consume()?;
            let bits = as_str(&fields["bits"])?;
            if bits.len() != 16 || !bits.bytes().all(|byte| matches!(byte, b'0'..=b'9' | b'a'..=b'f')) {
                return Err(ProtocolError::malformed("Real must have exactly 16 lowercase hexadecimal digits"));
            }
            Ok(Value::Real(u64::from_str_radix(bits, 16).expect("validated hexadecimal")))
        }
        "tuple" => {
            exact(value, &["items", "kind"])?;
            budget.consume()?;
            let items = counted(&fields["items"], budget, false)?;
            Ok(Value::Tuple(items.iter().map(|item| decode_value(item, budget)).collect::<Result<_>>()?))
        }
        "constructor" => {
            exact(value, &["arguments", "identity", "kind"])?;
            budget.consume()?;
            let identity = Hash::from_json(&fields["identity"])?;
            let items = counted(&fields["arguments"], budget, true)?;
            Ok(Value::Constructor(identity, items.iter().map(|item| decode_value(item, budget)).collect::<Result<_>>()?))
        }
        _ => Err(ProtocolError::unsupported("unsupported boundary value kind")),
    }
}

/// Renders a boundary value, charging the budget.
pub fn encode_value(value: &Value, budget: &mut Budget) -> Result<Json> {
    encode_value_at(value, budget, 1)
}

fn encode_value_at(value: &Value, budget: &mut Budget, depth: usize) -> Result<Json> {
    if depth > budget.limits.max_json_depth {
        return Err(ProtocolError::exceeded("value nesting exceeds max_json_depth"));
    }
    budget.consume()?;
    Ok(Json::Object(match value {
        Value::Int(number) => {
            if !(INT_MIN..=INT_MAX).contains(number) {
                return Err(ProtocolError::unsupported("Int is outside the 63-bit range"));
            }
            object([("kind", "int".into()), ("value", number.to_string().into())])
        }
        Value::Text(text) => {
            budget.text(text)?;
            object([("kind", "text".into()), ("value", text.as_str().into())])
        }
        Value::Real(bits) => object([("bits", format!("{bits:016x}").into()), ("kind", "real".into())]),
        Value::Hash(hash) => object([("kind", "hash".into()), ("value", hash.as_str().into())]),
        Value::Tuple(items) => {
            budget.count(items.len(), false)?;
            let items = items.iter().map(|item| encode_value_at(item, budget, depth + 2)).collect::<Result<Vec<_>>>()?;
            object([("items", Json::Array(items)), ("kind", "tuple".into())])
        }
        Value::Constructor(identity, arguments) => {
            budget.count(arguments.len(), true)?;
            let items = arguments.iter().map(|item| encode_value_at(item, budget, depth + 2)).collect::<Result<Vec<_>>>()?;
            object([("arguments", Json::Array(items)), ("identity", identity.as_str().into()), ("kind", "constructor".into())])
        }
    }))
}

/// The only invocation identity a v0 host session uses.
pub const INVOCATION_ID: &str = "0000000000000000";

/// The category of a host effect failure.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum FailureCategory {
    UnsupportedOperation,
    RefusedAuthority,
    OutsideFailure,
    CompletionUnknown,
}

impl FailureCategory {
    /// The wire name.
    pub fn as_str(self) -> &'static str {
        match self {
            FailureCategory::UnsupportedOperation => "unsupported_operation",
            FailureCategory::RefusedAuthority => "refused_authority",
            FailureCategory::OutsideFailure => "outside_failure",
            FailureCategory::CompletionUnknown => "completion_unknown",
        }
    }

    /// The one completion the protocol permits with this category.
    pub fn completion(self) -> &'static str {
        match self {
            FailureCategory::UnsupportedOperation | FailureCategory::RefusedAuthority => "not_started",
            FailureCategory::OutsideFailure => "failed",
            FailureCategory::CompletionUnknown => "unknown",
        }
    }
}

/// The reason a pending request is withdrawn.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CancelReason {
    Cancelled,
    Timeout,
    HostShutdown,
}

/// One `jacquard-diagnostic-v1` entry; `raw` keeps the exact object.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Diagnostic {
    /// Empty when Core sent null.
    pub code: String,
    pub domain: String,
    pub severity: String,
    pub summary: String,
    pub cause: String,
    pub next_step: String,
    pub raw: Object,
}

/// An exact effect operation granted for the invocation.
#[derive(Clone, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct OperationEntry {
    pub effect: Hash,
    pub operation: Hash,
}

/// Core asking the host to perform one operation.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct EffectRequest {
    pub ordinal: usize,
    pub effect: Hash,
    pub operation: Hash,
    pub arguments: Vec<Value>,
    pub raw: Object,
}

/// Core's final frame.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Terminal {
    /// An invocation outcome: a value, or diagnostics, with Core's evidence.
    Outcome { value: Option<Value>, diagnostics: Vec<Diagnostic>, terminal: String, evidence: Object, raw: Object },
    /// A session-level failure.
    Fatal { diagnostics: Vec<Diagnostic>, raw: Object },
    /// The acknowledgement of a host shutdown.
    ShutdownAck { raw: Object },
}

/// A parsed Core frame after `core_hello`.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum CoreFrame {
    Request(EffectRequest),
    Terminal(Terminal),
}

const TERMINALS: [&str; 5] = ["ok", "diagnostic", "cancelled", "host_failure", "completion_unknown"];

fn string_bytes(value: &Json) -> usize {
    match value {
        Json::Str(text) => text.len(),
        Json::Array(items) => items.iter().map(string_bytes).sum(),
        Json::Object(fields) => fields.values().map(string_bytes).sum(),
        _ => 0,
    }
}

fn diagnostics(value: &Json, limits: &Limits) -> Result<Vec<Diagnostic>> {
    let entries = as_array(value)?;
    if entries.is_empty() {
        return Err(ProtocolError::malformed("diagnostics must be nonempty"));
    }
    if entries.len() > limits.max_diagnostics || string_bytes(value) > limits.max_diagnostic_bytes {
        return Err(ProtocolError::exceeded("diagnostics exceed selected limits"));
    }
    entries
        .iter()
        .map(|entry| {
            let fields = as_object(entry)?;
            let mut keys = vec!["cause", "code", "domain", "next_step", "schema", "severity", "span", "summary"];
            if fields.contains_key("contrast") {
                keys.push("contrast");
            }
            exact(entry, &keys)?;
            if fields["schema"] != Json::from("jacquard-diagnostic-v1") {
                return Err(ProtocolError::malformed("unknown diagnostic schema"));
            }
            let code = match &fields["code"] {
                Json::Null => String::new(),
                other => as_str(other)?.to_owned(),
            };
            let text = |key: &str| as_str(&fields[key]).map(str::to_owned);
            if !matches!(fields["span"], Json::Null) {
                as_object(&fields["span"])?;
            }
            if let Some(contrast) = fields.get("contrast").filter(|value| !matches!(value, Json::Null)) {
                let pair = exact(contrast, &["intended", "mistaken"])?;
                as_str(&pair["intended"])?;
                as_str(&pair["mistaken"])?;
            }
            Ok(Diagnostic {
                code,
                domain: text("domain")?,
                severity: text("severity")?,
                summary: text("summary")?,
                cause: text("cause")?,
                next_step: text("next_step")?,
                raw: fields.clone(),
            })
        })
        .collect()
}

fn sorted_hashes(value: &Json, limit: usize) -> Result<Vec<Hash>> {
    let hashes = as_array(value)?.iter().map(Hash::from_json).collect::<Result<Vec<_>>>()?;
    if hashes.len() > limit {
        return Err(ProtocolError::exceeded("effect set exceeds selected limits"));
    }
    if hashes.windows(2).any(|pair| pair[1] <= pair[0]) {
        return Err(ProtocolError::malformed("effects must be a strictly sorted set"));
    }
    Ok(hashes)
}

fn hash_list(hashes: &[Hash]) -> Json {
    Json::Array(hashes.iter().map(|hash| hash.as_str().into()).collect())
}

/// Validates and renders the single v0 invoke frame.
pub fn invoke_frame(target: &Hash, spec: &Interface, args: &[Value], operations: &[OperationEntry], limits: &Limits) -> Result<Object> {
    if args.len() != spec.parameters.len() {
        return Err(ProtocolError::malformed("argument count differs from parameter count"));
    }
    let mut budget = Budget::new(*limits);
    budget.count(args.len(), true)?;
    if spec.effects.len() > limits.max_effects {
        return Err(ProtocolError::exceeded("effect set exceeds selected limits"));
    }
    if spec.effects.windows(2).any(|pair| pair[1] <= pair[0]) {
        return Err(ProtocolError::malformed("effects must be a strictly sorted set"));
    }
    if operations.len() > limits.max_operations {
        return Err(ProtocolError::exceeded("registry exceeds max_operations"));
    }
    let mut entries = operations.to_vec();
    entries.sort();
    let mut granted = Vec::with_capacity(entries.len());
    for (index, entry) in entries.iter().enumerate() {
        if !spec.effects.contains(&entry.effect) {
            return Err(ProtocolError::malformed("operation effect is outside the interface row"));
        }
        if index > 0 && entries[index - 1] == *entry {
            return Err(ProtocolError::malformed("duplicate operation entry"));
        }
        granted.push(Json::Object(object([
            ("effect", entry.effect.as_str().into()),
            ("mode", "once".into()),
            ("operation", entry.operation.as_str().into()),
        ])));
    }
    let arguments = args.iter().map(|arg| encode_value(arg, &mut budget)).collect::<Result<Vec<_>>>()?;
    let parameters = spec.parameters.iter().map(|parameter| encode_type(parameter, &mut budget)).collect::<Result<Vec<_>>>()?;
    let result = encode_type(&spec.result, &mut budget)?;
    Ok(object([
        ("arguments", Json::Array(arguments)),
        ("capabilities", Json::Object(object([("effects", hash_list(&spec.effects)), ("operations", Json::Array(granted))]))),
        (
            "interface",
            Json::Object(object([("effects", hash_list(&spec.effects)), ("parameters", Json::Array(parameters)), ("result", result)])),
        ),
        ("invocation_id", INVOCATION_ID.into()),
        ("kind", "invoke".into()),
        ("protocol", PROTOCOL.into()),
        ("target", Json::Object(object([("callable", target.as_str().into()), ("kind", "store-term-v0".into())]))),
    ]))
}

fn hashes(fields: &Object, keys: &[&str]) -> Result<()> {
    for key in keys {
        Hash::from_json(&fields[*key])?;
    }
    Ok(())
}

fn validate_evidence(value: &Json, limits: &Limits, budget: &mut Budget) -> Result<(Object, String)> {
    let evidence = exact(value, &["core", "host_observations"])?;
    let core =
        exact(&evidence["core"], &["capabilities", "effect_requests", "interface", "invocation_id", "schema", "target", "terminal"])?;
    let terminal = match &core["terminal"] {
        Json::Str(terminal) if TERMINALS.contains(&terminal.as_str()) => terminal.clone(),
        _ => return Err(ProtocolError::malformed("unknown core evidence schema or terminal")),
    };
    if core["schema"] != Json::from("jacquard-host-core-evidence-v0") {
        return Err(ProtocolError::malformed("unknown core evidence schema or terminal"));
    }
    if core["invocation_id"] != Json::from(INVOCATION_ID) {
        return Err(ProtocolError::order("wrong evidence invocation"));
    }
    let target = exact(&core["target"], &["callable", "kind"])?;
    Hash::from_json(&target["callable"])?;
    if target["kind"] != Json::from("store-term-v0") {
        return Err(ProtocolError::malformed("unknown target kind"));
    }
    let spec = exact(&core["interface"], &["effects", "parameters", "result"])?;
    sorted_hashes(&spec["effects"], limits.max_effects)?;
    let parameters = as_array(&spec["parameters"])?;
    budget.count(parameters.len(), true)?;
    for parameter in parameters {
        decode_type(parameter, budget)?;
    }
    decode_type(&spec["result"], budget)?;
    let capabilities = exact(&core["capabilities"], &["effects", "operations"])?;
    sorted_hashes(&capabilities["effects"], limits.max_effects)?;
    let operations = as_array(&capabilities["operations"])?;
    if operations.len() > limits.max_operations {
        return Err(ProtocolError::exceeded("too many evidence operations"));
    }
    for operation in operations {
        let entry = exact(operation, &["effect", "mode", "operation"])?;
        hashes(entry, &["effect", "operation"])?;
        if entry["mode"] != Json::from("once") {
            return Err(ProtocolError::malformed("unknown operation mode"));
        }
    }
    let requests = as_array(&core["effect_requests"])?;
    if requests.len() > limits.max_effect_requests {
        return Err(ProtocolError::exceeded("too many evidence requests"));
    }
    for (index, request) in requests.iter().enumerate() {
        let entry = exact(request, &["effect", "operation", "ordinal"])?;
        hashes(entry, &["effect", "operation"])?;
        if entry["ordinal"] != Json::Int(index as i64 + 1) {
            return Err(ProtocolError::malformed("evidence ordinals must be consecutive"));
        }
    }
    let observations = exact(&evidence["host_observations"], &["responses", "schema"])?;
    if observations["schema"] != Json::from("jacquard-host-observations-v0") {
        return Err(ProtocolError::malformed("unknown host observations schema"));
    }
    const CATEGORIES: [&str; 8] = [
        "ok",
        "unsupported_operation",
        "refused_authority",
        "outside_failure",
        "completion_unknown",
        "cancelled",
        "timeout",
        "host_shutdown",
    ];
    const COMPLETIONS: [&str; 4] = ["completed", "not_started", "failed", "unknown"];
    for response in as_array(&observations["responses"])? {
        let entry = exact(response, &["category", "completion", "ordinal"])?;
        if !matches!(&entry["category"], Json::Str(category) if CATEGORIES.contains(&category.as_str())) {
            return Err(ProtocolError::malformed("unknown observation category"));
        }
        if !matches!(&entry["completion"], Json::Str(completion) if COMPLETIONS.contains(&completion.as_str())) {
            return Err(ProtocolError::malformed("unknown observation completion"));
        }
        match entry["ordinal"] {
            Json::Int(ordinal) if ordinal >= 1 && ordinal <= requests.len() as i64 => {}
            _ => return Err(ProtocolError::malformed("invalid observation ordinal")),
        }
    }
    Ok((evidence.clone(), terminal))
}

fn invocation(fields: &Object) -> Result<()> {
    if as_str(&fields["invocation_id"])? != INVOCATION_ID {
        return Err(ProtocolError::order("frame names the wrong invocation"));
    }
    Ok(())
}

/// Validates one Core frame after `core_hello`.
pub fn parse_core_frame(frame: &Object, limits: &Limits) -> Result<CoreFrame> {
    let kind = frame.get("kind").and_then(Json::as_str).unwrap_or("");
    if kind == "core_hello" {
        return Err(ProtocolError::order("core_hello may only be the first frame"));
    }
    if frame.get("protocol") != Some(&Json::from(PROTOCOL)) {
        return Err(ProtocolError::malformed("unknown protocol"));
    }
    let value = Json::Object(frame.clone());
    match kind {
        "fatal" => {
            exact(&value, &["diagnostics", "kind", "protocol"])?;
            Ok(CoreFrame::Terminal(Terminal::Fatal { diagnostics: diagnostics(&frame["diagnostics"], limits)?, raw: frame.clone() }))
        }
        "shutdown_ack" => {
            exact(&value, &["kind", "protocol"])?;
            Ok(CoreFrame::Terminal(Terminal::ShutdownAck { raw: frame.clone() }))
        }
        "effect_request" => {
            exact(&value, &["arguments", "effect", "invocation_id", "kind", "mode", "operation", "protocol", "request_id"])?;
            invocation(frame)?;
            if frame["mode"] != Json::from("once") {
                return Err(ProtocolError::malformed("unknown operation mode"));
            }
            let id = as_str(&frame["request_id"])?;
            if id.len() != 16 || !id.bytes().all(|byte| matches!(byte, b'0'..=b'9' | b'a'..=b'f')) {
                return Err(ProtocolError::malformed("request_id must have 16 lowercase hexadecimal digits"));
            }
            let ordinal = u64::from_str_radix(id, 16).expect("validated hexadecimal");
            if ordinal < 1 {
                return Err(ProtocolError::order("request ordinals start at one"));
            }
            if ordinal > MAX_SAFE_INTEGER as u64 {
                return Err(ProtocolError::order("request ordinal is not consecutive"));
            }
            let mut budget = Budget::new(*limits);
            let items = counted(&frame["arguments"], &budget, true)?;
            let effect = Hash::from_json(&frame["effect"])?;
            let operation = Hash::from_json(&frame["operation"])?;
            let arguments = items.iter().map(|item| decode_value(item, &mut budget)).collect::<Result<_>>()?;
            Ok(CoreFrame::Request(EffectRequest { ordinal: ordinal as usize, effect, operation, arguments, raw: frame.clone() }))
        }
        "outcome" => {
            exact(&value, &["evidence", "invocation_id", "kind", "protocol", "result"])?;
            invocation(frame)?;
            // Core encodes evidence types and the result value against one budget.
            let mut budget = Budget::new(*limits);
            let (evidence, terminal) = validate_evidence(&frame["evidence"], limits, &mut budget)?;
            let result = as_object(&frame["result"])?;
            match result.get("kind").and_then(Json::as_str) {
                Some("ok") => {
                    exact(&frame["result"], &["kind", "value"])?;
                    if terminal != "ok" {
                        return Err(ProtocolError::malformed("ok result has a non-ok terminal"));
                    }
                    let value = decode_value(&result["value"], &mut budget)?;
                    Ok(CoreFrame::Terminal(Terminal::Outcome {
                        value: Some(value),
                        diagnostics: vec![],
                        terminal,
                        evidence,
                        raw: frame.clone(),
                    }))
                }
                Some("error") => {
                    exact(&frame["result"], &["diagnostics", "kind"])?;
                    if terminal == "ok" {
                        return Err(ProtocolError::malformed("error result has an ok terminal"));
                    }
                    let diagnostics = diagnostics(&result["diagnostics"], limits)?;
                    Ok(CoreFrame::Terminal(Terminal::Outcome { value: None, diagnostics, terminal, evidence, raw: frame.clone() }))
                }
                _ => Err(ProtocolError::malformed("unknown result kind")),
            }
        }
        _ => Err(ProtocolError::malformed("unknown Core frame kind")),
    }
}

/// The host session state.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum State {
    Hello,
    Negotiating,
    Selected,
    ShuttingDown,
    Running,
    Waiting,
    Finishing,
    Closed,
}

impl State {
    /// The state's protocol name.
    pub fn as_str(self) -> &'static str {
        match self {
            State::Hello => "hello",
            State::Negotiating => "negotiating",
            State::Selected => "selected",
            State::ShuttingDown => "shutting_down",
            State::Running => "running",
            State::Waiting => "waiting",
            State::Finishing => "finishing",
            State::Closed => "closed",
        }
    }
}

/// The v0 host state machine: one hello, one selection, one invocation or
/// shutdown, consecutive requests, and one terminal.
#[derive(Debug)]
pub struct Session {
    pub state: State,
    pub limits: Limits,
    pub requests: Vec<EffectRequest>,
    pub responses: Vec<Object>,
    pub terminal: Option<Terminal>,
    pub pending: Option<usize>,
}

impl Default for Session {
    fn default() -> Self {
        Session { state: State::Hello, limits: HARD_LIMITS, requests: vec![], responses: vec![], terminal: None, pending: None }
    }
}

impl Session {
    /// A session awaiting `core_hello`.
    pub fn new() -> Session {
        Session::default()
    }

    /// Validates the next Core frame. Returns `None` for `core_hello`.
    pub fn receive(&mut self, frame: &Object) -> Result<Option<CoreFrame>> {
        if self.state == State::Closed {
            return Err(ProtocolError::order("Core sent a frame after its terminal"));
        }
        if self.state == State::Hello {
            if frame.get("kind") != Some(&Json::from("core_hello")) {
                return Err(ProtocolError::order("first Core frame must be core_hello"));
            }
            let value = Json::Object(frame.clone());
            exact(&value, &["carrier", "kind", "limits", "versions"])?;
            if frame["carrier"] != Json::from(CARRIER) {
                return Err(ProtocolError::malformed("unknown carrier"));
            }
            let versions = as_array(&frame["versions"])?;
            if versions.is_empty() {
                return Err(ProtocolError::malformed("versions must be nonempty"));
            }
            let mut offered = false;
            for version in versions {
                offered |= as_str(version)? == PROTOCOL;
            }
            if Limits::from_json(&frame["limits"])? != HARD_LIMITS {
                return Err(ProtocolError::malformed("hello must advertise exactly the frozen limits"));
            }
            if !offered {
                return Err(ProtocolError::new("E1600", format!("Core does not offer {PROTOCOL}")));
            }
            self.state = State::Negotiating;
            return Ok(None);
        }
        let parsed = parse_core_frame(frame, &self.limits)?;
        match &parsed {
            CoreFrame::Request(request) => {
                if self.state != State::Running {
                    return Err(ProtocolError::order("effect request without an available invocation slot"));
                }
                if request.ordinal != self.requests.len() + 1 {
                    return Err(ProtocolError::order("request ordinal is not consecutive"));
                }
                if request.ordinal > self.limits.max_effect_requests {
                    return Err(ProtocolError::exceeded("Core exceeded max_effect_requests"));
                }
                self.requests.push(request.clone());
                self.pending = Some(request.ordinal);
                self.state = State::Waiting;
            }
            CoreFrame::Terminal(terminal) => {
                let allowed = match terminal {
                    Terminal::Outcome { .. } => matches!(self.state, State::Running | State::Waiting | State::Finishing),
                    Terminal::Fatal { .. } => {
                        matches!(self.state, State::Negotiating | State::Selected | State::ShuttingDown | State::Running | State::Finishing)
                    }
                    Terminal::ShutdownAck { .. } => self.state == State::ShuttingDown,
                };
                if !allowed {
                    return Err(ProtocolError::order(match terminal {
                        Terminal::Outcome { .. } => "outcome without a live invocation",
                        Terminal::Fatal { .. } => "fatal while a response is outstanding",
                        Terminal::ShutdownAck { .. } => "shutdown_ack without shutdown",
                    }));
                }
                self.pending = None;
                self.terminal = Some(terminal.clone());
                self.state = State::Closed;
            }
        }
        Ok(Some(parsed))
    }

    fn require(&self, states: &[State]) -> Result<()> {
        if !states.contains(&self.state) {
            return Err(ProtocolError::order(format!("message is not permitted in state {}", self.state.as_str())));
        }
        Ok(())
    }

    /// Chooses the session limits.
    pub fn select(&mut self, limits: Limits) -> Result<Object> {
        self.require(&[State::Negotiating])?;
        limits.validate()?;
        self.limits = limits;
        self.state = State::Selected;
        Ok(object([("kind", "host_select".into()), ("limits", Json::Object(limits.to_json())), ("protocol", PROTOCOL.into())]))
    }

    /// Renders the session's single invocation.
    pub fn invoke(&mut self, target: &Hash, spec: &Interface, args: &[Value], operations: &[OperationEntry]) -> Result<Object> {
        self.require(&[State::Selected])?;
        let frame = invoke_frame(target, spec, args, operations, &self.limits)?;
        self.state = State::Running;
        Ok(frame)
    }

    /// Ends a selected session without invoking.
    pub fn shutdown(&mut self) -> Result<Object> {
        self.require(&[State::Selected])?;
        self.state = State::ShuttingDown;
        Ok(object([("kind", "shutdown".into()), ("protocol", PROTOCOL.into())]))
    }

    fn respond(&mut self, mut fields: Object, terminal: bool) -> Object {
        let ordinal = self.pending.take().expect("a pending request");
        fields.insert("protocol".into(), PROTOCOL.into());
        fields.insert("invocation_id".into(), INVOCATION_ID.into());
        fields.insert("request_id".into(), format!("{ordinal:016x}").into());
        self.responses.push(fields.clone());
        self.state = if terminal { State::Finishing } else { State::Running };
        fields
    }

    /// The `effect_ok` frame for a value, validated without changing state.
    pub fn ok_frame(&self, value: &Value) -> Result<Object> {
        self.require(&[State::Waiting])?;
        let encoded = encode_value(value, &mut Budget::new(self.limits))?;
        let ordinal = self.pending.expect("waiting has a pending request");
        let frame = object([
            ("invocation_id", INVOCATION_ID.into()),
            ("kind", "effect_ok".into()),
            ("protocol", PROTOCOL.into()),
            ("request_id", format!("{ordinal:016x}").into()),
            ("value", encoded),
        ]);
        frame_bytes(&frame, self.limits.max_frame_bytes, self.limits.max_json_depth)?;
        Ok(frame)
    }

    /// Answers the pending request with a value.
    pub fn respond_ok(&mut self, value: &Value) -> Result<Object> {
        self.require(&[State::Waiting])?;
        let encoded = encode_value(value, &mut Budget::new(self.limits))?;
        Ok(self.respond(object([("kind", "effect_ok".into()), ("value", encoded)]), false))
    }

    /// Answers the pending request with a permitted failure.
    pub fn respond_failure(&mut self, category: FailureCategory, message: &str) -> Result<Object> {
        self.require(&[State::Waiting])?;
        if message.len() > self.limits.max_host_message_bytes {
            return Err(ProtocolError::exceeded("host message exceeds max_host_message_bytes"));
        }
        let fields = object([
            ("category", category.as_str().into()),
            ("completion", category.completion().into()),
            ("kind", "effect_failure".into()),
            ("message", message.into()),
        ]);
        Ok(self.respond(fields, true))
    }

    /// Withdraws the pending request.
    pub fn cancel(&mut self, reason: CancelReason, completion: &str) -> Result<Object> {
        self.require(&[State::Waiting])?;
        if !["not_started", "failed", "unknown"].contains(&completion) {
            return Err(ProtocolError::malformed("unknown cancellation enum"));
        }
        let reason = match reason {
            CancelReason::Cancelled => "cancelled",
            CancelReason::Timeout => "timeout",
            CancelReason::HostShutdown => "host_shutdown",
        };
        Ok(self.respond(object([("completion", completion.into()), ("kind", "cancel".into()), ("reason", reason.into())]), true))
    }
}
