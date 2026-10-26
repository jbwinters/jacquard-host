//! One bounded `stdio-u32-json-v0` worker lifetime.
//!
//! Background threads only read stdout frames on request, drain stderr, and
//! perform writes. Each waits on its pipe with `poll` and a short timeout, so
//! disposal can stop and join every thread even while a descendant still
//! holds a pipe. Every wait on the worker happens on the caller's thread with
//! a timeout, so the lifetime deadline or a cancellation kills the worker's
//! process group from that thread, which is also the only thread that reaps
//! it: a group is never signalled after its leader has been reaped.

use std::io::{Read, Write};
use std::process::{Child, Command, ExitStatus, Stdio};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::mpsc::{self, Receiver, RecvTimeoutError, Sender, SyncSender};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use crate::error::ProtocolError;
use crate::json::{Object, decode_object, frame_bytes};
use crate::protocol::{HARD_LIMITS, Limits};

/// The worker lifetime when `Options::deadline` is `None`.
pub const DEFAULT_DEADLINE: Duration = Duration::from_secs(30);
/// How long stderr may stay open after the worker exits before the remaining
/// process group is treated as holding its pipes.
const EXIT_GRACE: Duration = Duration::from_secs(1);
/// The stderr wait once the invocation is cancelled or expired.
const CANCELLED_GRACE: Duration = Duration::from_millis(100);
/// How often a wait rechecks the deadline and the cancellation token.
const POLL: Duration = Duration::from_millis(10);

/// A cooperative cancellation token shared with the caller and handlers.
#[derive(Clone, Debug, Default)]
pub struct Cancel(Arc<AtomicBool>);

impl Cancel {
    /// A token that is not cancelled.
    pub fn new() -> Cancel {
        Cancel::default()
    }
    /// Requests cancellation.
    pub fn cancel(&self) {
        self.0.store(true, Ordering::SeqCst);
    }
    /// Whether cancellation was requested.
    pub fn is_cancelled(&self) -> bool {
        self.0.load(Ordering::SeqCst)
    }
}

/// Options for one worker lifetime.
#[derive(Clone, Debug, Default)]
pub struct Options {
    /// The Core executable and leading arguments; `host worker --store DIR` is
    /// appended. Defaults to `$JACQUARD_BIN`, then `jacquard`.
    pub command: Vec<String>,
    /// Replaces the inherited environment when set.
    pub env: Option<Vec<(String, String)>>,
    /// Bounds the whole lifetime: spawn, pipes, handlers, and exit.
    pub deadline: Option<Duration>,
}

impl Options {
    /// Refuses an empty command element or a zero deadline.
    pub fn validate(&self) -> Result<(), String> {
        if self.command.iter().any(String::is_empty) {
            return Err("command must be a nonempty executable argument list".into());
        }
        if self.deadline == Some(Duration::ZERO) {
            return Err("deadline must be positive".into());
        }
        Ok(())
    }

    fn command(&self) -> Vec<String> {
        if !self.command.is_empty() {
            return self.command.clone();
        }
        vec![std::env::var("JACQUARD_BIN").ok().filter(|bin| !bin.is_empty()).unwrap_or_else(|| "jacquard".into())]
    }
}

/// A carrier failure.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum CarrierError {
    /// The worker could not start, closed a pipe, exited unsuccessfully, was
    /// aborted, or outlived its deadline.
    Loss { message: String, timeout: bool, aborted: bool },
    /// A frame violated the protocol.
    Protocol(ProtocolError),
}

impl CarrierError {
    fn loss(message: impl Into<String>) -> CarrierError {
        CarrierError::Loss { message: message.into(), timeout: false, aborted: false }
    }
}

impl std::fmt::Display for CarrierError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            CarrierError::Loss { message, .. } => f.write_str(message),
            CarrierError::Protocol(error) => error.fmt(f),
        }
    }
}

impl From<ProtocolError> for CarrierError {
    fn from(error: ProtocolError) -> Self {
        CarrierError::Protocol(error)
    }
}

type FrameResult = Result<Option<Object>, CarrierError>;

struct Stderr {
    data: Mutex<Vec<u8>>,
    limit: AtomicUsize,
}

