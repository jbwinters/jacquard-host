//! The pinned `host-protocol-v0` conformance kit, embedded and verified, and
//! a replayer that drives its recordings, including the intentional hostile
//! host inputs, against a worker through the independent Rust carrier.

use std::collections::BTreeMap;

use crate::carrier::{Cancel, CarrierError, Options, Worker};
use crate::compat::{CARRIER, CORE_VERSION, KIT_DIGESTS, PENDING_DECISIONS, PROTOCOL};
use crate::json::{Json, Object, decode, frame_bytes, object, stringify};
use crate::protocol::{HARD_LIMITS, Hash};
use crate::sha256::hex_digest;

/// The number of recordings in the pinned kit.
pub const TRANSCRIPT_COUNT: usize = 21;

const FILES: [(&str, &[u8]); 5] = [
    ("kit/kit.json", include_bytes!("../kit/host-protocol-v0/kit/kit.json")),
    ("kit/transcripts.json", include_bytes!("../kit/host-protocol-v0/kit/transcripts.json")),
    ("kit/fixtures.jac", include_bytes!("../kit/host-protocol-v0/kit/fixtures.jac")),
    ("kit/README.md", include_bytes!("../kit/host-protocol-v0/kit/README.md")),
    ("vectors.json", include_bytes!("../kit/host-protocol-v0/vectors.json")),
];

/// One pinned kit file, such as `"kit/fixtures.jac"`.
pub fn file(name: &str) -> Option<&'static [u8]> {
    FILES.iter().find(|(path, _)| *path == name).map(|(_, data)| *data)
}

/// One host-side action in a recording.
#[derive(Clone, Debug)]
pub struct Step {
    pub direction: String,
    pub raw_hex: Option<String>,
    pub frame: Object,
    pub after_terminal: bool,
}

/// One positive or hostile recording.
#[derive(Clone, Debug)]
pub struct Transcript {
    pub name: String,
    pub kind: String,
    pub phase: String,
    pub expect: Object,
    pub observed: Object,
    pub status: String,
    pub exit_status: i64,
    pub core_output: Vec<Object>,
    pub host_input: Vec<Step>,
}

/// The verified, decoded pinned kit.
#[derive(Clone, Debug)]
pub struct Kit {
    pub manifest: Object,
    pub transcripts: Vec<Transcript>,
    pub identities: BTreeMap<String, Hash>,
}

fn text(object: &Object, key: &str) -> String {
    object.get(key).and_then(Json::as_str).unwrap_or("").to_owned()
}

fn decode_file(name: &str) -> Result<Json, String> {
    decode(file(name).ok_or_else(|| format!("missing kit file {name}"))?, 256).map_err(|error| format!("{name}: {error}"))
}

/// Verifies the embedded files against the pinned digests and decodes them.
pub fn load() -> Result<Kit, String> {
    for (name, expected) in KIT_DIGESTS {
        let data = file(name).ok_or_else(|| format!("missing kit file {name}"))?;
        if hex_digest(data) != expected {
            return Err(format!("pinned conformance digest mismatch: {name}"));
        }
    }
    let (Json::Object(manifest), Json::Array(entries)) = (decode_file("kit/kit.json")?, decode_file("kit/transcripts.json")?) else {
        return Err("kit files have an unexpected shape".into());
    };
    let pending: Vec<String> = manifest
        .get("pending_decisions")
        .and_then(Json::as_array)
        .unwrap_or(&[])
        .iter()
        .map(|decision| decision.as_object().map_or_else(String::new, |entry| text(entry, "name")))
        .collect();
    if manifest.get("protocol") != Some(&Json::from(PROTOCOL))
        || manifest.get("carrier") != Some(&Json::from(CARRIER))
        || manifest.get("core_version") != Some(&Json::from(CORE_VERSION))
        || entries.len() != TRANSCRIPT_COUNT
        || pending != PENDING_DECISIONS
    {
        return Err("kit metadata does not match the pin".into());
    }
    let mut identities = BTreeMap::new();
    if let Some(Json::Object(map)) = manifest.get("identities") {
        for (name, value) in map {
            let hash = Hash::parse(value.as_str().unwrap_or("")).map_err(|error| error.to_string())?;
            identities.insert(name.clone(), hash);
        }
    }
    let transcripts = entries.iter().map(parse_transcript).collect::<Result<_, _>>()?;
    Ok(Kit { manifest, transcripts, identities })
}

