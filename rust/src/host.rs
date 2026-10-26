//! Invoke checked Jacquard terms through `jacquard-host-v0` and serve their
//! effect requests from a closed registry of Rust handlers. Each invocation
//! runs one fresh, serial worker.

use std::collections::BTreeMap;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc;
use std::thread;

use crate::carrier::{Cancel, CarrierError, Options, Worker};
use crate::json::{Json, Object, frame_bytes, object};
use crate::protocol::{
    CoreFrame, Diagnostic, EffectRequest, FailureCategory, HARD_LIMITS, Hash, Interface, Limits, OperationEntry, Session, State, Terminal,
    Value, invoke_frame,
};

/// Host evidence schema recorded beside Core's frames.
pub const EVIDENCE_SCHEMA: &str = "jacquard-host-rust-evidence-v0";

/// A handler's structured refusal or failure. Any other failure is reported
/// to Core as completion unknown, without its message.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum HandlerError {
    /// One of the four permitted failure categories, with a message for Core.
    Failure(FailureCategory, String),
    /// Anything else; reported as completion unknown.
    Unknown,
}

impl HandlerError {
    /// The host does not provide the operation (not started).
    pub fn unsupported(message: impl Into<String>) -> Self {
        HandlerError::Failure(FailureCategory::UnsupportedOperation, message.into())
    }
    /// The host refused to authorize the operation (not started).
    pub fn refused(message: impl Into<String>) -> Self {
        HandlerError::Failure(FailureCategory::RefusedAuthority, message.into())
    }
    /// An outside action failed without effect.
    pub fn outside_failed(message: impl Into<String>) -> Self {
        HandlerError::Failure(FailureCategory::OutsideFailure, message.into())
    }
    /// An outside action's completion is unknown.
    pub fn completion_unknown(message: impl Into<String>) -> Self {
        HandlerError::Failure(FailureCategory::CompletionUnknown, message.into())
    }
}

/// Any other error, such as an I/O error raised with `?`, is reported to
/// Core as completion unknown, without its message.
impl<E: std::error::Error> From<E> for HandlerError {
    fn from(_: E) -> Self {
        HandlerError::Unknown
    }
}

/// What a handler sees: whether the invocation is still live.
#[derive(Clone, Debug)]
pub struct HandlerContext {
    expired: Arc<AtomicBool>,
    ended: Arc<AtomicBool>,
    cancel: Cancel,
}

impl HandlerContext {
    #[cfg(test)]
    pub(crate) fn for_test() -> Self {
        HandlerContext { expired: Arc::default(), ended: Arc::default(), cancel: Cancel::new() }
    }

    /// Whether the worker expired, the caller cancelled, or the invocation
    /// ended. Handlers should stop; the host no longer waits for them.
    pub fn is_cancelled(&self) -> bool {
        self.expired.load(Ordering::SeqCst) || self.ended.load(Ordering::SeqCst) || self.cancel.is_cancelled()
    }
}

/// One effect operation's handler.
pub type Handler = Arc<dyn Fn(&HandlerContext, &[Value]) -> Result<Value, HandlerError> + Send + Sync>;

/// A closed map from exact operation identities to handlers.
#[derive(Clone, Default)]
pub struct OperationRegistry {
    handlers: BTreeMap<OperationEntry, Handler>,
}

impl OperationRegistry {
    /// An empty registry.
    pub fn new() -> Self {
        Self::default()
    }

    /// Binds a handler to one effect operation identity.
    pub fn bind<F>(&mut self, effect: Hash, operation: Hash, handler: F) -> Result<&mut Self, String>
    where
        F: Fn(&HandlerContext, &[Value]) -> Result<Value, HandlerError> + Send + Sync + 'static,
    {
        let key = OperationEntry { effect, operation };
        if self.handlers.contains_key(&key) {
            return Err("operation identity is already bound".into());
        }
        self.handlers.insert(key, Arc::new(handler));
        Ok(self)
    }

    /// The bound operations in protocol order.
    pub fn entries(&self) -> Vec<OperationEntry> {
        self.handlers.keys().cloned().collect()
    }

    fn handler(&self, request: &EffectRequest) -> Option<Handler> {
        self.handlers.get(&OperationEntry { effect: request.effect.clone(), operation: request.operation.clone() }).cloned()
    }
}

/// The kind of an invocation's result.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ResultKind {
    Ok,
    Error,
    Fatal,
    ShutdownAck,
    CarrierLoss,
    ProtocolError,
    HostError,
}