/// One worker process.
pub struct Worker {
    child: Option<Child>,
    stdin: Option<SyncSender<Vec<u8>>>,
    writes: Option<Receiver<std::io::Result<()>>>,
    reads: Option<Sender<Limits>>,
    frames: Option<Receiver<FrameResult>>,
    reading: bool,
    stderr_done: Option<Receiver<()>>,
    stderr: Arc<Stderr>,
    /// Set at disposal; every pipe thread then stops and closes its pipe.
    stop: Arc<AtomicBool>,
    /// Set when input closes or the worker expires; stops a write in progress.
    input_stop: Arc<AtomicBool>,
    /// Set when the worker expires; stops a read in progress.
    output_stop: Arc<AtomicBool>,
    threads: Vec<JoinHandle<()>>,
    expires: Instant,
    cancel: Cancel,
    expired: Arc<AtomicBool>,
    fault: Option<CarrierError>,
    reaped: bool,
    /// The spawn error, when the worker never started.
    pub spawn_error: Option<String>,
    /// The exit status once reaped, unless a signal ended the worker.
    pub exit_status: Option<i32>,
    /// The signal that ended the worker, by name.
    pub signal: Option<String>,
}

impl Worker {
    /// Spawns the worker. A spawn failure is reported by the first operation.
    pub fn start(store: &str, options: &Options, cancel: Cancel) -> Result<Worker, String> {
        options.validate()?;
        let command = options.command();
        let mut process = Command::new(&command[0]);
        process.args(&command[1..]).args(["host", "worker", "--store", store]);
        if let Some(env) = &options.env {
            process.env_clear().envs(env.iter().cloned());
        }
        process.stdin(Stdio::piped()).stdout(Stdio::piped()).stderr(Stdio::piped());
        isolate(&mut process);
        let stderr = Arc::new(Stderr { data: Mutex::new(Vec::new()), limit: AtomicUsize::new(HARD_LIMITS.max_stderr_bytes) });
        let mut worker = Worker {
            child: None,
            stdin: None,
            writes: None,
            reads: None,
            frames: None,
            reading: false,
            stderr_done: None,
            stderr: Arc::clone(&stderr),
            stop: Arc::new(AtomicBool::new(false)),
            input_stop: Arc::new(AtomicBool::new(false)),
            output_stop: Arc::new(AtomicBool::new(false)),
            threads: Vec::new(),
            expires: Instant::now() + options.deadline.unwrap_or(DEFAULT_DEADLINE),
            cancel,
            expired: Arc::new(AtomicBool::new(false)),
            fault: None,
            reaped: false,
            spawn_error: None,
            exit_status: None,
            signal: None,
        };
        let mut child = match process.spawn() {
            Ok(child) => child,
            Err(error) => {
                worker.spawn_error = Some(error.to_string());
                worker.fault =
                    Some(CarrierError::loss(format!("could not start {} ({error}); check the configured Core installation", command[0])));
                worker.reaped = true;
                return Ok(worker);
            }
        };
        let mut stdout = Pipe::new(child.stdout.take().expect("piped stdout"), &worker.stop, &worker.output_stop);
        let mut err = Pipe::new(child.stderr.take().expect("piped stderr"), &worker.stop, &worker.stop);
        let mut stdin = Pipe::new(child.stdin.take().expect("piped stdin"), &worker.stop, &worker.input_stop);
        // Stored first, so a failure below still kills and reaps the worker.
        worker.child = Some(child);
        // stdout: one frame per request, read under the requested limits.
        let (read_tx, read_rx) = mpsc::channel::<Limits>();
        let (frame_tx, frame_rx) = mpsc::channel::<FrameResult>();
        worker.spawn("stdout", move || {
            for limits in read_rx {
                let frame = read_frame(&mut stdout, &limits);
                let last = !matches!(frame, Ok(Some(_)));
                if frame_tx.send(frame).is_err() || last {
                    return;
                }
            }
        });
        worker.reads = Some(read_tx);
        worker.frames = Some(frame_rx);
        // stderr: always drained; a bounded prefix is kept.
        let (done_tx, done_rx) = mpsc::channel::<()>();
        worker.spawn("stderr", move || {
            let mut chunk = [0u8; 8192];
            while let Ok(n) = err.read(&mut chunk) {
                if n == 0 {
                    break;
                }
                let mut data = stderr.data.lock().expect("stderr lock");
                let room = stderr.limit.load(Ordering::SeqCst).saturating_sub(data.len());
                data.extend_from_slice(&chunk[..n.min(room)]);
            }
            let _ = done_tx.send(());
        });
        worker.stderr_done = Some(done_rx);
        // stdin: one writer thread, so a blocked write cannot hold the caller.
        let (write_tx, write_rx) = mpsc::sync_channel::<Vec<u8>>(0);
        let (result_tx, result_rx) = mpsc::channel::<std::io::Result<()>>();
        worker.spawn("stdin", move || {
            for bytes in write_rx {
                let result = stdin.write_all(&bytes).and_then(|()| stdin.flush());
                let failed = result.is_err();
                if result_tx.send(result).is_err() || failed {
                    return;
                }
            }
        });
        worker.stdin = Some(write_tx);
        worker.writes = Some(result_rx);
        Ok(worker)
    }