fn parse_transcript(value: &Json) -> Result<Transcript, String> {
    let entry = value.as_object().ok_or("transcript is not an object")?;
    let name = text(entry, "name");
    let objects = |key: &str| -> Result<Vec<&Object>, String> {
        entry
            .get(key)
            .and_then(Json::as_array)
            .unwrap_or(&[])
            .iter()
            .map(|item| item.as_object().ok_or_else(|| format!("{name}: {key} entry is not an object")))
            .collect()
    };
    let core_output = objects("core_output")?.into_iter().cloned().collect();
    let host_input = objects("host_input")?
        .into_iter()
        .map(|step| Step {
            direction: text(step, "direction"),
            raw_hex: step.get("raw_hex").and_then(Json::as_str).map(str::to_owned),
            frame: step.get("frame").and_then(Json::as_object).cloned().unwrap_or_default(),
            after_terminal: step.get("after_terminal") == Some(&Json::Bool(true)),
        })
        .collect();
    let (Some(expect), Some(observed)) = (entry.get("expect").and_then(Json::as_object), entry.get("observed").and_then(Json::as_object))
    else {
        return Err("transcript is missing required fields".into());
    };
    if name.is_empty() {
        return Err("transcript is missing required fields".into());
    }
    Ok(Transcript {
        kind: text(entry, "kind"),
        phase: text(entry, "phase"),
        status: text(entry, "status"),
        expect: expect.clone(),
        observed: observed.clone(),
        exit_status: entry.get("exit_status").and_then(Json::as_int).unwrap_or(0),
        core_output,
        host_input,
        name,
    })
}

/// The observed result of replaying one recording.
#[derive(Clone, Debug)]
pub struct Replay {
    pub name: String,
    pub matches_recording: bool,
    /// `"conforms"` or `"diverges"`.
    pub status: String,
    pub differences: Vec<String>,
    pub prose_drift: Vec<String>,
    pub summary: Object,
    pub core_frames: Vec<Object>,
    pub exit_status: Option<i32>,
    pub signal: Option<String>,
    pub stderr: Vec<u8>,
}

fn is_terminal(frame: &Object) -> bool {
    matches!(frame.get("kind").and_then(Json::as_str), Some("outcome" | "fatal" | "shutdown_ack"))
}

const PROSE: [&str; 4] = ["summary", "cause", "next_step", "contrast"];
const DIAGNOSTIC_SCHEMA: &str = "jacquard-diagnostic-v1";

fn is_diagnostic(object: &Object) -> bool {
    object.get("schema") == Some(&Json::from(DIAGNOSTIC_SCHEMA))
}

fn strip_prose(value: &Json) -> Json {
    match value {
        Json::Array(items) => Json::Array(items.iter().map(strip_prose).collect()),
        Json::Object(fields) => Json::Object(
            fields
                .iter()
                .filter(|(key, _)| !(is_diagnostic(fields) && PROSE.contains(&key.as_str())))
                .map(|(key, item)| (key.clone(), strip_prose(item)))
                .collect(),
        ),
        other => other.clone(),
    }
}

fn prose_drift(expected: &Json, actual: Option<&Json>, path: &str) -> Vec<String> {
    match (expected, actual) {
        (Json::Array(items), Some(Json::Array(others))) => {
            items.iter().enumerate().flat_map(|(i, item)| prose_drift(item, others.get(i), &format!("{path}/{i}"))).collect()
        }
        (Json::Object(fields), Some(Json::Object(others))) if is_diagnostic(fields) => PROSE
            .iter()
            .filter(|key| canonical_option(fields.get(**key)) != canonical_option(others.get(**key)))
            .map(|key| format!("{path}/{key}"))
            .collect(),
        (Json::Object(fields), Some(Json::Object(others))) => {
            fields.iter().flat_map(|(key, item)| prose_drift(item, others.get(key), &format!("{path}/{key}"))).collect()
        }
        _ => vec![],
    }
}

/// Mirrors JavaScript's `String()` for the JSON values `kit.ts` coerces.
pub fn js_string(value: &Json) -> String {
    match value {
        Json::Str(text) => text.clone(),
        Json::Null => "null".into(),
        Json::Bool(flag) => flag.to_string(),
        Json::Int(number) => number.to_string(),
        Json::Array(items) => {
            items.iter().map(|item| if *item == Json::Null { String::new() } else { js_string(item) }).collect::<Vec<_>>().join(",")
        }
        Json::Object(_) => "[object Object]".into(),
    }
}

