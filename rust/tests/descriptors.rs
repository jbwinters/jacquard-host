//! Runs alone in its own test process so the descriptor count is exact.

#![cfg(target_os = "linux")]

use std::time::Duration;

use jacquard_host::carrier::{Cancel, Options};
use jacquard_host::host::{Host, HostOptions, OperationRegistry};
use jacquard_host::protocol::{BoundaryType, Interface};

#[test]
fn completed_invocations_release_their_pipes() {
    let env = std::env::vars().chain([("JACQUARD_RUST_STANDIN".into(), "ok".into())]).collect();
    let carrier = Options {
        command: vec![env!("CARGO_BIN_EXE_jacquard-host-standin").into()],
        env: Some(env),
        deadline: Some(Duration::from_secs(5)),
    };
    let host = Host::new("unused", HostOptions { carrier, limits: None }).unwrap();
    let ready = jacquard_host::kit::load().unwrap().identities["kit-ready"].clone();
    let unit = Interface { parameters: vec![], result: BoundaryType::Tuple(vec![]), effects: vec![] };
    let invoke = || host.invoke(&ready, &unit, &[], &OperationRegistry::new(), &Cancel::new());
    let descriptors = || std::fs::read_dir("/proc/self/fd").unwrap().count();
    assert!(invoke().ok()); // warm up lazily opened files
    let before = descriptors();
    for _ in 0..5 {
        let result = invoke();
        assert!(result.ok(), "{}", result.message());
    }
    let after = descriptors();
    assert!(after <= before, "open descriptors grew from {before} to {after}");
}
