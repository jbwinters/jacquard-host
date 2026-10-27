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

/// The count of `directory` once it is at most `limit`, or after two seconds.
/// A joined thread can stay listed in `/proc/self/task` briefly after it
/// exits, so an exact count taken at once can see one more; a leak persists.
fn settled(directory: &str, limit: usize) -> usize {
    let deadline = Instant::now() + Duration::from_secs(2);
    loop {
        let current = count(directory);
        if current <= limit || Instant::now() >= deadline {
            return current;
        }
        std::thread::sleep(Duration::from_millis(10));
    }
}

#[test]
fn invocations_release_their_pipes_and_threads() {
    assert!(invoke("ok").ok()); // warm up lazily opened files
    let (descriptors, threads) = (count("/proc/self/fd"), count("/proc/self/task"));
    for _ in 0..5 {
        let result = invoke("ok");
        assert!(result.ok(), "{}", result.message());
    }
    let grown = settled("/proc/self/fd", descriptors);
    assert!(grown <= descriptors, "descriptors grew from {descriptors} to {grown}");
    let grown = settled("/proc/self/task", threads);
    assert!(grown <= threads, "threads grew from {threads} to {grown}");
    // A descendant outside the process group keeps holding stderr after the
    // invocation returns; the host must still stop its threads and close its
    // pipes rather than wait for the descendant.
    let start = Instant::now();
    let result = invoke("escaped-descendant");
    let elapsed = start.elapsed();
    let pid: i32 = String::from_utf8_lossy(&result.stderr).parse().expect("descendant pid");
    let (after_descriptors, after_threads) = (settled("/proc/self/fd", descriptors), settled("/proc/self/task", threads));
    // SAFETY: the pid names the stand-in's own descendant.
    unsafe {
        kill(pid, 9);
    }
    assert!(result.kind == ResultKind::CarrierLoss && elapsed < Duration::from_secs(5), "{:?} after {elapsed:?}", result.kind);
    assert!(after_descriptors <= descriptors, "escaped descendant: descriptors grew from {descriptors} to {after_descriptors}");
    assert!(after_threads <= threads, "escaped descendant: threads grew from {threads} to {after_threads}");
}
