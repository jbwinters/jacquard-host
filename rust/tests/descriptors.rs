//! Runs alone in its own test process so descriptor and thread counts are
//! exact.

#![cfg(target_os = "linux")]

use std::time::{Duration, Instant};

use jacquard_host::carrier::{Cancel, Options};
use jacquard_host::host::{Host, HostOptions, InvocationResult, OperationRegistry, ResultKind};
use jacquard_host::protocol::{BoundaryType, Interface};

unsafe extern "C" {
    fn kill(pid: i32, signal: i32) -> i32;
}

fn invoke(mode: &str) -> InvocationResult {
    let env = std::env::vars().chain([("JACQUARD_RUST_STANDIN".into(), mode.into())]).collect();
    let carrier = Options {
        command: vec![env!("CARGO_BIN_EXE_jacquard-host-standin").into()],
        env: Some(env),
        deadline: Some(Duration::from_secs(5)),
    };
    let host = Host::new("unused", HostOptions { carrier, limits: None }).unwrap();
    let ready = jacquard_host::kit::load().unwrap().identities["kit-ready"].clone();
    let unit = Interface { parameters: vec![], result: BoundaryType::Tuple(vec![]), effects: vec![] };
    host.invoke(&ready, &unit, &[], &OperationRegistry::new(), &Cancel::new())
}

fn count(directory: &str) -> usize {
    std::fs::read_dir(directory).unwrap().count()
}

#[test]
fn invocations_release_their_pipes_and_threads() {
    assert!(invoke("ok").ok()); // warm up lazily opened files
    let (descriptors, threads) = (count("/proc/self/fd"), count("/proc/self/task"));
    for _ in 0..5 {
        let result = invoke("ok");
        assert!(result.ok(), "{}", result.message());
    }
    assert!(count("/proc/self/fd") <= descriptors, "descriptors grew from {descriptors} to {}", count("/proc/self/fd"));
    assert!(count("/proc/self/task") <= threads, "threads grew from {threads} to {}", count("/proc/self/task"));
    // A descendant outside the process group keeps holding stderr after the
    // invocation returns; the host must still stop its threads and close its
    // pipes rather than wait for the descendant.
    let start = Instant::now();
    let result = invoke("escaped-descendant");
    let pid: i32 = String::from_utf8_lossy(&result.stderr).parse().expect("descendant pid");
    let (after_descriptors, after_threads) = (count("/proc/self/fd"), count("/proc/self/task"));
    // SAFETY: the pid names the stand-in's own descendant.
    unsafe {
        kill(pid, 9);
    }
    assert!(
        result.kind == ResultKind::CarrierLoss && start.elapsed() < Duration::from_secs(5),
        "{:?} after {:?}",
        result.kind,
        start.elapsed()
    );
    assert!(after_descriptors <= descriptors, "escaped descendant: descriptors grew from {descriptors} to {after_descriptors}");
    assert!(after_threads <= threads, "escaped descendant: threads grew from {threads} to {after_threads}");
}
