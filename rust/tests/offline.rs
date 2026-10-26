//! Offline tests: strict codecs, the session state machine, kit replay
//! through the Rust carrier, and host fault handling against the stand-in.

use std::collections::BTreeMap;
use std::time::{Duration, Instant};

use jacquard_host::ProtocolError;
use jacquard_host::carrier::{Cancel, Options};
use jacquard_host::compat::{CARRIER, PROTOCOL};
use jacquard_host::host::{EVIDENCE_SCHEMA, Failure, Host, HostOptions, OperationRegistry, ResultKind};
use jacquard_host::json::{INT_MAX, INT_MIN, Json, Object, decode, decode_object, frame_bytes, object, stringify};
use jacquard_host::kit::{self, Kit, TRANSCRIPT_COUNT, Transcript, js_string, replay_one};
use jacquard_host::protocol::{
    BoundaryType, Budget, HARD_LIMITS, Hash, Interface, Limits, OperationEntry, Session, Value, decode_type, decode_value, encode_value,
    invoke_frame, parse_core_frame,
};

const STANDIN: &str = env!("CARGO_BIN_EXE_jacquard-host-standin");

fn code<T: std::fmt::Debug>(result: Result<T, ProtocolError>, expected: &str, context: &str) {
    match result {
        Err(error) if error.code == expected => {}
        other => panic!("{context}: expected {expected}, got {other:?}"),
    }
}

fn json(text: &str) -> Json {
    decode(text.as_bytes(), 64).unwrap()
}

fn hash(c: char) -> Hash {
    Hash::parse(&c.to_string().repeat(64)).unwrap()
}

fn unit() -> Interface {
    Interface { parameters: vec![], result: BoundaryType::Tuple(vec![]), effects: vec![] }
}

fn loaded() -> Kit {
    kit::load().unwrap()
}

fn find(kit: &Kit, name: &str) -> Transcript {
    kit.transcripts.iter().find(|transcript| transcript.name == name).cloned().unwrap()
}

fn ids() -> BTreeMap<String, Hash> {
    loaded().identities
}