impl ResultKind {
    /// The kind's name, as the other adapters spell it.
    pub fn as_str(self) -> &'static str {
        match self {
            ResultKind::Ok => "ok",
            ResultKind::Error => "error",
            ResultKind::Fatal => "fatal",
            ResultKind::ShutdownAck => "shutdown_ack",
            ResultKind::CarrierLoss => "carrier_loss",
            ResultKind::ProtocolError => "protocol_error",
            ResultKind::HostError => "host_error",
        }
    }
}

/// Core's frames and evidence beside the host's observations.
#[derive(Clone, Debug)]
pub struct InvocationResult {
    pub kind: ResultKind,
    pub value: Option<Value>,
    pub diagnostics: Vec<Diagnostic>,
    pub terminal: Option<String>,
    pub evidence: Option<Object>,
    pub core_frames: Vec<Object>,
    pub host_frames: Vec<Object>,
    pub requests: Vec<EffectRequest>,
    pub exit_status: Option<i32>,
    pub signal: Option<String>,
    pub stderr: Vec<u8>,
    pub host_evidence: Object,
}

impl InvocationResult {
    fn new(kind: ResultKind) -> Self {
        InvocationResult {
            kind,
            value: None,
            diagnostics: vec![],
            terminal: None,
            evidence: None,
            core_frames: vec![],
            host_frames: vec![],
            requests: vec![],
            exit_status: None,
            signal: None,
            stderr: vec![],
            host_evidence: Object::new(),
        }
    }

    /// Whether Core returned a successful outcome.
    pub fn ok(&self) -> bool {
        self.kind == ResultKind::Ok
    }

    /// The first Core diagnostic code, else the host evidence code.
    pub fn code(&self) -> String {
        match self.diagnostics.first() {
            Some(diagnostic) if !diagnostic.code.is_empty() => diagnostic.code.clone(),
            _ => self.host_evidence.get("code").and_then(Json::as_str).unwrap_or("").to_owned(),
        }
    }

    /// A summary for people.
    pub fn message(&self) -> String {
        if self.ok() {
            return "Jacquard invocation succeeded".into();
        }
        let detail = match self.diagnostics.first() {
            Some(diagnostic) => format!("{} {}", diagnostic.summary, diagnostic.cause),
            None => self.host_evidence.get("detail").and_then(Json::as_str).map_or_else(|| self.kind.as_str().to_owned(), str::to_owned),
        };
        let code = self.code();
        format!("{}: {detail}", if code.is_empty() { self.kind.as_str().to_owned() } else { code })
    }

    /// The value, or an [`InvocationError`] classifying the failure.
    pub fn into_value(self) -> Result<Value, InvocationError> {
        if self.ok() {
            return Ok(self.value.clone().expect("an ok result has a value"));
        }
        Err(InvocationError(Box::new(self)))
    }
}

/// How an unsuccessful invocation failed.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Failure {
    /// The worker's lifetime deadline expired.
    DeadlineExceeded,
    /// The carrier was lost: spawn failure, closed pipes, bad exit, or abort.
    WorkerLost,
    /// An outside operation failed or its completion is unknown.
    OperationFailed,
    /// Core reported a diagnostic, or a frame was refused.
    Other,
}

/// An unsuccessful invocation.
#[derive(Clone, Debug)]
pub struct InvocationError(pub Box<InvocationResult>);

impl InvocationError {
    /// The classification.
    pub fn failure(&self) -> Failure {
        let result = &self.0;
        if result.host_evidence.get("reason") == Some(&Json::from("deadline")) {
            Failure::DeadlineExceeded
        } else if result.kind == ResultKind::CarrierLoss {
            Failure::WorkerLost
        } else if matches!(result.terminal.as_deref(), Some("host_failure" | "completion_unknown")) {
            Failure::OperationFailed
        } else {
            Failure::Other
        }
    }
}

impl std::fmt::Display for InvocationError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0.message())
    }
}

impl std::error::Error for InvocationError {}

/// Options for a [`Host`].
#[derive(Clone, Debug, Default)]
pub struct HostOptions {
    /// The worker command, environment, and lifetime deadline.
    pub carrier: Options,
    /// The session limits; `None` selects the hard limits.
    pub limits: Option<Limits>,
}

/// Invokes terms in one prepared store.
#[derive(Clone, Debug)]
pub struct Host {
    pub store: String,
    pub limits: Limits,
    options: Options,
}