    /// Starts one pipe thread; a failure becomes the worker's fault.
    fn spawn(&mut self, name: &str, body: impl FnOnce() + Send + 'static) {
        if self.fault.is_some() {
            return;
        }
        match thread::Builder::new().name(format!("jacquard-worker-{name}")).spawn(body) {
            Ok(handle) => self.threads.push(handle),
            Err(error) => self.fault = Some(CarrierError::loss(format!("could not start the worker's {name} thread ({error})"))),
        }
    }

    /// Whether the worker's lifetime ended by expiry or cancellation.
    pub fn is_expired(&self) -> bool {
        self.expired.load(Ordering::SeqCst)
    }

    /// A flag handlers can observe; set when the worker expires or is cancelled.
    pub fn expired_flag(&self) -> Arc<AtomicBool> {
        Arc::clone(&self.expired)
    }

    /// The carrier failure that ended the worker, if any.
    pub fn fault(&mut self) -> Option<CarrierError> {
        if self.fault.is_none() {
            if self.cancel.is_cancelled() {
                self.expire();
                self.fault = Some(CarrierError::Loss {
                    message: "invocation aborted; outside completion is unknown".into(),
                    timeout: false,
                    aborted: true,
                });
            } else if Instant::now() >= self.expires {
                self.expire();
                self.fault =
                    Some(CarrierError::Loss { message: "worker lifetime deadline exceeded".into(), timeout: true, aborted: false });
            }
        }
        self.fault.clone()
    }

    /// Kills the worker's process group (its leader is unreaped) and stops
    /// pipe traffic, including a read or write in progress.
    fn expire(&mut self) {
        self.expired.store(true, Ordering::SeqCst);
        if !self.reaped
            && let Some(child) = self.child.as_mut()
        {
            kill_tree(child);
        }
        self.output_stop.store(true, Ordering::SeqCst);
        self.close_input();
    }

    /// Waits on `receive` until it yields, the deadline passes, or the
    /// invocation is cancelled.
    pub(crate) fn wait_for<T>(&mut self, receiver: &Receiver<T>) -> Result<Option<T>, CarrierError> {
        loop {
            if let Some(fault) = self.fault() {
                return Err(fault);
            }
            let slice = self.expires.saturating_duration_since(Instant::now()).min(POLL);
            match receiver.recv_timeout(slice) {
                Ok(value) => return Ok(Some(value)),
                Err(RecvTimeoutError::Timeout) => continue,
                Err(RecvTimeoutError::Disconnected) => return Ok(None),
            }
        }
    }

    /// Selects how much stderr is retained.
    pub fn set_stderr_limit(&mut self, limit: usize) {
        let mut data = self.stderr.data.lock().expect("stderr lock");
        self.stderr.limit.store(limit, Ordering::SeqCst);
        data.truncate(limit);
    }

    /// The retained stderr prefix.
    pub fn stderr(&self) -> Vec<u8> {
        self.stderr.data.lock().expect("stderr lock").clone()
    }

    /// Reads one frame under `limits`. Returns `None` at a clean end of stream.
    pub fn read_frame(&mut self, limits: &Limits) -> Result<Option<Object>, CarrierError> {
        if let Some(fault) = self.fault() {
            return Err(fault);
        }
        if !self.reading {
            match &self.reads {
                Some(reads) if reads.send(*limits).is_ok() => self.reading = true,
                _ => return Ok(None),
            }
        }
        let Some(frames) = self.frames.take() else {
            return Ok(None);
        };
        let received = self.wait_for(&frames);
        self.frames = Some(frames);
        match received? {
            Some(frame) => {
                self.reading = false;
                frame
            }
            None => Ok(None),
        }
    }

