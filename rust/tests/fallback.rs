//! The exit wait used where `waitid` is unavailable, forced on Linux. Runs in
//! its own test process because the switch is process-global.

#![cfg(target_os = "linux")]

use std::sync::atomic::Ordering;
use std::time::{Duration, Instant};

use jacquard_host::carrier::{Cancel, Options, TEST_WITHOUT_WAITID};
use jacquard_host::host::{Host, HostOptions, InvocationResult, OperationRegistry};
use jacquard_host::json::Json;
use jacquard_host::protocol::{BoundaryType, Interface};

fn invoke(mode: &str, deadline: Duration) -> InvocationResult {
    let env = std::env::vars().chain([("JACQUARD_RUST_STANDIN".into(), mode.into())]).collect();
    let carrier = Options { command: vec![env!("CARGO_BIN_EXE_jacquard-host-standin").into()], env: Some(env), deadline: Some(deadline) };
    let host = Host::new("unused", HostOptions { carrier, limits: None }).unwrap();
    let ready = jacquard_host::kit::load().unwrap().identities["kit-ready"].clone();
    let unit = Interface { parameters: vec![], result: BoundaryType::Tuple(vec![]), effects: vec![] };
    host.invoke(&ready, &unit, &[], &OperationRegistry::new(), &Cancel::new())
}

#[test]
fn the_fallback_exit_wait_obeys_the_deadline() {
    TEST_WITHOUT_WAITID.store(true, Ordering::SeqCst);
    let result = invoke("ok", Duration::from_secs(5));
    assert!(result.ok() && result.exit_status == Some(0), "{}", result.message());
    // The worker sends its terminal, closes stdout, and never exits.
    let start = Instant::now();
    let result = invoke("hang-after-close", Duration::from_millis(500));
    assert_eq!(result.host_evidence.get("reason"), Some(&Json::from("deadline")), "{}", result.message());
    assert!(start.elapsed() < Duration::from_secs(2), "returned after {:?}", start.elapsed());
    assert_eq!(result.signal.as_deref(), Some("SIGKILL"));
}
