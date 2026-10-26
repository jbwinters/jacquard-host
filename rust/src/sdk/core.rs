//! Bounded runs of Core's public commands, and digests of prepared files.

use std::collections::BTreeMap;
use std::ffi::OsStr;
use std::io::Read;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread;
use std::time::{Duration, Instant};

use crate::carrier::{Pipe, exited_unreaped, isolate, kill_group, kill_tree};
use crate::json::{Json, stringify};
use crate::sha256::hex_digest;

use super::Error;

/// Each Core command must finish within this time.
const COMMAND_TIMEOUT: Duration = Duration::from_secs(30);
const STDOUT_LIMIT: usize = 8 << 20;
const STDERR_LIMIT: usize = 4096;

/// Reads up to `limit` bytes, then drains and discards the rest.
fn bounded_read(mut source: impl Read, limit: usize) -> (Vec<u8>, bool) {
    let mut kept = Vec::new();
    let mut overflow = false;
    let mut buffer = [0u8; 8192];
    loop {
        match source.read(&mut buffer) {
            Ok(0) => return (kept, overflow),
            Ok(n) => {
                let room = limit - kept.len();
                if n > room {
                    overflow = true;
                }
                kept.extend_from_slice(&buffer[..n.min(room)]);
            }
            Err(error) if error.kind() == std::io::ErrorKind::Interrupted => {}
            Err(_) => return (kept, overflow),
        }
    }
}

/// Runs one Core subcommand with the prelude set, returning its stdout. The
/// command runs in its own process group, which is killed when it times out,
/// and on Linux also when it exits. Output is read through stoppable pipes:
/// once the command has exited and its output stays open for the grace
/// period (a descendant that left the group still holds it), the readers stop
/// and the pipes close.
pub(crate) fn run(binary: &Path, prelude: &Path, arguments: &[&OsStr]) -> Result<String, Error> {
    let name = arguments.first().map_or_else(String::new, |argument| argument.to_string_lossy().into_owned());
    let failed = |detail: String| Error::preparation(format!("Core {name} failed: {detail}"));
    let mut command = Command::new(binary);
    command.args(arguments).env("JACQUARD_PRELUDE", prelude).stdin(Stdio::null()).stdout(Stdio::piped()).stderr(Stdio::piped());
    isolate(&mut command);
    let mut running = Running { child: command.spawn().map_err(|error| failed(error.to_string()))?, reaped: false };
    let stop = Arc::new(AtomicBool::new(false));
    // Declared after `running`, so on any early return the readers stop
    // first and the command's group is then killed and reaped.
    let mut readers = Readers { stop: stop.clone(), stdout: None, stderr: None };
    let stdout = Pipe::new(running.child.stdout.take().expect("piped stdout"), &stop, &stop);
    readers.stdout = Some(spawn_reader(stdout, STDOUT_LIMIT).map_err(|error| failed(format!("cannot start a reader: {error}")))?);
    let stderr = Pipe::new(running.child.stderr.take().expect("piped stderr"), &stop, &stop);
    readers.stderr = Some(spawn_reader(stderr, STDERR_LIMIT).map_err(|error| failed(format!("cannot start a reader: {error}")))?);
    let deadline = Instant::now() + COMMAND_TIMEOUT;
    let status = loop {
        let child = &mut running.child;
        match exited_unreaped(child.id()) {
            Some(true) => {
                // The leader is unreaped, so its group id is still ours.
                kill_group(child.id());
                running.reaped = true;
                break child.wait();
            }
            Some(false) => {}
            None => match child.try_wait() {
                Ok(Some(status)) => {
                    running.reaped = true;
                    break Ok(status);
                }
                Ok(None) => {}
                Err(error) => {
                    // The state is unknown; never signal a group that may
                    // no longer be ours.
                    running.reaped = true;
                    break Err(error);
                }
            },
        }
        if Instant::now() >= deadline {
            drop(readers);
            drop(running);
            return Err(failed(format!("no exit within {} seconds", COMMAND_TIMEOUT.as_secs())));
        }
        thread::sleep(Duration::from_millis(10));
    };
    let status = status.map_err(|error| failed(error.to_string()))?;
    let Some(((stdout, overflow), (stderr, _))) = readers.finish(Instant::now() + OUTPUT_GRACE) else {
        return Err(failed("its output did not close".into()));
    };
    if !status.success() {
        let detail = String::from_utf8_lossy(&stderr).trim().to_owned();
        return Err(failed(if detail.is_empty() { status.to_string() } else { detail }));
    }
    if overflow {
        return Err(Error::preparation(format!("Core {name} output exceeds 8 MiB")));
    }
    String::from_utf8(stdout).map_err(|_| Error::preparation(format!("Core {name} output is not UTF-8")))
}