    /// Writes raw carrier bytes.
    pub fn write_bytes(&mut self, bytes: Vec<u8>) -> Result<(), CarrierError> {
        if let Some(fault) = self.fault() {
            return Err(fault);
        }
        let Some(stdin) = self.stdin.clone() else {
            return Err(CarrierError::loss("worker write failed (worker input is closed)"));
        };
        if stdin.send(bytes).is_err() {
            return Err(self.fault().unwrap_or_else(|| CarrierError::loss("worker write failed (worker input is closed)")));
        }
        let Some(writes) = self.writes.take() else {
            return Err(CarrierError::loss("worker write failed (worker input is closed)"));
        };
        let result = self.wait_for(&writes);
        self.writes = Some(writes);
        match result? {
            Some(Ok(())) => self.fault().map_or(Ok(()), Err),
            Some(Err(error)) => Err(self.fault().unwrap_or_else(|| CarrierError::loss(format!("worker write failed ({error})")))),
            None => Err(self.fault().unwrap_or_else(|| CarrierError::loss("worker write failed (worker input is closed)"))),
        }
    }

    /// Encodes and writes one frame under the given limits.
    pub fn write_frame(&mut self, frame: &Object, limits: &Limits) -> Result<(), CarrierError> {
        let bytes = frame_bytes(frame, limits.max_frame_bytes, limits.max_json_depth)?;
        self.write_bytes(bytes)
    }

    /// Closes the worker's stdin, stopping any write in progress.
    pub fn close_input(&mut self) {
        self.input_stop.store(true, Ordering::SeqCst);
        self.stdin = None;
    }

    /// Closes stdin and waits for the worker to exit, bounded by the deadline,
    /// then reaps it. Call only after stdout reached end of stream.
    pub fn wait(&mut self) -> Result<(), CarrierError> {
        self.close_input();
        self.reap(false);
        self.fault().map_or(Ok(()), Err)
    }

    fn reap(&mut self, killing: bool) {
        if self.reaped {
            return;
        }
        let Some(pid) = self.child.as_ref().map(Child::id) else {
            self.reaped = true;
            return;
        };
        // Wait for the exit without reaping where possible: the unreaped
        // leader keeps its process group id reserved through the grace.
        let mut held = false;
        let exited = loop {
            match exited_unreaped(pid) {
                Some(true) => break true,
                None => break false,
                Some(false) => {
                    // At expiry or cancellation, fault() kills the group, so
                    // this loop is bounded by the lifetime deadline.
                    if !killing {
                        let _ = self.fault();
                    }
                    thread::sleep(POLL);
                }
            }
        };
        let status = if exited {
            held = !self.stderr_drained(EXIT_GRACE, killing);
            if held {
                kill_group(pid);
            }
            // The worker has exited, so this only reaps it.
            self.child.as_mut().and_then(|child| child.wait().ok())
        } else {
            self.wait_reaping(killing)
        };
        self.reaped = true;
        if let Some(status) = status {
            self.record(status);
        }
        if !exited {
            held = !self.stderr_drained(EXIT_GRACE, killing);
        }
        if held && !self.stderr_drained(EXIT_GRACE, killing) {
            // A descendant outside the group still holds stderr; stop waiting.
            // Disposal stops the stderr thread and closes the pipe.
            self.stderr_done = None;
        }
        if held && self.exit_status == Some(0) && self.fault.is_none() && !self.is_expired() {
            self.fault = Some(CarrierError::loss("worker descendants kept its pipes open after exit"));
        }
    }

    /// Where the exit cannot be observed without reaping, polls `try_wait`,
    /// so expiry and cancellation can still kill the unreaped worker.
    fn wait_reaping(&mut self, killing: bool) -> Option<ExitStatus> {
        loop {
            match self.child.as_mut()?.try_wait() {
                Ok(Some(status)) => return Some(status),
                Ok(None) => {
                    if !killing {
                        let _ = self.fault();
                    }
                    thread::sleep(POLL);
                }
                Err(_) => return None,
            }
        }
    }

