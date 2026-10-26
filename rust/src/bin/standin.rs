//! A recorded or faulty worker for the crate's offline tests. It implements
//! no Core semantics and is not part of the adapter's interface.

use std::io::{Read, Write};
use std::process::{Command, Stdio, exit};
use std::thread::sleep;
use std::time::Duration;

use jacquard_host::json::{Json, Object, stringify};
use jacquard_host::kit::{Kit, load};

/// Names the stand-in behaviour.
const MODE_VAR: &str = "JACQUARD_RUST_STANDIN";
/// Names the transcript replayed in `replay` mode.
const CASE_VAR: &str = "JACQUARD_RUST_STANDIN_CASE";

fn main() {
    let mode = std::env::var(MODE_VAR).unwrap_or_default();
    match mode.as_str() {
        "exit" => exit(0),
        "silent" => block(),
        "truncated" => {
            let _ = std::io::stdout().write_all(&[0, 0, 0]);
            exit(0)
        }
        "escaped-descendant" => {
            // A descendant in its own process group holds stderr; killing the
            // worker's group misses it.
            let mut child = Command::new("sleep");
            child.arg("30").stdin(Stdio::null()).stdout(Stdio::null());
            std::os::unix::process::CommandExt::process_group(&mut child, 0);
            let pid = child.spawn().map(|child| child.id()).unwrap_or_else(|_| exit(3));
            eprint!("{pid}");
            exit(fault(&kit(), "ok"))
        }
        "stderr-descendant" | "stderr-descendant-nonzero" => {
            // Answer normally, but leave a descendant holding only stderr.
            let pid = Command::new("sleep").arg("30").stdin(Stdio::null()).stdout(Stdio::null()).spawn();
            eprint!("{}", pid.map(|child| child.id()).unwrap_or_else(|_| exit(3)));
            exit(fault(&kit(), if mode == "stderr-descendant" { "ok" } else { "nonzero" }))
        }
        "descendant" => {
            // Leave a descendant holding stdout, like a launcher that does not exec.
            let pid = Command::new("sleep").arg("30").stdin(Stdio::null()).stderr(Stdio::null()).spawn();
            eprint!("{}", pid.map(|child| child.id()).unwrap_or_else(|_| exit(3)));
            exit(0)
        }
        "replay" => exit(replay(&kit(), &std::env::var(CASE_VAR).unwrap_or_default())),
        "" => exit(2),
        _ => exit(fault(&kit(), &mode)),
    }
}

fn kit() -> Kit {
    load().unwrap_or_else(|_| exit(3))
}

fn send(frame: &Object) {
    let payload = stringify(&Json::Object(frame.clone()), 256).unwrap_or_else(|_| exit(3));
    let mut bytes = (payload.len() as u32).to_be_bytes().to_vec();
    bytes.extend_from_slice(payload.as_bytes());
    let mut stdout = std::io::stdout();
    let _ = stdout.write_all(&bytes).and_then(|()| stdout.flush());
}

fn discard(count: u64) -> bool {
    std::io::copy(&mut std::io::stdin().take(count), &mut std::io::sink()).is_ok_and(|copied| copied == count)
}

fn read_frame() -> bool {
    let mut prefix = [0u8; 4];
    std::io::stdin().read_exact(&mut prefix).is_ok() && discard(u64::from(u32::from_be_bytes(prefix)))
}

fn replay(kit: &Kit, name: &str) -> i32 {
    let Some(transcript) = kit.transcripts.iter().rev().find(|transcript| transcript.name == name) else {
        return 3;
    };
    let (mut index, mut raw) = (0, false);
    for step in &transcript.host_input {
        if step.direction == "core_to_host" {
            let frame = &transcript.core_output[index];
            index += 1;
            send(frame);
            if matches!(frame.get("kind").and_then(Json::as_str), Some("outcome" | "fatal" | "shutdown_ack")) {
                return transcript.exit_status as i32;
            }
        } else if let Some(hex) = &step.raw_hex {
            discard(hex.len() as u64 / 2);
            raw = true;
        } else if !step.after_terminal && !raw {
            read_frame();
        }
    }
    transcript.exit_status as i32
}

fn block() -> ! {
    loop {
        sleep(Duration::from_secs(3600));
    }
}

fn fault(kit: &Kit, mode: &str) -> i32 {
    let pure = &kit.transcripts[0];
    let mut hello = pure.core_output[0].clone();
    let mut outcome = pure.core_output[1].clone();
    if mode == "stderr" {
        let _ = std::io::stderr().write_all(&vec![b'x'; 2 * 1024 * 1024]);
    }
    if mode == "unknown-field" {
        hello.insert("extra".into(), Json::Int(1));
    }
    send(&hello);
    if mode == "no-read" {
        block();
    }
    let mut count = 1;
    while read_frame() {
        if count == 2 {
            if mode == "invalid-hash" {
                set_callable(&mut outcome, &"A".repeat(64));
            }
            send(&outcome);
            if mode == "extra" {
                send(&outcome);
            }
            if mode == "stall" {
                block();
            }
            close_stdout();
            if mode == "slow-exit" {
                sleep(Duration::from_millis(1500));
            }
            return if mode == "nonzero" { 74 } else { 0 };
        }
        count += 1;
    }
    0
}

fn set_callable(outcome: &mut Object, callable: &str) {
    if let Some(Json::Object(evidence)) = outcome.get_mut("evidence")
        && let Some(Json::Object(core)) = evidence.get_mut("core")
        && let Some(Json::Object(target)) = core.get_mut("target")
    {
        target.insert("callable".into(), callable.into());
    }
}

/// Closes file descriptor 1 so the host sees end of stream before exit.
fn close_stdout() {
    use std::os::fd::FromRawFd;
    let _ = std::io::stdout().flush();
    // SAFETY: descriptor 1 is open and nothing writes to stdout afterwards.
    drop(unsafe { std::fs::File::from_raw_fd(1) });
}