#[test]
fn strict_json_refusals() {
    for text in [
        r#"{"a":1,"a":2}"#,
        r#"{"x":{"a":1,"a":2}}"#,
        r#"{"x":1.0}"#,
        r#"{"x":1e0}"#,
        r#"{"x":NaN}"#,
        r#"{"x":Infinity}"#,
        r#"{"x":"\ud800"}"#,
        r#"{"x":"\udc00"}"#,
        r#"{"x":"\ud800A"}"#,
        "{}{}",
        "[]",
        r#"{"x":4611686018427387904}"#,
        r#"{"x":01}"#,
        r#"{"x":true,}"#,
        r#"{"x":[1,]}"#,
        "{\"x\":\"\x01\"}",
        r#"{"x":"\q"}"#,
        r#"{"x":-}"#,
    ] {
        code(decode_object(text.as_bytes(), 64), "E1601", text);
    }
    code(decode_object(&[0x7b, 0x22, 0xff, 0x22, 0x3a, 0x30, 0x7d], 64), "E1601", "invalid UTF-8");
    let parsed = decode_object(r#"{"a":4611686018427387903,"b":-4611686018427387904,"c":"😀é"}"#.as_bytes(), 64).unwrap();
    assert_eq!(parsed["a"], Json::Int(INT_MAX));
    assert_eq!(parsed["b"], Json::Int(INT_MIN));
    assert_eq!(parsed["c"], Json::from("😀é"));
    let value = Json::Object(object([
        ("z", Json::Array(vec![Json::Null, Json::Bool(true), Json::Int(-1)])),
        ("a", "q\"\\\n\x01😀".into()),
        ("__proto__", Json::Object(Object::new())),
    ]));
    assert_eq!(stringify(&value, 64).unwrap(), r#"{"__proto__":{},"a":"q\"\\\n\u0001😀","z":[null,true,-1]}"#);
    code(stringify(&Json::Object(object([("x", Json::Int(INT_MAX + 1))])), 64), "E1601", "63-bit encode");
}

#[test]
fn depth_frame_and_limits() {
    code(decode_object(br#"{"a":[[]]}"#, 2), "E1602", "decode depth");
    code(frame_bytes(&object([("a", "xx".into())]), 4, 64), "E1602", "frame bytes");
    assert_eq!(frame_bytes(&object([("a", Json::Int(1))]), 64, 64).unwrap(), b"\x00\x00\x00\x07{\"a\":1}");
    code(Limits { max_arguments: 0, ..HARD_LIMITS }.validate(), "E1602", "zero limit");
    let mut wire = HARD_LIMITS.to_json();
    wire.insert("extra".into(), Json::Int(1));
    code(Limits::from_json(&Json::Object(wire)), "E1601", "extra limit");
    let mut wire = HARD_LIMITS.to_json();
    wire.insert("max_arguments".into(), Json::Bool(true));
    code(Limits::from_json(&Json::Object(wire)), "E1601", "boolean limit");
    let mut wire = HARD_LIMITS.to_json();
    wire.insert("max_arguments".into(), Json::Int(1 << 60));
    code(Limits::from_json(&Json::Object(wire)), "E1601", "limit beyond the exact integer range");
    assert_eq!(Limits::from_json(&Json::Object(HARD_LIMITS.to_json())).unwrap(), HARD_LIMITS);
}

#[test]
fn values_roundtrip_exactly() {
    let values = [
        Value::Int(0),
        Value::Int(INT_MIN),
        Value::Int(INT_MAX),
        Value::Text("hé😀".into()),
        Value::Hash(hash('a')),
        Value::Tuple(vec![Value::Int(3), Value::Text("x".into())]),
        Value::Constructor(hash('b'), vec![Value::Int(3)]),
        Value::Real(0x7ff0_0000_0000_0001),
        Value::real(-0.0),
        Value::real(f64::INFINITY),
        Value::Tuple(vec![]),
    ];
    for value in values {
        let first = encode_value(&value, &mut Budget::new(HARD_LIMITS)).unwrap();
        let decoded = decode_value(&first, &mut Budget::new(HARD_LIMITS)).unwrap();
        assert_eq!(decoded, value);
        let second = encode_value(&decoded, &mut Budget::new(HARD_LIMITS)).unwrap();
        assert_eq!(stringify(&first, 64).unwrap(), stringify(&second, 64).unwrap());
    }
    assert_eq!(Value::real(-0.0), Value::Real(0x8000_0000_0000_0000));
    for text in ["-0", "00", "+1", "4611686018427387904", "-4611686018427387905", " 1"] {
        let wire = Json::Object(object([("kind", "int".into()), ("value", text.into())]));
        code(decode_value(&wire, &mut Budget::new(HARD_LIMITS)), "E1601", text);
    }
    code(decode_value(&json(r#"{"kind":"secret","value":"x"}"#), &mut Budget::new(HARD_LIMITS)), "E1604", "secret");
    code(encode_value(&Value::Int(INT_MAX + 1), &mut Budget::new(HARD_LIMITS)), "E1604", "Int range");
    let upper = format!(r#"{{"kind":"nominal","identity":"{}","arguments":[]}}"#, "A".repeat(64));
    code(decode_type(&json(&upper), &mut Budget::new(HARD_LIMITS)), "E1601", "uppercase hash");
    let small = Limits { max_value_nodes: 1, ..HARD_LIMITS };
    code(decode_value(&json(r#"{"kind":"tuple","items":[{"kind":"int","value":"1"}]}"#), &mut Budget::new(small)), "E1602", "value nodes");
    code(decode_value(&json(r#"{"kind":"real","bits":"7FF0000000000001"}"#), &mut Budget::new(HARD_LIMITS)), "E1601", "uppercase real");
    code(Hash::parse("A"), "E1601", "short hash");
}

#[test]
fn decoders_follow_cores_check_order() {
    let item = r#"{"kind":"int","value":"1"}"#;
    let many = vec![item; 65].join(",");
    // Core: exact fields, one node, identity, then the count.
    let bad = format!(r#"{{"kind":"constructor","identity":"bad","arguments":[{many}]}}"#);
    code(decode_value(&json(&bad), &mut Budget::new(HARD_LIMITS)), "E1601", "constructor identity before count");
    let good = format!(r#"{{"kind":"constructor","identity":"{}","arguments":[{many}]}}"#, "a".repeat(64));
    code(decode_value(&json(&good), &mut Budget::new(HARD_LIMITS)), "E1602", "constructor argument count");
    // A malformed descriptor is refused as malformed even when the budget is spent.
    let spent = Limits { max_value_nodes: 0, ..HARD_LIMITS };
    let nominal = format!(r#"{{"kind":"nominal","identity":"{}"}}"#, "a".repeat(64));
    code(decode_type(&json(&nominal), &mut Budget::new(spent)), "E1601", "exact fields before the node");
    code(decode_value(&json(r#"{"kind":"secret"}"#), &mut Budget::new(spent)), "E1604", "kind before the node");
    code(decode_value(&json(r#"{"value":"1"}"#), &mut Budget::new(HARD_LIMITS)), "E1601", "missing kind");
}

fn hello() -> Object {
    object([
        ("carrier", CARRIER.into()),
        ("kind", "core_hello".into()),
        ("limits", Json::Object(HARD_LIMITS.to_json())),
        ("versions", Json::Array(vec![PROTOCOL.into()])),
    ])
}

#[test]
fn hello_and_selection() {
    let mut extra = hello();
    extra.insert("extra".into(), Json::Int(1));
    code(Session::new().receive(&extra), "E1601", "extra hello field");
    let mut other = hello();
    other.insert("versions".into(), Json::Array(vec!["jacquard-host-v9".into()]));
    code(Session::new().receive(&other), "E1600", "unknown version");
    let mut lowered = hello();
    lowered.insert("limits".into(), Json::Object(Limits { max_arguments: 1, ..HARD_LIMITS }.to_json()));
    code(Session::new().receive(&lowered), "E1601", "lowered hello limits");
    code(Session::new().receive(&object([("kind", "fatal".into()), ("protocol", PROTOCOL.into())])), "E1608", "first frame");
    let mut session = Session::new();
    session.receive(&hello()).unwrap();
    code(session.invoke(&hash('a'), &unit(), &[], &[]), "E1608", "invoke before select");
    code(session.select(Limits { max_frame_bytes: HARD_LIMITS.max_frame_bytes + 1, ..HARD_LIMITS }), "E1602", "select above ceiling");
}

#[test]
fn invoke_frame_validation() {
    let (a, b) = (hash('a'), hash('b'));
    let mut spec = Interface { effects: vec![b.clone(), a.clone()], ..unit() };
    code(invoke_frame(&a, &spec, &[], &[], &HARD_LIMITS), "E1601", "unsorted effects");
    spec.effects = vec![a.clone()];
    let entry = |effect: &Hash, operation: &Hash| OperationEntry { effect: effect.clone(), operation: operation.clone() };
    code(invoke_frame(&a, &spec, &[], &[entry(&b, &a)], &HARD_LIMITS), "E1601", "operation outside row");
    code(invoke_frame(&a, &spec, &[], &[entry(&a, &b), entry(&a, &b)], &HARD_LIMITS), "E1601", "duplicate operation");
    code(invoke_frame(&a, &spec, &[Value::Int(1)], &[], &HARD_LIMITS), "E1601", "argument count");
    let frame = invoke_frame(&a, &spec, &[], &[entry(&a, &b)], &HARD_LIMITS).unwrap();
    assert_eq!(
        stringify(&frame["capabilities"], 64).unwrap(),
        format!(r#"{{"effects":["{a}"],"operations":[{{"effect":"{a}","mode":"once","operation":"{b}"}}]}}"#)
    );
}

#[test]
fn js_string_matches_javascript_coercion() {
    assert_eq!(js_string(&"x".into()), "x");
    assert_eq!(js_string(&Json::Null), "null");
    assert_eq!(js_string(&Json::Int(5)), "5");
    assert_eq!(js_string(&Json::Bool(true)), "true");
    assert_eq!(js_string(&json(r#"[1,null,["a","b"]]"#)), "1,,a,b");
    assert_eq!(js_string(&json(r#"{"a":1}"#)), "[object Object]");
}

#[test]
fn pinned_kit_loads() {
    let kit = loaded();
    assert_eq!(kit.transcripts.len(), TRANSCRIPT_COUNT);
    assert!(kit.identities.contains_key("kit-echo"));
    assert!(kit::file("kit/fixtures.jac").is_some());
}

#[test]
fn outcome_evidence_and_result_share_one_frame_budget() {
    let kit = loaded();
    let outcome = &kit.transcripts[0].core_output[1];
    parse_core_frame(outcome, &Limits { max_value_nodes: 2, ..HARD_LIMITS }).unwrap();
    code(parse_core_frame(outcome, &Limits { max_value_nodes: 1, ..HARD_LIMITS }), "E1602", "pure outcome");
    // An error outcome spends its two evidence types only.
    let refused = find(&kit, "refused-authority").core_output;
    let error_outcome = refused.last().unwrap();
    parse_core_frame(error_outcome, &Limits { max_value_nodes: 2, ..HARD_LIMITS }).unwrap();
    code(parse_core_frame(error_outcome, &Limits { max_value_nodes: 1, ..HARD_LIMITS }), "E1602", "error outcome");
}

#[test]
fn session_enforces_one_invocation_and_local_terminal_refusal() {
    let kit = loaded();
    let pure = &kit.transcripts[0];
    let mut session = Session::new();
    session.receive(&pure.core_output[0]).unwrap();
    session.select(HARD_LIMITS).unwrap();
    session.invoke(&kit.identities["kit-ready"], &unit(), &[], &[]).unwrap();
    code(session.invoke(&hash('a'), &unit(), &[], &[]), "E1608", "second invoke");
    session.receive(&pure.core_output[1]).unwrap();
    code(session.shutdown(), "E1608", "shutdown after terminal");
    code(session.receive(&pure.core_output[1]), "E1608", "frame after terminal");
}

#[test]
fn request_ordering_and_one_response_slot() {
    let kit = loaded();
    let transcript = find(&kit, "one-once-effect-success");
    let mut session = Session::new();
    session.receive(&transcript.core_output[0]).unwrap();
    session.select(HARD_LIMITS).unwrap();
    let invoke = transcript.host_input.iter().map(|step| &step.frame).find(|frame| frame.get("kind") == Some(&"invoke".into())).unwrap();
    let interface = invoke["interface"].as_object().unwrap();
    let decode = |value: &Json| decode_type(value, &mut Budget::new(HARD_LIMITS)).unwrap();
    let spec = Interface {
        parameters: interface["parameters"].as_array().unwrap().iter().map(decode).collect(),
        result: decode(&interface["result"]),
        effects: interface["effects"].as_array().unwrap().iter().map(|effect| Hash::parse(effect.as_str().unwrap()).unwrap()).collect(),
    };
    let operations: Vec<OperationEntry> = invoke["capabilities"].as_object().unwrap()["operations"]
        .as_array()
        .unwrap()
        .iter()
        .map(|entry| {
            let entry = entry.as_object().unwrap();
            OperationEntry {
                effect: Hash::parse(entry["effect"].as_str().unwrap()).unwrap(),
                operation: Hash::parse(entry["operation"].as_str().unwrap()).unwrap(),
            }
        })
        .collect();
    let callable = Hash::parse(invoke["target"].as_object().unwrap()["callable"].as_str().unwrap()).unwrap();
    session.invoke(&callable, &spec, &[Value::Text("ping".into())], &operations).unwrap();
    let request = &transcript.core_output[1];
    let with = |key: &str, value: Json| {
        let mut frame = request.clone();
        frame.insert(key.into(), value);
        frame
    };
    code(session.receive(&with("invocation_id", Json::Int(0))), "E1601", "numeric invocation id");
    code(session.receive(&with("invocation_id", "ffffffffffffffff".into())), "E1608", "other invocation");
    code(session.receive(&with("request_id", "0000000000000002".into())), "E1608", "skipped ordinal");
    session.receive(request).unwrap();
    code(session.receive(request), "E1608", "second request while waiting");
    session.respond_ok(&Value::Text("pong".into())).unwrap();
    code(session.respond_ok(&Value::Text("again".into())), "E1608", "second response");
}

fn standin(mode: &str, case: &str, deadline: Duration) -> Options {
    Options {
        command: vec![STANDIN.into()],
        env: Some(
            std::env::vars()
                .chain([("JACQUARD_RUST_STANDIN".into(), mode.into()), ("JACQUARD_RUST_STANDIN_CASE".into(), case.into())])
                .collect(),
        ),
        deadline: Some(deadline),
    }
}

fn replay_standin(transcript: &Transcript, original: &str) -> kit::Replay {
    replay_one(transcript, "unused", &standin("replay", original, Duration::from_secs(10)), &Cancel::new())
}

#[test]
fn all_recordings_replay_through_the_rust_carrier() {
    for transcript in loaded().transcripts {
        let replay = replay_standin(&transcript, &transcript.name);
        assert!(
            replay.differences.is_empty() && replay.prose_drift.is_empty() && replay.status == transcript.status,
            "{}: differences {:?}, prose drift {:?}, status {}",
            transcript.name,
            replay.differences,
            replay.prose_drift,
            replay.status
        );
    }
}

#[test]
fn replay_treats_absent_expectations_like_the_reference() {
    let kit = loaded();
    let mut missing = find(&kit, "pure-success");
    missing.expect = Object::new();
    assert_eq!(replay_standin(&missing, "pure-success").status, "diverges", "absent terminal expectation conformed");
    let mut unbounded = find(&kit, "unknown-version");
    unbounded.expect.remove("terminal_frames_max");
    assert_eq!(replay_standin(&unbounded, "unknown-version").status, "diverges", "missing terminal_frames_max conformed");
}

#[test]
fn prose_drift_is_reported_without_normalizing() {
    let kit = loaded();
    let original = find(&kit, "refused-authority");
    let mut modified = original.clone();
    let last = modified.core_output.last_mut().unwrap();
    let Some(Json::Object(result)) = last.get_mut("result") else { panic!("no result") };
    let Some(Json::Array(diagnostics)) = result.get_mut("diagnostics") else { panic!("no diagnostics") };
    let Json::Object(diagnostic) = &mut diagnostics[0] else { panic!("no diagnostic") };
    let summary = format!("{} changed", diagnostic["summary"].as_str().unwrap());
    diagnostic.insert("summary".into(), summary.into());
    let replay = replay_standin(&modified, &original.name);
    assert!(replay.differences.is_empty() && replay.prose_drift.len() == 1, "{:?} {:?}", replay.differences, replay.prose_drift);
    let summary_of =
        |frame: &Object| frame["result"].as_object().unwrap()["diagnostics"].as_array().unwrap()[0].as_object().unwrap()["summary"].clone();
    assert_eq!(summary_of(replay.core_frames.last().unwrap()), summary_of(original.core_output.last().unwrap()));
}

fn faulty(mode: &str, deadline: Duration, limits: Option<Limits>) -> Host {
    Host::new("unused", HostOptions { carrier: standin(mode, "", deadline), limits }).unwrap()
}

fn invoke_ready(host: &Host) -> jacquard_host::host::InvocationResult {
    host.invoke(&ids()["kit-ready"], &unit(), &[], &OperationRegistry::new(), &Cancel::new())
}

fn reason(result: &jacquard_host::host::InvocationResult) -> Option<&str> {
    result.host_evidence.get("reason").and_then(Json::as_str)
}

#[test]
fn malformed_extra_and_noncanonical_core_frames_fail_closed() {
    for (mode, expected) in [("unknown-field", "E1601"), ("invalid-hash", "E1601"), ("extra", "E1608")] {
        let result = invoke_ready(&faulty(mode, Duration::from_secs(2), None));
        assert!(result.kind == ResultKind::ProtocolError && result.code() == expected, "{mode}: {:?} {}", result.kind, result.message());
    }
}

#[test]
fn carrier_failures_retain_host_evidence() {
    for mode in ["exit", "truncated", "silent", "stall", "nonzero"] {
        let result = invoke_ready(&faulty(mode, Duration::from_secs(1), None));
        assert!(
            result.kind == ResultKind::CarrierLoss && result.code() == "E1611" && result.host_evidence["schema"] == EVIDENCE_SCHEMA.into(),
            "{mode}: {:?} {:?}",
            result.kind,
            result.host_evidence
        );
        let saw_outcome = result.core_frames.iter().any(|frame| frame.get("kind") == Some(&"outcome".into()));
        assert_eq!(matches!(mode, "stall" | "nonzero"), saw_outcome, "{mode}: outcome observed");
        let expired = matches!(mode, "silent" | "stall");
        assert_eq!(expired, reason(&result) == Some("deadline"), "{mode}: {:?}", reason(&result));
        let failure = result.into_value().unwrap_err().failure();
        assert_eq!(failure, if expired { Failure::DeadlineExceeded } else { Failure::WorkerLost }, "{mode}");
    }
}

#[test]
fn frames_after_selection_are_read_under_the_selected_limits() {
    let limits = Limits { max_frame_bytes: 1024, ..HARD_LIMITS };
    let result = invoke_ready(&faulty("oversized-early", Duration::from_secs(2), Some(limits)));
    assert!(result.kind == ResultKind::ProtocolError && result.code() == "E1602", "{:?} {}", result.kind, result.message());
}

#[test]
fn stderr_is_drained_and_retention_obeys_the_selected_ceiling() {
    let limits = Limits { max_stderr_bytes: 17, ..HARD_LIMITS };
    let result = invoke_ready(&faulty("stderr", Duration::from_secs(5), Some(limits)));
    assert!(result.ok() && result.stderr.len() == 17, "{}, stderr {} bytes", result.message(), result.stderr.len());
}

#[test]
fn backpressure_spends_the_same_lifetime() {
    let ids = ids();
    let text = BoundaryType::Nominal(ids["text"].clone(), vec![]);
    let spec = Interface { parameters: vec![text.clone()], result: text, effects: vec![] };
    let start = Instant::now();
    let host = faulty("no-read", Duration::from_secs(1), None);
    let result = host.invoke(&ids["kit-ready"], &spec, &[Value::Text("x".repeat(220_000))], &OperationRegistry::new(), &Cancel::new());
    assert!(
        reason(&result) == Some("deadline") && start.elapsed() < Duration::from_secs(4),
        "{:?} after {:?}",
        reason(&result),
        start.elapsed()
    );
    assert_eq!(result.into_value().unwrap_err().failure(), Failure::DeadlineExceeded);
}

#[test]
fn cancellation_aborts_without_retry() {
    let cancel = Cancel::new();
    let trigger = cancel.clone();
    std::thread::spawn(move || {
        std::thread::sleep(Duration::from_millis(100));
        trigger.cancel();
    });
    let result =
        faulty("silent", Duration::from_secs(5), None).invoke(&ids()["kit-ready"], &unit(), &[], &OperationRegistry::new(), &cancel);
    assert_eq!(reason(&result), Some("aborted"));
    assert_eq!(result.into_value().unwrap_err().failure(), Failure::WorkerLost);
}

#[test]
fn local_refusal_starts_no_worker_and_invalid_options_are_rejected() {
    let missing = Options { command: vec!["/no/such/executable".into()], ..Options::default() };
    let limits = Limits { max_frame_bytes: 20, ..HARD_LIMITS };
    let host = Host::new("unused", HostOptions { carrier: missing.clone(), limits: Some(limits) }).unwrap();
    let result = invoke_ready(&host);
    assert!(
        result.kind == ResultKind::HostError && result.code() == "E1602" && result.exit_status.is_none(),
        "{:?} {}",
        result.kind,
        result.code()
    );
    let spawn = Host::new("unused", HostOptions { carrier: missing, limits: None }).unwrap();
    assert_eq!(reason(&invoke_ready(&spawn)), Some("worker_spawn_failed"));
    for options in [
        HostOptions { carrier: Options { deadline: Some(Duration::ZERO), ..Options::default() }, limits: None },
        HostOptions { carrier: Options { command: vec![String::new()], ..Options::default() }, limits: None },
        HostOptions { limits: Some(Limits { max_arguments: 0, ..HARD_LIMITS }), ..HostOptions::default() },
    ] {
        assert!(Host::new("unused", options.clone()).is_err(), "accepted {options:?}");
    }
}

#[test]
fn registry_refuses_duplicates_and_sorts_entries() {
    let (a, b) = (hash('a'), hash('b'));
    let mut registry = OperationRegistry::new();
    registry.bind(b.clone(), a.clone(), |_, _| Ok(Value::Tuple(vec![]))).unwrap();
    registry.bind(a.clone(), b.clone(), |_, _| Ok(Value::Tuple(vec![]))).unwrap();
    assert!(registry.bind(a.clone(), b.clone(), |_, _| Ok(Value::Tuple(vec![]))).is_err());
    let entries = registry.entries();
    assert_eq!((&entries[0].effect, &entries[1].effect), (&a, &b));
}

#[cfg(unix)]
mod unix {
    use super::*;

    unsafe extern "C" {
        fn kill(pid: i32, signal: i32) -> i32;
    }

    fn alive(pid: i32) -> bool {
        // SAFETY: signal 0 only checks for existence.
        unsafe { kill(pid, 0) == 0 }
    }

    fn sigkill(pid: i32) {
        // SAFETY: the pid names the stand-in's own descendant.
        unsafe {
            kill(pid, 9);
        }
    }

    fn descendant(mode: &str, stderr: &[u8]) -> i32 {
        String::from_utf8_lossy(stderr).parse().unwrap_or_else(|_| panic!("{mode}: stand-in stderr {stderr:?}"))
    }

    /// Waits for the process named by a stand-in's stderr to exit.
    fn descendant_dies(mode: &str, stderr: &[u8]) {
        let pid = descendant(mode, stderr);
        let deadline = Instant::now() + Duration::from_secs(2);
        while alive(pid) {
            if Instant::now() > deadline {
                sigkill(pid);
                panic!("{mode}: descendant survived the worker");
            }
            std::thread::sleep(Duration::from_millis(10));
        }
    }

    #[test]
    fn deadline_stops_descendants_holding_the_pipes() {
        let start = Instant::now();
        let result = invoke_ready(&faulty("descendant", Duration::from_millis(500), None));
        assert!(
            start.elapsed() < Duration::from_secs(3) && reason(&result) == Some("deadline"),
            "{:?} {:?}",
            start.elapsed(),
            result.host_evidence
        );
        descendant_dies("descendant", &result.stderr);
    }

    #[test]
    fn deadline_during_the_exit_grace_is_reported_as_the_deadline() {
        let result = invoke_ready(&faulty("stderr-descendant", Duration::from_millis(500), None));
        assert_eq!(reason(&result), Some("deadline"), "{}", result.message());
        if cfg!(target_os = "linux") {
            descendant_dies("stderr-descendant", &result.stderr);
        } else {
            sigkill(descendant("stderr-descendant", &result.stderr));
        }
    }

    #[test]
    fn slow_clean_exit_succeeds() {
        let start = Instant::now();
        let result = invoke_ready(&faulty("slow-exit", Duration::from_secs(5), None));
        assert!(result.ok() && start.elapsed() >= Duration::from_millis(1500), "{} after {:?}", result.message(), start.elapsed());
    }

    #[test]
    fn escaped_descendant_does_not_hold_the_host() {
        let start = Instant::now();
        let result = invoke_ready(&faulty("escaped-descendant", Duration::from_secs(10), None));
        sigkill(descendant("escaped-descendant", &result.stderr));
        assert!(
            !result.ok() && result.kind == ResultKind::CarrierLoss && start.elapsed() < Duration::from_secs(5),
            "{:?} after {:?}",
            result.kind,
            start.elapsed()
        );
    }

    /// Post-exit group cleanup needs `waitid(WNOWAIT)`, so it is Linux-only.
    #[cfg(target_os = "linux")]
    #[test]
    fn descendant_holding_stderr_after_exit_fails_closed() {
        for (mode, status) in [("stderr-descendant", 0), ("stderr-descendant-nonzero", 74)] {
            let result = invoke_ready(&faulty(mode, Duration::from_secs(5), None));
            assert!(!result.ok() && result.kind == ResultKind::CarrierLoss && result.code() == "E1611", "{mode}: {}", result.message());
            assert_eq!(result.exit_status, Some(status), "{mode}");
            descendant_dies(mode, &result.stderr);
        }
    }
}