/// Compares decoded frames. Frames are at most `max_json_depth` deep, so the
/// 256 bound never refuses one; keys are sorted, matching the kit's canonical
/// JSON.
fn canonical(value: &Json) -> String {
    stringify(value, 256).unwrap_or_else(|error| format!("unencodable: {error}"))
}

/// An absent prose field compares equal to `null`, as in `kit.ts`.
fn canonical_option(value: Option<&Json>) -> String {
    canonical(value.unwrap_or(&Json::Null))
}

fn frames(list: &[Object]) -> Json {
    Json::Array(list.iter().cloned().map(Json::Object).collect())
}

fn hex_bytes(text: &str) -> Result<Vec<u8>, String> {
    if !text.len().is_multiple_of(2) || !text.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err("raw_hex is not hexadecimal".into());
    }
    (0..text.len()).step_by(2).map(|i| u8::from_str_radix(&text[i..i + 2], 16).map_err(|error| error.to_string())).collect()
}

struct Drive {
    observed: Vec<Object>,
    ended: bool,
    skipped: i64,
    refusal: Json,
}

impl Drive {
    fn read(&mut self, worker: &mut Worker) -> Result<bool, CarrierError> {
        let Some(frame) = worker.read_frame()? else {
            return Ok(false);
        };
        self.ended |= is_terminal(&frame);
        self.observed.push(frame);
        Ok(true)
    }

    fn run(&mut self, worker: &mut Worker, transcript: &Transcript) -> Result<(), String> {
        for step in &transcript.host_input {
            if step.direction == "core_to_host" {
                self.read(worker).map_err(|error| error.to_string())?;
            } else if let Some(raw) = &step.raw_hex {
                worker.write_bytes(hex_bytes(raw)?).map_err(|error| error.to_string())?;
                worker.close_input();
            } else if step.after_terminal && self.ended {
                self.refusal = "E1608".into();
            } else if self.ended {
                self.skipped += 1;
            } else {
                let data =
                    frame_bytes(&step.frame, HARD_LIMITS.max_frame_bytes, HARD_LIMITS.max_json_depth).map_err(|error| error.to_string())?;
                match worker.write_bytes(data) {
                    Err(CarrierError::Loss { message, .. }) if message.starts_with("worker write failed") => {}
                    other => other.map_err(|error| error.to_string())?,
                }
            }
        }
        worker.close_input();
        while self.read(worker).map_err(|error| error.to_string())? {}
        worker.wait().map_err(|error| error.to_string())
    }
}