    fn record(&mut self, status: ExitStatus) {
        #[cfg(unix)]
        {
            use std::os::unix::process::ExitStatusExt;
            if let Some(signal) = status.signal() {
                self.signal = Some(signal_name(signal));
                return;
            }
        }
        self.exit_status = status.code();
    }

    /// Whether stderr reached end of stream within `limit`. The wait also ends
    /// at the lifetime deadline, expiry, or cancellation (expiry is enforced
    /// unless `killing`), after which it waits only briefly: the process group
    /// has already been signalled.
    fn stderr_drained(&mut self, limit: Duration, killing: bool) -> bool {
        let Some(done) = self.stderr_done.take() else {
            return true;
        };
        let start = Instant::now();
        let mut ended: Option<Instant> = None;
        let drained = loop {
            // fault() first, so expiry during the grace is recorded as the fault.
            let faulted = !killing && self.fault().is_some();
            let late = self.is_expired() || self.cancel.is_cancelled() || Instant::now() >= self.expires;
            if ended.is_none() && (faulted || late) {
                ended = Some(Instant::now());
            }
            let left = match ended {
                Some(at) => CANCELLED_GRACE.saturating_sub(at.elapsed()),
                None => limit.saturating_sub(start.elapsed()),
            };
            if left.is_zero() {
                break false;
            }
            match done.recv_timeout(left.min(POLL)) {
                Ok(()) | Err(RecvTimeoutError::Disconnected) => break true,
                Err(RecvTimeoutError::Timeout) => {}
            }
        };
        if !drained {
            self.stderr_done = Some(done);
        }
        drained
    }

    /// Kills a live worker, reaps it, stops its pipe threads, and closes its
    /// pipes. Each thread notices the stop within one poll interval.
    pub fn dispose(&mut self) {
        self.close_input();
        if !self.reaped {
            if let Some(child) = self.child.as_mut() {
                kill_tree(child);
            }
            self.reap(true);
        }
        self.stderr_drained(EXIT_GRACE, true);
        self.stop.store(true, Ordering::SeqCst);
        self.reads = None;
        self.frames = None;
        self.writes = None;
        self.stderr_done = None;
        for handle in self.threads.drain(..) {
            let _ = handle.join();
        }
    }
}

impl Drop for Worker {
    fn drop(&mut self) {
        self.dispose();
    }
}

fn read_frame(stdout: &mut impl Read, limits: &Limits) -> FrameResult {
    let mut prefix = [0u8; 4];
    let mut filled = 0;
    while filled < 4 {
        match stdout.read(&mut prefix[filled..]) {
            Ok(0) if filled == 0 => return Ok(None),
            Ok(0) => return Err(CarrierError::loss("worker closed stdout during a frame")),
            Ok(n) => filled += n,
            Err(error) if error.kind() == std::io::ErrorKind::Interrupted => {}
            Err(_) => return Err(CarrierError::loss("worker closed stdout during a frame")),
        }
    }
    let length = u32::from_be_bytes(prefix) as usize;
    if length == 0 || length > limits.max_frame_bytes {
        return Err(ProtocolError::exceeded("frame length exceeds max_frame_bytes").into());
    }
    let mut payload = vec![0u8; length];
    stdout.read_exact(&mut payload).map_err(|_| CarrierError::loss("worker closed stdout during a frame"))?;
    Ok(Some(decode_object(&payload, limits.max_json_depth)?))
}

/// One end of a worker pipe. On Unix every read or write first waits for
/// readiness with `poll` in short slices and gives up once the worker is
/// disposed or this pipe is stopped, so a descendant holding the other end
/// cannot pin the thread or keep receiving data.
struct Pipe<T> {
    inner: T,
    #[cfg_attr(not(unix), allow(dead_code))]
    stops: [Arc<AtomicBool>; 2],
}

impl<T> Pipe<T> {
    fn new(inner: T, all: &Arc<AtomicBool>, own: &Arc<AtomicBool>) -> Pipe<T> {
        Pipe { inner, stops: [Arc::clone(all), Arc::clone(own)] }
    }
}

