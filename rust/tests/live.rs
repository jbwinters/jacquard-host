//! Live tests against the real Core named by `JACQUARD_BIN` and
//! `JACQUARD_PRELUDE`; each test skips without them.

use std::path::PathBuf;
use std::process::Command;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::mpsc;
use std::time::{Duration, Instant};

use jacquard_host::carrier::{Cancel, Options};
use jacquard_host::host::{Failure, HandlerContext, HandlerError, Host, HostOptions, OperationRegistry, ResultKind};
use jacquard_host::json::Json;
use jacquard_host::kit::{self, Kit, TRANSCRIPT_COUNT};
use jacquard_host::protocol::{BoundaryType, Interface, Value};

/// A fresh store holding the kit fixtures, removed on drop.
struct Live {
    root: PathBuf,
    store: String,
    env: Vec<(String, String)>,
    binary: String,
    kit: Kit,
}

impl Drop for Live {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

static NEXT: AtomicUsize = AtomicUsize::new(0);

fn prepare() -> Option<Live> {
    let (Ok(binary), Ok(prelude)) = (std::env::var("JACQUARD_BIN"), std::env::var("JACQUARD_PRELUDE")) else {
        eprintln!("skipped: live tests need JACQUARD_BIN and JACQUARD_PRELUDE");
        return None;
    };
    let root = std::env::temp_dir().join(format!("jacquard-host-rust-live-{}-{}", std::process::id(), NEXT.fetch_add(1, Ordering::SeqCst)));
    std::fs::create_dir_all(&root).unwrap();
    let source = root.join("fixtures.jac");
    std::fs::write(&source, kit::file("kit/fixtures.jac").unwrap()).unwrap();
    let store = root.join("store");
    let env: Vec<(String, String)> =
        std::env::vars().filter(|(key, _)| key != "JACQUARD_PRELUDE").chain([("JACQUARD_PRELUDE".into(), prelude)]).collect();
    let live = Live { store: store.to_string_lossy().into_owned(), root, env, binary, kit: kit::load().unwrap() };
    let output = Command::new(&live.binary)
        .arg("run")
        .arg(&source)
        .arg("--store")
        .arg(&store)
        .env_clear()
        .envs(live.env.iter().cloned())
        .output()
        .unwrap();
    assert!(output.status.success(), "installing fixtures: {}\n{}", output.status, String::from_utf8_lossy(&output.stderr));
    Some(live)
}

impl Live {
    fn options(&self, deadline: Option<Duration>) -> Options {
        Options { command: vec![self.binary.clone()], env: Some(self.env.clone()), deadline }
    }

    fn host(&self, deadline: Option<Duration>) -> Host {
        Host::new(&self.store, HostOptions { carrier: self.options(deadline), limits: None }).unwrap()
    }

    fn id(&self, name: &str) -> jacquard_host::protocol::Hash {
        self.kit.identities[name].clone()
    }

    fn echo(&self) -> Interface {
        let text = BoundaryType::Nominal(self.id("text"), vec![]);
        Interface { parameters: vec![text.clone()], result: text, effects: vec![self.id("kit-world")] }
    }

    fn registry<F>(&self, handler: F) -> OperationRegistry
    where
        F: Fn(&HandlerContext, &[Value]) -> Result<Value, HandlerError> + Send + Sync + 'static,
    {
        let mut registry = OperationRegistry::new();
        registry.bind(self.id("kit-world"), self.id("send"), handler).unwrap();
        registry
    }