impl Host {
    /// Validates options before any worker starts.
    pub fn new(store: impl Into<String>, options: HostOptions) -> Result<Host, String> {
        let limits = options.limits.unwrap_or(HARD_LIMITS);
        limits.validate().map_err(|error| error.to_string())?;
        options.carrier.validate()?;
        Ok(Host { store: store.into(), limits, options: options.carrier })
    }

    /// Runs `target` with `args`. The invoke frame is validated before a
    /// worker starts. Cancelling `cancel` kills the worker; nothing is retried.
    pub fn invoke(
        &self,
        target: &Hash,
        spec: &Interface,
        args: &[Value],
        registry: &OperationRegistry,
        cancel: &Cancel,
    ) -> InvocationResult {
        let checked = invoke_frame(target, spec, args, &registry.entries(), &self.limits)
            .and_then(|frame| frame_bytes(&frame, self.limits.max_frame_bytes, self.limits.max_json_depth));
        if let Err(error) = checked {
            let mut result = InvocationResult::new(ResultKind::HostError);
            result.host_evidence = object([
                ("code", error.code.into()),
                ("detail", error.message.into()),
                ("reason", "invoke_refused_locally".into()),
                ("schema", EVIDENCE_SCHEMA.into()),
            ]);
            return result;
        }
        self.run(|session| session.invoke(target, spec, args, &registry.entries()), registry, cancel)
    }

    /// Negotiates a session and shuts it down without invoking.
    pub fn shutdown_probe(&self, cancel: &Cancel) -> InvocationResult {
        self.run(Session::shutdown, &OperationRegistry::new(), cancel)
    }

    fn run<F>(&self, first: F, registry: &OperationRegistry, cancel: &Cancel) -> InvocationResult
    where
        F: FnOnce(&mut Session) -> Result<Object, crate::ProtocolError>,
    {
        let mut session = Session::new();
        let mut result = InvocationResult::new(ResultKind::CarrierLoss);
        let mut worker = match Worker::start(&self.store, &self.options, cancel.clone()) {
            Ok(worker) => worker,
            Err(message) => {
                result.host_evidence =
                    object([("detail", message.into()), ("reason", "invalid_options".into()), ("schema", EVIDENCE_SCHEMA.into())]);
                return result;
            }
        };
        let mut sending = false;
        let ended = Arc::new(AtomicBool::new(false));
        let outcome = self.drive(&mut worker, &mut session, &mut result, &mut sending, first, registry, cancel, &ended);
        if let Err(error) = outcome {
            let (kind, code, reason) = match &error {
                CarrierError::Protocol(protocol) if sending => (ResultKind::HostError, protocol.code, "host_frame_refused"),
                CarrierError::Protocol(protocol) => (ResultKind::ProtocolError, protocol.code, "core_frame_refused"),
                CarrierError::Loss { timeout: true, .. } => (ResultKind::CarrierLoss, "E1611", "deadline"),
                CarrierError::Loss { .. } if worker.spawn_error.is_some() => (ResultKind::CarrierLoss, "E1611", "worker_spawn_failed"),
                CarrierError::Loss { aborted: true, .. } => (ResultKind::CarrierLoss, "E1611", "aborted"),
                CarrierError::Loss { .. } => (ResultKind::CarrierLoss, "E1611", "carrier_loss"),
            };
            result.kind = kind;
            result.host_evidence = object([
                ("code", code.into()),
                ("detail", error.to_string().into()),
                ("outstanding_request", session.pending.map_or(Json::Null, |ordinal| Json::Int(ordinal as i64))),
                ("reason", reason.into()),
                ("schema", EVIDENCE_SCHEMA.into()),
                ("state", session.state.as_str().into()),
            ]);
        }
        worker.dispose();
        ended.store(true, Ordering::SeqCst);
        result.requests = session.requests.clone();
        result.exit_status = worker.exit_status;
        result.signal = worker.signal.clone();
        result.stderr = worker.stderr();
        result
    }