#[cfg(unix)]
impl<T: std::os::fd::AsRawFd> Pipe<T> {
    fn ready(&self, events: i16) -> std::io::Result<()> {
        let mut entry = sys::PollFd { fd: self.inner.as_raw_fd(), events, revents: 0 };
        loop {
            if self.stops.iter().any(|stop| stop.load(Ordering::SeqCst)) {
                return Err(std::io::Error::new(std::io::ErrorKind::BrokenPipe, "worker pipe closed by the host"));
            }
            // SAFETY: one valid pollfd entry for a descriptor this pipe owns.
            match unsafe { sys::poll(&mut entry, 1, 20) } {
                n if n > 0 => return Ok(()),
                0 => {}
                _ if std::io::Error::last_os_error().kind() == std::io::ErrorKind::Interrupted => {}
                _ => return Err(std::io::Error::last_os_error()),
            }
        }
    }
}

#[cfg(unix)]
impl<T: Read + std::os::fd::AsRawFd> Read for Pipe<T> {
    fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
        // A readable pipe (data or end of stream) never blocks the read.
        self.ready(sys::POLLIN)?;
        self.inner.read(buf)
    }
}

#[cfg(unix)]
impl<T: Write + std::os::fd::AsRawFd> Write for Pipe<T> {
    fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
        // A writable pipe accepts at least PIPE_BUF bytes without blocking.
        self.ready(sys::POLLOUT)?;
        self.inner.write(&buf[..buf.len().min(sys::PIPE_BUF)])
    }
    fn flush(&mut self) -> std::io::Result<()> {
        self.inner.flush()
    }
}

#[cfg(not(unix))]
impl<T: Read> Read for Pipe<T> {
    fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
        self.inner.read(buf)
    }
}

#[cfg(not(unix))]
impl<T: Write> Write for Pipe<T> {
    fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
        self.inner.write(buf)
    }
    fn flush(&mut self) -> std::io::Result<()> {
        self.inner.flush()
    }
}

#[cfg(unix)]
mod sys {
    use std::os::raw::{c_int, c_short};

    #[repr(C)]
    pub struct PollFd {
        pub fd: c_int,
        pub events: c_short,
        pub revents: c_short,
    }

    #[cfg(any(target_os = "linux", target_os = "l4re"))]
    pub type Nfds = std::os::raw::c_ulong;
    #[cfg(not(any(target_os = "linux", target_os = "l4re")))]
    pub type Nfds = std::os::raw::c_uint;

    unsafe extern "C" {
        pub fn kill(pid: c_int, signal: c_int) -> c_int;
        pub fn poll(fds: *mut PollFd, nfds: Nfds, timeout: c_int) -> c_int;
        #[cfg(target_os = "linux")]
        pub fn waitid(idtype: c_int, id: u32, infop: *mut std::os::raw::c_void, options: c_int) -> c_int;
    }

    pub const SIGKILL: c_int = 9;
    pub const POLLIN: c_short = 0x1;
    pub const POLLOUT: c_short = 0x4;
    /// The pipe write size that never blocks once `poll` reports POLLOUT:
    /// one page on Linux, POSIX's minimum `PIPE_BUF` elsewhere.
    #[cfg(target_os = "linux")]
    pub const PIPE_BUF: usize = 4096;
    #[cfg(not(target_os = "linux"))]
    pub const PIPE_BUF: usize = 512;
}

#[cfg(unix)]
fn isolate(command: &mut Command) {
    use std::os::unix::process::CommandExt;
    command.process_group(0);
}

#[cfg(not(unix))]
fn isolate(_: &mut Command) {}

/// Kills an unreaped worker and its process group.
fn kill_tree(child: &mut Child) {
    #[cfg(unix)]
    kill_group(child.id());
    let _ = child.kill();
}

#[cfg(unix)]
fn kill_group(pid: u32) {
    // SAFETY: kill(2) with a negative pid signals a process group; the caller
    // guarantees the leader is unreaped, so the group id cannot be reused.
    unsafe {
        sys::kill(-(pid as i32), sys::SIGKILL);
    }
}

#[cfg(not(unix))]
fn kill_group(_: u32) {}

/// Test-only: makes Linux take the exit wait used where `waitid` is
/// unavailable. Not part of the adapter's interface.
#[doc(hidden)]
pub static TEST_WITHOUT_WAITID: AtomicBool = AtomicBool::new(false);