    fn invoke_echo(&self, host: &Host, registry: &OperationRegistry) -> jacquard_host::host::InvocationResult {
        host.invoke(&self.id("kit-echo"), &self.echo(), &[Value::Text("ping".into())], registry, &Cancel::new())
    }
}

#[test]
fn live_core_matches_all_recordings() {
    let Some(live) = prepare() else { return };
    let replays = kit::replay_kit(&live.store, &live.options(None), Some(&live.kit), &Cancel::new()).unwrap();
    for replay in &replays {
        assert!(
            replay.differences.is_empty() && replay.prose_drift.is_empty(),
            "{}: {:?}, prose drift {:?}",
            replay.name,
            replay.differences,
            replay.prose_drift
        );
    }
    assert_eq!(replays.len(), TRANSCRIPT_COUNT);
}

#[test]
fn live_pure_and_effectful_invocations_retain_evidence() {
    let Some(live) = prepare() else { return };
    let host = live.host(None);
    let unit = Interface { parameters: vec![], result: BoundaryType::Tuple(vec![]), effects: vec![] };
    let pure = host.invoke(&live.id("kit-ready"), &unit, &[], &OperationRegistry::new(), &Cancel::new());
    assert_eq!(pure.exit_status, Some(0));
    assert_eq!(pure.into_value().unwrap(), Value::Tuple(vec![]));
    let calls = Arc::new(AtomicUsize::new(0));
    let counter = Arc::clone(&calls);
    let registry = live.registry(move |_, args| {
        counter.fetch_add(1, Ordering::SeqCst);
        match &args[0] {
            Value::Text(text) => Ok(Value::Text(format!("{text}!"))),
            _ => Err(HandlerError::outside_failed("expected text")),
        }
    });
    let result = live.invoke_echo(&host, &registry);
    assert_eq!(result.requests.len(), 1);
    assert_eq!(result.host_frames[2]["kind"], Json::from("effect_ok"));
    let terminal =
        result.evidence.as_ref().and_then(|evidence| evidence.get("core")).and_then(Json::as_object).map(|core| core["terminal"].clone());
    assert_eq!(terminal, Some(Json::from("ok")));
    assert_eq!(result.into_value().unwrap(), Value::Text("ping!".into()));
    assert_eq!(calls.load(Ordering::SeqCst), 1);
    let probe = host.shutdown_probe(&Cancel::new());
    assert_eq!(probe.kind, ResultKind::ShutdownAck, "{}", probe.message());
}

#[test]
fn live_failure_pairs_are_never_retried() {
    let Some(live) = prepare() else { return };
    type Make = fn(&str) -> HandlerError;
    let cases: [(Make, &str); 4] = [
        (|m| HandlerError::unsupported(m), "E1606"),
        (|m| HandlerError::refused(m), "E1607"),
        (|m| HandlerError::outside_failed(m), "E1613"),
        (|m| HandlerError::completion_unknown(m), "E1612"),
    ];
    for (failure, expected) in cases {
        let calls = Arc::new(AtomicUsize::new(0));
        let counter = Arc::clone(&calls);
        let registry = live.registry(move |_, _| {
            counter.fetch_add(1, Ordering::SeqCst);
            Err(failure("synthetic failure"))
        });
        let result = live.invoke_echo(&live.host(None), &registry);
        assert_eq!(result.code(), expected);
        assert_eq!(calls.load(Ordering::SeqCst), 1, "{expected}");
        assert!(result.diagnostics[0].cause.ends_with("synthetic failure"), "{expected}: {}", result.diagnostics[0].cause);
    }
    let unknown = live.registry(|_, _| Err(HandlerError::Unknown));
    let panics = live.registry(|_, _| panic!("private detail"));
    for (name, registry) in [("error", unknown), ("panic", panics)] {
        let result = live.invoke_echo(&live.host(None), &registry);
        assert!(result.code() == "E1612" && !result.message().contains("private detail"), "{name}: {}", result.message());
        assert_eq!(result.into_value().unwrap_err().failure(), Failure::OperationFailed, "{name}");
    }
}

#[test]
fn live_handler_deadline_kills_the_worker_without_a_late_response() {
    let Some(live) = prepare() else { return };
    for cooperative in [true, false] {
        let calls = Arc::new(AtomicUsize::new(0));
        let counter = Arc::clone(&calls);
        let (finished, observed) = mpsc::channel();
        let finished = std::sync::Mutex::new(finished);
        let registry = live.registry(move |context, _| {
            counter.fetch_add(1, Ordering::SeqCst);
            if cooperative {
                // Waits for cancellation.
                while !context.is_cancelled() {
                    std::thread::sleep(Duration::from_millis(5));
                }
            } else {
                // Ignores cancellation; the host abandons it.
                std::thread::sleep(Duration::from_secs(1));
            }
            let _ = finished.lock().unwrap().send(context.is_cancelled());
            Ok(Value::Text("late".into()))
        });
        let start = Instant::now();
        let result = live.invoke_echo(&live.host(Some(Duration::from_millis(250))), &registry);
        assert!(
            result.host_evidence.get("reason") == Some(&Json::from("deadline"))
                && result.code() == "E1611"
                && calls.load(Ordering::SeqCst) == 1
                && result.host_frames.len() == 2
                && start.elapsed() < Duration::from_secs(2),
            "cooperative={cooperative}: {:?} frames={} after {:?}",
            result.host_evidence,
            result.host_frames.len(),
            start.elapsed()
        );
        let cancelled = observed.recv_timeout(Duration::from_secs(3)).expect("handler did not finish");
        assert!(cancelled, "cooperative={cooperative}: handler context was not cancelled");
    }
}