/// Drives one recording against the worker and compares what Core sends
/// with the recording, modulo non-normative diagnostic prose.
pub fn replay_one(transcript: &Transcript, store: &str, options: &Options, cancel: &Cancel) -> Replay {
    let mut replay = Replay {
        name: transcript.name.clone(),
        matches_recording: false,
        status: "diverges".into(),
        differences: vec![],
        prose_drift: vec![],
        summary: Object::new(),
        core_frames: vec![],
        exit_status: None,
        signal: None,
        stderr: vec![],
    };
    let mut worker = match Worker::start(store, options, cancel.clone()) {
        Ok(worker) => worker,
        Err(message) => {
            replay.differences.push(message);
            return replay;
        }
    };
    let mut drive = Drive { observed: vec![], ended: false, skipped: 0, refusal: Json::Null };
    if let Err(message) = drive.run(&mut worker, transcript) {
        replay.differences.push(message);
    }
    worker.dispose();
    let observed = drive.observed;
    replay.exit_status = worker.exit_status;
    replay.signal = worker.signal.clone();
    replay.stderr = worker.stderr();

    let none = Object::new();
    let end = observed.iter().find(|frame| is_terminal(frame)).unwrap_or(&none);
    let end_kind = end.get("kind").and_then(Json::as_str);
    let result = if end_kind == Some("outcome") { end.get("result").and_then(Json::as_object) } else { None };
    let diagnostics = if end_kind == Some("fatal") {
        end.get("diagnostics")
    } else if result.and_then(|result| result.get("kind")) == Some(&Json::from("error")) {
        result.and_then(|result| result.get("diagnostics"))
    } else {
        None
    };
    let diagnostic = diagnostics.and_then(Json::as_array).and_then(|list| list.first()).and_then(Json::as_object).unwrap_or(&none);
    let primary = diagnostic.get("code").cloned().unwrap_or(Json::Null);
    let terminal_name = match end_kind {
        Some("fatal") => Json::from("fatal"),
        Some("shutdown_ack") => Json::from("shutdown"),
        _ => end
            .get("evidence")
            .and_then(Json::as_object)
            .and_then(|evidence| evidence.get("core"))
            .and_then(Json::as_object)
            .and_then(|core| core.get("terminal"))
            .cloned()
            .unwrap_or(Json::Null),
    };
    let requests = observed.iter().filter(|frame| frame.get("kind") == Some(&Json::from("effect_request"))).count() as i64;
    let terminals = observed.iter().filter(|frame| is_terminal(frame)).count() as i64;
    replay.summary = object([
        ("effect_requests", requests.into()),
        ("host_inputs_skipped_after_terminal", drive.skipped.into()),
        ("host_local_refusal", drive.refusal.clone()),
        ("primary_code", primary.clone()),
        ("terminal", terminal_name.clone()),
        ("terminal_frames", terminals.into()),
        ("terminal_kind", end.get("kind").cloned().unwrap_or(Json::Null)),
    ]);
    let exit_is = |code: i64| replay.exit_status.map(i64::from) == Some(code);
    // As in kit.ts, an absent expectation never equals an observed value or null.
    let expects = |key: &str, observed: &Json| transcript.expect.get(key) == Some(observed);
    let same_frames = canonical(&strip_prose(&frames(&observed))) == canonical(&strip_prose(&frames(&transcript.core_output)));
    let conforms = if transcript.kind == "positive" {
        let mut conforms = expects("terminal", &terminal_name) && exit_is(0) && same_frames;
        if let Some(step) = transcript.host_input.iter().find(|step| step.frame.get("kind") == Some(&Json::from("effect_failure")))
            && let Some(message) = step.frame.get("message")
        {
            let expected = js_string(message);
            conforms &= diagnostic.get("cause").and_then(Json::as_str).is_some_and(|cause| cause.ends_with(&expected));
        }
        conforms
    } else {
        let expect = &transcript.expect;
        let maximum = expect.get("terminal_frames_max").and_then(Json::as_int);
        let forbid = expect.get("forbid_outside_action") == Some(&Json::Bool(true));
        let exit = if expect.get("code") == Some(&Json::from("E1611")) { 74 } else { 0 };
        let mut conforms = expects("effect_requests_before_failure", &Json::Int(requests))
            && maximum.is_some_and(|maximum| terminals <= maximum)
            && (!forbid || requests == 0)
            && exit_is(exit);
        if transcript.phase == "terminal" {
            conforms &= expects("code", &drive.refusal) && terminal_name == Json::from("ok");
        } else {
            conforms &= expects("code", &primary);
        }
        conforms
    };
    replay.status = if conforms { "conforms" } else { "diverges" }.into();
    if replay.status != transcript.status {
        replay.differences.push("conformance status differs from the recording".into());
    }
    if !exit_is(transcript.exit_status) {
        replay.differences.push("exit status differs from the recording".into());
    }
    if canonical(&Json::Object(replay.summary.clone())) != canonical(&Json::Object(transcript.observed.clone())) {
        replay.differences.push("observed summary differs from the recording".into());
    }
    if !same_frames {
        replay.differences.push("Core frames differ semantically from the recording".into());
    }
    replay.matches_recording = replay.differences.is_empty();
    replay.prose_drift = prose_drift(&frames(&transcript.core_output), Some(&frames(&observed)), "");
    replay.core_frames = observed;
    replay
}

/// Replays every recording in order, loading the pinned kit when `kit` is
/// `None`.
pub fn replay_kit(store: &str, options: &Options, kit: Option<&Kit>, cancel: &Cancel) -> Result<Vec<Replay>, String> {
    let loaded;
    let kit = match kit {
        Some(kit) => kit,
        None => {
            loaded = load()?;
            &loaded
        }
    };
    Ok(kit.transcripts.iter().map(|transcript| replay_one(transcript, store, options, cancel)).collect())
}