/// Whether the worker has exited, without reaping it: `Some(true)` once it
/// has, `Some(false)` while it runs, `None` where the platform cannot tell.
#[cfg(target_os = "linux")]
fn exited_unreaped(pid: u32) -> Option<bool> {
    if TEST_WITHOUT_WAITID.load(Ordering::SeqCst) {
        return None;
    }
    const P_PID: i32 = 1;
    const WEXITED: i32 = 4;
    const WNOHANG: i32 = 1;
    const WNOWAIT: i32 = 0x0100_0000;
    loop {
        // siginfo_t: 128 bytes, pointer-aligned; zeroed before each call.
        let mut info = [0u64; 16];
        // SAFETY: waitid writes at most sizeof(siginfo_t) == 128 bytes.
        let result = unsafe { sys::waitid(P_PID, pid, info.as_mut_ptr().cast(), WEXITED | WNOWAIT | WNOHANG) };
        if result == 0 {
            // si_signo, the first int on every Linux architecture, stays zero
            // while the child still runs.
            let si_signo = info[0].to_ne_bytes();
            return Some(i32::from_ne_bytes([si_signo[0], si_signo[1], si_signo[2], si_signo[3]]) != 0);
        }
        if std::io::Error::last_os_error().kind() != std::io::ErrorKind::Interrupted {
            return None;
        }
    }
}

#[cfg(not(target_os = "linux"))]
fn exited_unreaped(_: u32) -> Option<bool> {
    None
}

/// Names the POSIX signals common to Linux and macOS, with each platform's
/// numbering; other signals are reported by number.
#[cfg(unix)]
fn signal_name(signal: i32) -> String {
    #[cfg(all(target_os = "linux", not(any(target_arch = "mips", target_arch = "mips64", target_arch = "sparc64"))))]
    const PLATFORM: &[(i32, &str)] = &[
        (7, "SIGBUS"),
        (10, "SIGUSR1"),
        (12, "SIGUSR2"),
        (17, "SIGCHLD"),
        (18, "SIGCONT"),
        (19, "SIGSTOP"),
        (20, "SIGTSTP"),
        (21, "SIGTTIN"),
        (22, "SIGTTOU"),
        (23, "SIGURG"),
        (24, "SIGXCPU"),
        (25, "SIGXFSZ"),
        (26, "SIGVTALRM"),
        (27, "SIGPROF"),
        (28, "SIGWINCH"),
        (29, "SIGIO"),
        (31, "SIGSYS"),
    ];
    #[cfg(any(target_os = "macos", target_os = "ios"))]
    const PLATFORM: &[(i32, &str)] = &[
        (10, "SIGBUS"),
        (12, "SIGSYS"),
        (16, "SIGURG"),
        (17, "SIGSTOP"),
        (18, "SIGTSTP"),
        (19, "SIGCONT"),
        (20, "SIGCHLD"),
        (21, "SIGTTIN"),
        (22, "SIGTTOU"),
        (23, "SIGIO"),
        (24, "SIGXCPU"),
        (25, "SIGXFSZ"),
        (26, "SIGVTALRM"),
        (27, "SIGPROF"),
        (28, "SIGWINCH"),
        (30, "SIGUSR1"),
        (31, "SIGUSR2"),
    ];
    #[cfg(not(any(
        all(target_os = "linux", not(any(target_arch = "mips", target_arch = "mips64", target_arch = "sparc64"))),
        target_os = "macos",
        target_os = "ios"
    )))]
    const PLATFORM: &[(i32, &str)] = &[];
    const COMMON: &[(i32, &str)] = &[
        (1, "SIGHUP"),
        (2, "SIGINT"),
        (3, "SIGQUIT"),
        (4, "SIGILL"),
        (5, "SIGTRAP"),
        (6, "SIGABRT"),
        (8, "SIGFPE"),
        (9, "SIGKILL"),
        (11, "SIGSEGV"),
        (13, "SIGPIPE"),
        (14, "SIGALRM"),
        (15, "SIGTERM"),
    ];
    COMMON
        .iter()
        .chain(PLATFORM)
        .find(|(number, _)| *number == signal)
        .map_or_else(|| format!("SIG{signal}"), |(_, name)| (*name).to_owned())
}
