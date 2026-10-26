//! The example projects run as commands through the Rust SDK against the
//! real Core named by `JACQUARD_BIN` and `JACQUARD_PRELUDE`; each test skips
//! without them.

#[allow(dead_code)]
#[path = "../examples/jacquard-examples.rs"]
mod examples;

use std::path::PathBuf;
use std::sync::atomic::{AtomicUsize, Ordering};

use examples::Failure;
use jacquard_host::host::Failure as InvocationFailure;
use jacquard_host::json::{Json, decode};

static NEXT: AtomicUsize = AtomicUsize::new(0);

/// A scratch directory, removed on drop.
struct Scratch(PathBuf);

impl Drop for Scratch {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn scratch() -> Option<Scratch> {
    if std::env::var_os("JACQUARD_BIN").is_none() || std::env::var_os("JACQUARD_PRELUDE").is_none() {
        assert!(std::env::var_os("JACQUARD_REQUIRE_LIVE").is_none(), "live tests need JACQUARD_BIN and JACQUARD_PRELUDE");
        eprintln!("skipped: live tests need JACQUARD_BIN and JACQUARD_PRELUDE");
        return None;
    }
    let root =
        std::env::temp_dir().join(format!("jacquard-host-rust-examples-{}-{}", std::process::id(), NEXT.fetch_add(1, Ordering::SeqCst)));
    std::fs::create_dir_all(&root).unwrap();
    Some(Scratch(root))
}

fn command(arguments: &[&str]) -> (String, Result<(), Failure>) {
    let mut out = Vec::new();
    let arguments: Vec<String> = arguments.iter().map(|argument| argument.to_string()).collect();
    let result = examples::run(&arguments, &mut out);
    (String::from_utf8(out).unwrap(), result)
}

#[test]
fn live_all_four_projects_run_as_commands() {
    let Some(root) = scratch() else { return };
    for project in examples::PROJECTS {
        let workdir = root.0.join(project);
        let workdir = workdir.to_str().unwrap();
        let (output, result) = command(&["--workdir", workdir, project]);
        assert!(result.is_ok(), "{project}: {result:?}\n{output}");
        match project {
            "quickstart" => assert_eq!(output, "Order total 35: shipping 5\nOrder total 75: shipping 0\n"),
            "planner" => assert!(output.contains("\"score\": 16") && output.contains("planner.plan: ok"), "{output}"),
            "triage" => {
                let rows = decode(output.trim().as_bytes(), 16).unwrap();
                let urgencies: Vec<&str> =
                    rows.as_array().unwrap().iter().map(|row| row.as_object().unwrap()["urgency"].as_str().unwrap()).collect();
                assert_eq!(urgencies, ["PAGE", "TODAY", "WATCH"], "{output}");
            }
            _ => {
                assert!(output.contains("\"$tag\": \"Fulfilled\""), "{output}");
                let (repeated, result) = command(&["--workdir", workdir, project]);
                assert!(result.is_ok() && repeated.contains("DuplicateOrder") && repeated.contains("\"deliveries\":1"), "{repeated}");
            }
        }
    }
    let recovery = root.0.join("recovery");
    let recovery = recovery.to_str().unwrap();
    let (output, result) = command(&["--workdir", recovery, "--fault", "publish-failed", "fulfillment"]);
    match result {
        Err(Failure::Sdk(error)) => {
            assert_eq!(error.failure(), Some(InvocationFailure::OperationFailed));
            assert_eq!(error.result().unwrap().code(), "E1613");
        }
        other => panic!("publish failure: {other:?}"),
    }
    assert!(output.contains("\"pendingDeliveries\":1"), "{output}");
    // A failed explicit delivery prints no count.
    let (output, result) = command(&["--workdir", recovery, "--fault", "publish-failed", "--deliver-pending", "fulfillment"]);
    assert!(matches!(result, Err(Failure::Other(_))) && !output.contains("delivered"), "{output}");
    for expected in ["{\"delivered\":1}", "{\"delivered\":0}"] {
        let (output, result) = command(&["--workdir", recovery, "--deliver-pending", "fulfillment"]);
        assert!(result.is_ok() && output.contains(expected), "deliver pending: {result:?} {output}");
    }
    let inspect = root.0.join("triage-inspect");
    let (_, result) = command(&["--workdir", root.0.join("triage").to_str().unwrap(), "--inspect", inspect.to_str().unwrap(), "triage"]);
    assert!(result.is_ok(), "{result:?}");
    assert!(PathBuf::from(format!("{}.0.json", inspect.display())).exists(), "inspection not saved");
    assert!(matches!(command(&["unknown"]).1, Err(Failure::Other(_))));
    assert!(matches!(command(&["--fault", "nope", "fulfillment"]).1, Err(Failure::Other(_))));
}

#[test]
fn live_partial_completion_faults_recover_explicitly_across_restart() {
    let Some(root) = scratch() else { return };
    for (fault, code, deliveries) in [("after-commit", "E1612", 1), ("publish-failed", "E1613", 1), ("after-delivery", "E1612", 0)] {
        let workdir = root.0.join(fault);
        let workdir = workdir.to_str().unwrap();
        let (output, result) = command(&["--workdir", workdir, "--fault", fault, "fulfillment"]);
        let Err(Failure::Sdk(error)) = result else { panic!("{fault}: {result:?}") };
        assert_eq!(error.result().map(|result| result.code()), Some(code.to_owned()), "{fault}: {error}");
        assert!(output.contains("\"bookings\":1"), "{fault}: {output}");
        // A restart sees the committed booking; recovery is explicit.
        let (output, result) = command(&["--workdir", workdir, "--deliver-pending", "fulfillment"]);
        assert!(result.is_ok() && output.contains(&format!("{{\"delivered\":{deliveries}}}")), "{fault}: {result:?} {output}");
        let (output, result) = command(&["--workdir", workdir, "fulfillment"]);
        assert!(result.is_ok() && output.contains("DuplicateOrder") && output.contains("\"pendingDeliveries\":0"), "{fault}: {output}");
        let summary = output.trim().lines().last().unwrap();
        assert_eq!(decode(summary.as_bytes(), 4).unwrap().as_object().unwrap()["deliveries"], Json::Int(1), "{fault}");
    }
}

/// The example binary `cargo test` builds beside this test's binary.
fn example_binary() -> PathBuf {
    let deps = std::env::current_exe().unwrap().parent().unwrap().to_owned();
    deps.parent().unwrap().join("examples").join(format!("jacquard-examples{}", std::env::consts::EXE_SUFFIX))
}

#[test]
fn the_command_exits_2_for_usage_errors() {
    let binary = example_binary();
    assert!(binary.exists(), "{} was not built", binary.display());
    for arguments in [&["unknown"][..], &["--fault", "nope", "fulfillment"], &["--workdir"], &[]] {
        let output = std::process::Command::new(&binary).args(arguments).output().unwrap();
        assert_eq!(output.status.code(), Some(2), "{arguments:?}: {}", String::from_utf8_lossy(&output.stderr));
        assert!(!output.stderr.is_empty(), "{arguments:?}: no message");
    }
}

#[test]
fn live_the_command_exits_1_for_a_jacquard_failure() {
    let Some(root) = scratch() else { return };
    let output = std::process::Command::new(example_binary())
        .args(["--workdir", root.0.to_str().unwrap(), "--fault", "publish-failed", "fulfillment"])
        .output()
        .unwrap();
    assert_eq!(output.status.code(), Some(1), "{}", String::from_utf8_lossy(&output.stderr));
    assert!(String::from_utf8_lossy(&output.stderr).contains("E1613"));
    assert!(String::from_utf8_lossy(&output.stdout).contains("\"pendingDeliveries\":1"));
}