/// How long output may stay open after the command exits.
const OUTPUT_GRACE: Duration = Duration::from_secs(1);

type Output = (Vec<u8>, bool);

/// The running command; dropping it before it is reaped kills its process
/// group and reaps it.
struct Running {
    child: std::process::Child,
    reaped: bool,
}

impl Drop for Running {
    fn drop(&mut self) {
        if !self.reaped {
            kill_tree(&mut self.child);
            let _ = self.child.wait();
        }
    }
}

#[cfg(test)]
thread_local! {
    /// Test-only: the reader start (0 for stdout, 1 for stderr) that fails.
    static FAIL_READER: std::cell::Cell<Option<usize>> = const { std::cell::Cell::new(None) };
    static READERS_STARTED: std::cell::Cell<usize> = const { std::cell::Cell::new(0) };
}

fn spawn_reader<R: Send + 'static>(source: Pipe<R>, limit: usize) -> std::io::Result<thread::JoinHandle<Output>>
where
    Pipe<R>: Read,
{
    #[cfg(test)]
    {
        let index = READERS_STARTED.with(|started| started.replace(started.get() + 1));
        if FAIL_READER.with(std::cell::Cell::get) == Some(index) {
            return Err(std::io::Error::new(std::io::ErrorKind::WouldBlock, "injected"));
        }
    }
    thread::Builder::new().name("jacquard-core-output".into()).spawn(move || bounded_read(source, limit))
}

/// The two output readers. Dropping them stops and joins them on Unix,
/// where their reads are interruptible; elsewhere a reader blocked on a
/// pipe a descendant holds is left to finish on its own.
struct Readers {
    stop: Arc<AtomicBool>,
    stdout: Option<thread::JoinHandle<Output>>,
    stderr: Option<thread::JoinHandle<Output>>,
}

impl Readers {
    /// Both outputs, if both close before `deadline`.
    fn finish(mut self, deadline: Instant) -> Option<(Output, Output)> {
        let done = |reader: &Option<thread::JoinHandle<Output>>| reader.as_ref().is_none_or(thread::JoinHandle::is_finished);
        while !(done(&self.stdout) && done(&self.stderr)) {
            if Instant::now() >= deadline {
                return None;
            }
            thread::sleep(Duration::from_millis(10));
        }
        let join = |reader: Option<thread::JoinHandle<Output>>| reader.and_then(|reader| reader.join().ok());
        Some((join(self.stdout.take())?, join(self.stderr.take())?))
    }
}

impl Drop for Readers {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::SeqCst);
        for reader in [self.stdout.take(), self.stderr.take()].into_iter().flatten() {
            if cfg!(unix) || reader.is_finished() {
                let _ = reader.join();
            }
        }
    }
}

/// The SHA-256 digest of a file's bytes.
pub(crate) fn file_digest(path: &Path) -> std::io::Result<String> {
    Ok(hex_digest(&std::fs::read(path)?))
}

/// Digests every regular file under `root`, keyed by `/`-separated relative
/// path, refusing other entry kinds.
pub(crate) fn tree(root: &Path) -> Result<BTreeMap<String, String>, Error> {
    fn walk(root: &Path, directory: &Path, files: &mut BTreeMap<String, String>) -> Result<(), Error> {
        let io = |error: std::io::Error| Error::preparation(format!("cannot read {}: {error}", directory.display()));
        let mut entries = std::fs::read_dir(directory).map_err(io)?.collect::<Result<Vec<_>, _>>().map_err(io)?;
        entries.sort_by_key(|entry| entry.file_name());
        for entry in entries {
            let path = entry.path();
            let kind = entry.file_type().map_err(io)?;
            let relative = path.strip_prefix(root).unwrap_or(&path);
            let relative = relative.iter().map(|part| part.to_string_lossy()).collect::<Vec<_>>().join("/");
            if kind.is_dir() {
                walk(root, &path, files)?;
            } else if kind.is_file() {
                files.insert(relative, file_digest(&path).map_err(io)?);
            } else {
                return Err(Error::preparation(format!("unsupported filesystem entry in prepared artifacts: {relative}")));
            }
        }
        Ok(())
    }
    let mut files = BTreeMap::new();
    walk(root, root, &mut files)?;
    Ok(files)
}

