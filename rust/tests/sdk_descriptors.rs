//! A Core command whose descendant leaves its process group and keeps the
//! output open fails preparation after the grace period, without leaking
//! reader threads or pipe descriptors. Its own binary, so no other test
//! changes the counts.

#![cfg(target_os = "linux")]

use std::os::unix::fs::PermissionsExt;
use std::time::{Duration, Instant};

use jacquard_host::sdk::{Error, PrepareOptions, Runtime, RuntimeOptions};

fn count(directory: &str) -> usize {
    std::fs::read_dir(directory).unwrap().count()
}

#[test]
fn an_escaped_descendant_holding_core_output_leaks_nothing() {
    let root = std::env::temp_dir().join(format!("jacquard-host-rust-escaped-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&root);
    std::fs::create_dir_all(root.join("prelude")).unwrap();
    std::fs::write(root.join("prelude/01-prim.jqd"), "prelude\n").unwrap();
    std::fs::write(root.join("rules.jac"), "rules\n").unwrap();
    let pids = root.join("pids");
    // setsid takes the sleeper out of the command's process group, so the
    // group kill cannot reach it; it inherits stdout and stderr.
    let script = format!(
        "#!/bin/sh\ncase \"$1\" in\n--version) echo 'jacquard {}'; setsid sleep 30 & echo $! >> '{}' ;;\nesac\n",
        jacquard_host::compat::CORE_VERSION,
        pids.display()
    );
    let binary = root.join("jacquard");
    std::fs::write(&binary, script).unwrap();
    std::fs::set_permissions(&binary, std::fs::Permissions::from_mode(0o755)).unwrap();
    let options = PrepareOptions { cache_dir: Some(root.join("cache")) };
    let attempt = || {
        let runtime = Runtime::new(RuntimeOptions {
            binary: Some(binary.to_string_lossy().into()),
            prelude: Some(root.join("prelude")),
            ..Default::default()
        })
        .unwrap();
        let started = Instant::now();
        let error = runtime.prepare(&[root.join("rules.jac")], &options).expect_err("output stays open");
        (error, started.elapsed())
    };
    // Warm up thread-local and allocator state before counting.
    let _ = attempt();
    let (threads, descriptors) = (count("/proc/self/task"), count("/proc/self/fd"));
    for _ in 0..3 {
        let (error, elapsed) = attempt();
        assert!(matches!(&error, Error::Preparation(message) if message.contains("its output did not close")), "{error}");
        assert!(elapsed < Duration::from_millis(1800), "the grace period took {elapsed:?}");
    }
    let after = (count("/proc/self/task"), count("/proc/self/fd"));
    for pid in std::fs::read_to_string(&pids).unwrap_or_default().split_whitespace() {
        let _ = std::process::Command::new("kill").arg("-9").arg(pid).status();
    }
    let _ = std::fs::remove_dir_all(&root);
    assert_eq!(after, (threads, descriptors), "threads and descriptors grew");
}