    #[allow(clippy::too_many_arguments)]
    fn drive<F>(
        &self,
        worker: &mut Worker,
        session: &mut Session,
        result: &mut InvocationResult,
        sending: &mut bool,
        first: F,
        registry: &OperationRegistry,
        cancel: &Cancel,
        ended: &Arc<AtomicBool>,
    ) -> Result<(), CarrierError>
    where
        F: FnOnce(&mut Session) -> Result<Object, crate::ProtocolError>,
    {
        let mut send = |worker: &mut Worker, frame: Object, limits: &Limits, result: &mut InvocationResult| -> Result<(), CarrierError> {
            *sending = true;
            worker.write_frame(&frame, limits)?;
            result.host_frames.push(frame);
            *sending = false;
            Ok(())
        };
        let hello = worker.read_frame(&HARD_LIMITS)?.ok_or_else(|| loss("worker exited before core_hello"))?;
        result.core_frames.push(hello.clone());
        session.receive(&hello)?;
        let selected = session.select(self.limits)?;
        send(worker, selected, &HARD_LIMITS, result)?;
        worker.set_stderr_limit(self.limits.max_stderr_bytes);
        let opening = first(session)?;
        send(worker, opening, &session.limits.clone(), result)?;
        while session.state != State::Closed {
            let frame = worker.read_frame(&session.limits.clone())?.ok_or_else(|| loss("worker exited before a terminal frame"))?;
            result.core_frames.push(frame.clone());
            match session.receive(&frame)? {
                Some(CoreFrame::Request(request)) => {
                    let response = respond(worker, session, registry, &request, cancel, ended)?;
                    send(worker, response, &session.limits.clone(), result)?;
                }
                Some(CoreFrame::Terminal(Terminal::Outcome { value, diagnostics, terminal, evidence, .. })) => {
                    result.kind = if value.is_some() { ResultKind::Ok } else { ResultKind::Error };
                    result.value = value;
                    result.diagnostics = diagnostics;
                    result.terminal = Some(terminal);
                    result.evidence = Some(evidence);
                }
                Some(CoreFrame::Terminal(Terminal::Fatal { diagnostics, .. })) => {
                    result.kind = ResultKind::Fatal;
                    result.diagnostics = diagnostics;
                }
                Some(CoreFrame::Terminal(Terminal::ShutdownAck { .. })) => result.kind = ResultKind::ShutdownAck,
                None => {}
            }
        }
        worker.close_input();
        if let Some(extra) = worker.read_frame(&session.limits.clone())? {
            result.core_frames.push(extra.clone());
            session.receive(&extra)?;
        }
        worker.wait()?;
        if worker.exit_status != Some(0) {
            return Err(loss("worker exited unsuccessfully after its terminal"));
        }
        Ok(())
    }
}

fn loss(message: &str) -> CarrierError {
    CarrierError::Loss { message: message.into(), timeout: false, aborted: false }
}

/// Runs the handler on its own thread, bounded by the worker lifetime, and
/// renders the response. A handler still running at the deadline is
/// abandoned: no late response is sent and nothing is retried.
fn respond(
    worker: &mut Worker,
    session: &mut Session,
    registry: &OperationRegistry,
    request: &EffectRequest,
    cancel: &Cancel,
    ended: &Arc<AtomicBool>,
) -> Result<Object, CarrierError> {
    let answer = match registry.handler(request) {
        None => Err(HandlerError::unsupported("operation is not configured")),
        Some(handler) => {
            let context = HandlerContext { expired: worker.expired_flag(), ended: Arc::clone(ended), cancel: cancel.clone() };
            let arguments = request.arguments.clone();
            let (tx, rx) = mpsc::channel();
            let spawned = thread::Builder::new().name("jacquard-handler".into()).spawn(move || {
                let outcome = catch_unwind(AssertUnwindSafe(|| handler(&context, &arguments)));
                let _ = tx.send(outcome.unwrap_or_else(|_| Err(HandlerError::completion_unknown("handler panicked"))));
            });
            match spawned {
                // The handler never ran.
                Err(_) => Err(HandlerError::outside_failed("handler thread could not start")),
                Ok(_) => match worker.wait_for(&rx)? {
                    Some(answer) => answer,
                    None => Err(HandlerError::completion_unknown("handler panicked")),
                },
            }
        }
    };
    if let Some(fault) = worker.fault() {
        return Err(fault);
    }
    let failure = match answer {
        Ok(value) => match session.ok_frame(&value) {
            Ok(_) => return Ok(session.respond_ok(&value)?),
            Err(_) => (FailureCategory::OutsideFailure, "handler result has no encoding within the selected limits".to_owned()),
        },
        Err(HandlerError::Failure(category, message)) => (category, message),
        Err(HandlerError::Unknown) => (FailureCategory::CompletionUnknown, "handler returned an error".to_owned()),
    };
    let message = bounded(&failure.1, session.limits.max_host_message_bytes);
    Ok(session.respond_failure(failure.0, &message)?)
}

/// At most `limit` bytes of `text`, cut at a character boundary.
fn bounded(text: &str, limit: usize) -> String {
    if text.len() <= limit {
        return text.to_owned();
    }
    let mut end = limit;
    while !text.is_char_boundary(end) {
        end -= 1;
    }
    text[..end].to_owned()
}