/// Canonical JSON for a string map: keys in code-point order.
pub(crate) fn canonical(map: &BTreeMap<String, String>) -> String {
    let object = map.iter().map(|(key, value)| (key.clone(), Json::Str(value.clone()))).collect();
    stringify(&Json::Object(object), 4).expect("a flat object")
}

/// The digest of a string map's canonical JSON.
pub(crate) fn map_digest(map: &BTreeMap<String, String>) -> String {
    hex_digest(canonical(map).as_bytes())
}

/// The digest of bytes.
pub(crate) fn digest(data: &[u8]) -> String {
    hex_digest(data)
}

/// Finds an executable the way a shell would, then resolves symbolic links.
pub(crate) fn executable(binary: &str) -> Result<PathBuf, Error> {
    let missing = || Error::preparation(format!("cannot find Core executable {binary}; configure JACQUARD_BIN using the setup guide"));
    let candidate = if binary.contains(std::path::MAIN_SEPARATOR) || binary.contains('/') {
        Some(PathBuf::from(binary))
    } else {
        // Like Go's exec.LookPath, relative entries (including empty ones,
        // which mean the current directory) are not searched.
        std::env::var_os("PATH").and_then(|paths| {
            std::env::split_paths(&paths)
                .filter(|directory| directory.is_absolute())
                .map(|directory| directory.join(binary))
                .find(|path| is_executable(path))
        })
    };
    let candidate = candidate.filter(|path| is_executable(path)).ok_or_else(missing)?;
    candidate.canonicalize().map_err(|error| Error::preparation(format!("cannot resolve Core executable {binary}: {error}")))
}

#[cfg(unix)]
fn is_executable(path: &Path) -> bool {
    use std::os::unix::fs::PermissionsExt;
    path.metadata().is_ok_and(|metadata| metadata.is_file() && metadata.permissions().mode() & 0o111 != 0)
}

#[cfg(not(unix))]
fn is_executable(path: &Path) -> bool {
    path.is_file()
}

/// Standard base64 with padding.
pub(crate) fn base64(data: &[u8]) -> String {
    const ALPHABET: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::with_capacity(data.len().div_ceil(3) * 4);
    for chunk in data.chunks(3) {
        let bytes = [chunk[0], chunk.get(1).copied().unwrap_or(0), chunk.get(2).copied().unwrap_or(0)];
        let n = u32::from(bytes[0]) << 16 | u32::from(bytes[1]) << 8 | u32::from(bytes[2]);
        for (index, shift) in [18, 12, 6, 0].into_iter().enumerate() {
            out.push(if index <= chunk.len() { char::from(ALPHABET[(n >> shift) as usize & 63]) } else { '=' });
        }
    }
    out
}

#[cfg(all(test, unix))]
mod unix_tests {
    use super::{FAIL_READER, READERS_STARTED, run};
    use std::ffi::OsStr;
    use std::path::Path;
    use std::time::{Duration, Instant};

    #[test]
    fn a_reader_that_cannot_start_stops_the_other_and_the_command() {
        for position in [0, 1] {
            let marker = std::env::temp_dir().join(format!("jacquard-host-rust-reader-{}-{position}", std::process::id()));
            let _ = std::fs::remove_file(&marker);
            READERS_STARTED.with(|started| started.set(0));
            FAIL_READER.with(|fail| fail.set(Some(position)));
            let script = format!("sleep 0.3; touch '{}'", marker.display());
            let started = Instant::now();
            let error = run(Path::new("/bin/sh"), Path::new("/"), &[OsStr::new("-c"), OsStr::new(&script)]).unwrap_err();
            FAIL_READER.with(|fail| fail.set(None));
            assert!(error.to_string().contains("cannot start a reader: injected"), "{error}");
            assert!(started.elapsed() < Duration::from_millis(250), "cleanup waited for the command");
            std::thread::sleep(Duration::from_millis(600));
            assert!(!marker.exists(), "the command kept running after reader {position} failed to start");
        }
    }
}

#[cfg(test)]
mod tests {
    use super::base64;

    #[test]
    fn base64_matches_rfc_4648_vectors() {
        for (input, expected) in
            [("", ""), ("f", "Zg=="), ("fo", "Zm8="), ("foo", "Zm9v"), ("foob", "Zm9vYg=="), ("fooba", "Zm9vYmE="), ("foobar", "Zm9vYmFy")]
        {
            assert_eq!(base64(input.as_bytes()), expected);
        }
    }
}
