//! Call checked Jacquard functions from Rust: prepare and cache programs
//! with Core's public commands, map native Rust values, bind Rust closures to
//! exact operation identities, and keep the raw evidence of every
//! invocation. Each invocation runs one fresh, serial worker.
//!
//! ```no_run
//! use jacquard_host::sdk::{PrepareOptions, Runtime, RuntimeOptions};
//!
//! # fn main() -> Result<(), jacquard_host::sdk::Error> {
//! let runtime = Runtime::new(RuntimeOptions::default())?;
//! let program = runtime.prepare(&["rules.jac"], &PrepareOptions::default())?;
//! let shipping = program.function("shipping-cost")?;
//! let cost: i64 = shipping.call((35,))?;
//! # Ok(())
//! # }
//! ```

mod core;
mod data;
mod identities;
mod operations;
#[cfg(test)]
mod tests;

use std::collections::{BTreeMap, BTreeSet};
use std::ffi::OsStr;
use std::fmt;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::Duration;

use crate::carrier::{Cancel, Options};
use crate::compat::{CORE_VERSION, PROTOCOL};
use crate::host::{Failure, Host, HostOptions, InvocationError, InvocationResult, OperationRegistry};
use crate::json::{Json, Object, decode_object, frame_bytes, stringify};
use crate::protocol::{BoundaryType, HARD_LIMITS, Hash, Interface, Limits, Value, invoke_frame};

pub use data::{Data, Decode, Decoder, Dynamic, Encode, Encoder, Fields};
pub use operations::{IntoHandler, Operations, WithContext};

use identities::{Entry, Group, check_groups, hash_groups, normal, parse_signature};

const PROGRAM_SCHEMA: &str = "jacquard-rust-program-v1";

/// Prepared programs live here unless [`PrepareOptions::cache_dir`] is set.
pub const DEFAULT_CACHE_DIR: &str = ".scratch/jacquard-host-rust";

/// A local input that cannot be sent; no worker started.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct InvalidArgument {
    pub message: String,
}

impl InvalidArgument {
    /// An error at a value's location, such as `shipping-cost.argument[0]`.
    pub fn at(path: &str, message: impl fmt::Display) -> Self {
        InvalidArgument { message: format!("{path}: {message}") }
    }
}

impl fmt::Display for InvalidArgument {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.message)
    }
}

impl std::error::Error for InvalidArgument {}

/// A boundary value that does not decode into the requested Rust type.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct MappingError {
    pub message: String,
}

impl MappingError {
    pub fn new(message: impl Into<String>) -> Self {
        MappingError { message: message.into() }
    }
}

impl fmt::Display for MappingError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.message)
    }
}

impl std::error::Error for MappingError {}

/// An SDK failure.
#[derive(Clone, Debug)]
pub enum Error {
    /// The program could not be checked, hashed, installed, or loaded from
    /// its cache.
    Preparation(String),
    /// A mapping or binding problem: an unknown name, a missing handler, or
    /// a successful Core result that does not decode into the requested
    /// type. `result` is set when Core completed.
    Binding { message: String, result: Option<Box<InvocationResult>> },
    /// A local input that cannot be sent; no worker started.
    InvalidArgument(InvalidArgument),
    /// The invocation ran and did not succeed; classify it with
    /// [`Error::failure`].
    Invocation(InvocationError),
}

impl Error {
    pub(crate) fn preparation(message: impl Into<String>) -> Self {
        Error::Preparation(message.into())
    }

    pub(crate) fn binding(message: impl Into<String>) -> Self {
        Error::Binding { message: message.into(), result: None }
    }

    /// How an invocation failed, for [`Error::Invocation`].
    pub fn failure(&self) -> Option<Failure> {
        match self {
            Error::Invocation(error) => Some(error.failure()),
            _ => None,
        }
    }

    /// The raw result, when a worker ran.
    pub fn result(&self) -> Option<&InvocationResult> {
        match self {
            Error::Invocation(error) => Some(&error.0),
            Error::Binding { result, .. } => result.as_deref(),
            _ => None,
        }
    }
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Error::Preparation(message) | Error::Binding { message, .. } => f.write_str(message),
            Error::InvalidArgument(error) => error.fmt(f),
            Error::Invocation(error) => error.fmt(f),
        }
    }
}

impl std::error::Error for Error {}

impl From<InvalidArgument> for Error {
    fn from(error: InvalidArgument) -> Self {
        Error::InvalidArgument(error)
    }
}

impl From<InvocationError> for Error {
    fn from(error: InvocationError) -> Self {
        Error::Invocation(error)
    }
}

/// Which explicit batch failed. Completed batches are not retried; their
/// decoded values are kept in `completed`.
#[derive(Clone, Debug)]
pub struct BatchError<R> {
    /// The failed batch, or `None` when a batch was refused locally before
    /// any worker started.
    pub index: Option<usize>,
    pub completed: Vec<R>,
    pub error: Error,
}

impl<R> fmt::Display for BatchError<R> {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self.index {
            Some(index) => write!(f, "batch {index}: {}", self.error),
            None => self.error.fmt(f),
        }
    }
}

impl<R: fmt::Debug> std::error::Error for BatchError<R> {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        Some(&self.error)
    }
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// One discovery: empty until a run succeeds.
type Flight = Arc<Mutex<Option<Vec<Group>>>>;

/// Options for Core discovery and invocation bounds.
#[derive(Clone, Debug, Default)]
pub struct RuntimeOptions {
    /// The Core executable; defaults to `$JACQUARD_BIN`, then `jacquard`.
    pub binary: Option<String>,
    /// Core's prelude directory; defaults to `$JACQUARD_PRELUDE`.
    pub prelude: Option<PathBuf>,
    /// Bounds each invocation's worker lifetime (default 30 seconds).
    pub deadline: Option<Duration>,
    /// The session limits; `None` selects the hard limits.
    pub limits: Option<Limits>,
}

/// Prepares programs against one Core installation. Clones share one
/// version check and prelude discovery.
#[derive(Clone)]
pub struct Runtime {
    inner: Arc<RuntimeInner>,
}

impl fmt::Debug for Runtime {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Runtime").field("binary", &self.inner.binary).field("prelude", &self.inner.prelude).finish_non_exhaustive()
    }
}

struct RuntimeInner {
    binary: String,
    prelude: PathBuf,
    deadline: Option<Duration>,
    limits: Limits,
    /// Version checks and prelude discoveries, each run once per key; a
    /// failed run leaves its slot empty for the next caller.
    flights: Mutex<BTreeMap<String, Flight>>,
    commands: AtomicUsize,
}

/// Options for one preparation.
#[derive(Clone, Debug, Default)]
pub struct PrepareOptions {
    /// Holds verified prepared programs (default [`DEFAULT_CACHE_DIR`]).
    pub cache_dir: Option<PathBuf>,
}

/// Removes a staging directory unless it was published.
struct Staging(PathBuf);

impl Drop for Staging {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn staging_directory(cache: &Path) -> Result<Staging, Error> {
    static NEXT: AtomicUsize = AtomicUsize::new(0);
    let nanos = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map_or(0, |elapsed| elapsed.subsec_nanos());
    for _ in 0..100 {
        let name = format!("prepare-{}-{nanos:08x}-{}", std::process::id(), NEXT.fetch_add(1, Ordering::SeqCst));
        let path = cache.join(name);
        match std::fs::create_dir(&path) {
            Ok(()) => return Ok(Staging(path)),
            Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => {}
            Err(error) => return Err(Error::preparation(format!("cannot create {}: {error}", path.display()))),
        }
    }
    Err(Error::preparation("cannot create a staging directory"))
}

fn io_error(context: impl fmt::Display) -> impl FnOnce(std::io::Error) -> Error {
    move |error| Error::preparation(format!("{context}: {error}"))
}

/// The prepared metadata in `program.json`.
struct Metadata {
    identity: BTreeMap<String, String>,
    names: Vec<(String, Hash)>,
    constructors: Vec<(String, Hash)>,
    signatures: BTreeMap<String, String>,
    groups: Vec<Group>,
    files: BTreeMap<String, String>,
}

fn string_map(map: &BTreeMap<String, String>) -> Json {
    Json::Object(map.iter().map(|(key, value)| (key.clone(), Json::Str(value.clone()))).collect())
}

fn pairs(list: &[(String, Hash)]) -> Json {
    Json::Array(list.iter().map(|(name, hash)| Json::Array(vec![name.as_str().into(), hash.as_str().into()])).collect())
}

impl Metadata {
    fn to_json(&self) -> String {
        let groups = self.groups.iter().map(|group| {
            Json::Array(
                group
                    .iter()
                    .map(|entry| {
                        Json::Object(Object::from([
                            ("hash".into(), entry.hash.as_str().into()),
                            ("name".into(), entry.name.as_str().into()),
                        ]))
                    })
                    .collect(),
            )
        });
        let object = Object::from([
            ("constructors".into(), pairs(&self.constructors)),
            ("files".into(), string_map(&self.files)),
            ("groups".into(), Json::Array(groups.collect())),
            ("identity".into(), string_map(&self.identity)),
            ("names".into(), pairs(&self.names)),
            ("signatures".into(), string_map(&self.signatures)),
        ]);
        stringify(&Json::Object(object), 8).expect("bounded metadata")
    }

    fn from_json(text: &[u8]) -> Option<Metadata> {
        let object = decode_object(text, 8).ok()?;
        let map = |key: &str| -> Option<BTreeMap<String, String>> {
            object.get(key)?.as_object()?.iter().map(|(key, value)| Some((key.clone(), value.as_str()?.to_owned()))).collect()
        };
        let pairs = |key: &str| -> Option<Vec<(String, Hash)>> {
            object
                .get(key)?
                .as_array()?
                .iter()
                .map(|pair| match pair.as_array()? {
                    [name, hash] => Some((name.as_str()?.to_owned(), Hash::parse(hash.as_str()?).ok()?)),
                    _ => None,
                })
                .collect()
        };
        let groups = object
            .get("groups")?
            .as_array()?
            .iter()
            .map(|group| {
                group
                    .as_array()?
                    .iter()
                    .map(|entry| {
                        let entry = entry.as_object()?;
                        Some(Entry { name: entry.get("name")?.as_str()?.to_owned(), hash: Hash::parse(entry.get("hash")?.as_str()?).ok()? })
                    })
                    .collect()
            })
            .collect::<Option<_>>()?;
        Some(Metadata {
            identity: map("identity")?,
            names: pairs("names")?,
            constructors: pairs("constructors")?,
            signatures: map("signatures")?,
            groups,
            files: map("files")?,
        })
    }
}

impl Runtime {
    /// Validates options; Core does not run yet.
    pub fn new(options: RuntimeOptions) -> Result<Runtime, Error> {
        // Like the Go SDK, an empty option means unset.
        let binary = options
            .binary
            .filter(|value| !value.is_empty())
            .or_else(|| std::env::var("JACQUARD_BIN").ok().filter(|value| !value.is_empty()));
        let prelude = options
            .prelude
            .filter(|value| !value.as_os_str().is_empty())
            .or_else(|| std::env::var_os("JACQUARD_PRELUDE").filter(|value| !value.is_empty()).map(PathBuf::from));
        let Some(prelude) = prelude else {
            return Err(Error::binding(
                "set JACQUARD_PRELUDE or RuntimeOptions::prelude; Core and prelude must match the compatibility pin",
            ));
        };
        let prelude = std::path::absolute(&prelude).map_err(|error| Error::binding(error.to_string()))?;
        if prelude.to_str().is_none() {
            return Err(Error::binding("the prelude path must be UTF-8"));
        }
        if options.deadline == Some(Duration::ZERO) {
            return Err(Error::binding("deadline must be positive"));
        }
        let limits = options.limits.unwrap_or(HARD_LIMITS);
        limits.validate().map_err(|error| Error::binding(format!("invalid runtime limits: {error}")))?;
        Ok(Runtime {
            inner: Arc::new(RuntimeInner {
                binary: binary.unwrap_or_else(|| "jacquard".into()),
                prelude,
                deadline: options.deadline,
                limits,
                flights: Mutex::new(BTreeMap::new()),
                commands: AtomicUsize::new(0),
            }),
        })
    }

    /// Test-only: how many Core commands this runtime has run.
    #[doc(hidden)]
    pub fn core_commands(&self) -> usize {
        self.inner.commands.load(Ordering::SeqCst)
    }

    fn command(&self, binary: &Path, arguments: &[&OsStr]) -> Result<String, Error> {
        self.inner.commands.fetch_add(1, Ordering::SeqCst);
        core::run(binary, &self.inner.prelude, arguments)
    }

    /// Runs `work` once per key. Concurrent callers wait for one run; a
    /// successful result stays cached, and a failed or panicked run leaves
    /// the next caller to try again.
    fn once(&self, key: String, work: impl FnOnce() -> Result<Vec<Group>, Error>) -> Result<Vec<Group>, Error> {
        let slot = lock(&self.inner.flights).entry(key).or_default().clone();
        let mut done = lock(&slot);
        if let Some(groups) = done.as_ref() {
            return Ok(groups.clone());
        }
        let groups = work()?;
        *done = Some(groups.clone());
        Ok(groups)
    }

    /// Runs `--version` once per executable digest. Like the other SDKs, it
    /// accepts output whose last field is the pinned version.
    fn check_version(&self, binary: &Path, binary_hash: &str) -> Result<(), Error> {
        self.once(format!("version:{binary_hash}"), || {
            let version = self.command(binary, &[OsStr::new("--version")])?;
            if version.split_whitespace().last() != Some(CORE_VERSION) {
                return Err(Error::preparation(format!(
                    "expected pinned Core version {CORE_VERSION}, found {:?}; the exact release is also required",
                    version.trim()
                )));
            }
            Ok(Vec::new())
        })
        .map(drop)
    }

    fn prelude_groups(&self, binary: &Path, key: &str) -> Result<Vec<Group>, Error> {
        self.once(format!("prelude:{key}"), || {
            let mut groups = Vec::new();
            for name in ["01-prim.jqd", "02-data.jqd", "19-audit.jqd"] {
                let path = self.inner.prelude.join(name);
                groups.extend(hash_groups(&self.command(binary, &[OsStr::new("hash"), path.as_os_str()])?)?);
            }
            Ok(groups)
        })
    }

    /// Checks, hashes, and installs first-party declaration sources (`.jac`
    /// files, or `.jqd` files, in dependency order) into a verified cache
    /// keyed by the source bytes, Core executable bytes, prelude contents,
    /// and protocol.
    pub fn prepare<P: AsRef<Path>>(&self, sources: &[P], options: &PrepareOptions) -> Result<Program, Error> {
        let usage = || Error::preparation("pass .jac files or .jqd files in dependency order");
        let extension = |path: &Path| path.extension().and_then(OsStr::to_str).map(|extension| format!(".{extension}"));
        let syntax = sources.first().and_then(|source| extension(source.as_ref())).ok_or_else(usage)?;
        if !matches!(syntax.as_str(), ".jac" | ".jqd") || sources.iter().any(|source| extension(source.as_ref()).as_ref() != Some(&syntax))
        {
            return Err(usage());
        }
        let cache = options.cache_dir.clone().unwrap_or_else(|| PathBuf::from(DEFAULT_CACHE_DIR));
        let cache = std::path::absolute(&cache).map_err(io_error("cannot resolve the cache directory"))?;
        let binary = core::executable(&self.inner.binary)?;
        let binary_hash = core::file_digest(&binary).map_err(io_error(format!("cannot read {}", binary.display())))?;
        self.check_version(&binary, &binary_hash)?;
        let prelude_files = core::tree(&self.inner.prelude)
            .map_err(|error| Error::preparation(format!("cannot read prelude {}: {error}", self.inner.prelude.display())))?;
        let prelude_hash = core::map_digest(&prelude_files);
        let mut content = Vec::new();
        for (index, source) in sources.iter().enumerate() {
            let source = source.as_ref();
            if index > 0 {
                content.push(b'\n');
            }
            content.extend(std::fs::read(source).map_err(io_error(format!("cannot read {}", source.display())))?);
        }
        let identity = BTreeMap::from([
            ("binary".to_owned(), binary_hash.clone()),
            ("prelude".to_owned(), prelude_hash.clone()),
            ("protocol".to_owned(), PROTOCOL.to_owned()),
            ("schema".to_owned(), PROGRAM_SCHEMA.to_owned()),
            ("source".to_owned(), core::digest(&content)),
            ("syntax".to_owned(), syntax.clone()),
        ]);
        let directory = cache.join(core::map_digest(&identity));
        std::fs::create_dir_all(&cache).map_err(io_error(format!("cannot create {}", cache.display())))?;
        if directory.exists() {
            return self.load(&directory, &identity, &binary);
        }
        let staging = staging_directory(&cache)?;
        let snapshot = staging.0.join(format!("source{syntax}"));
        std::fs::write(&snapshot, &content).map_err(io_error("cannot write the source snapshot"))?;
        let output = self.command(&binary, &[OsStr::new("check"), snapshot.as_os_str(), OsStr::new("--print-sigs")])?;
        let signatures = output
            .split('\n')
            .filter_map(|line| line.trim().split_once(" : "))
            .filter(|(name, _)| !name.contains([' ', '\t']))
            .map(|(name, signature)| (name.to_owned(), signature.to_owned()))
            .collect();
        let mut groups = self.prelude_groups(&binary, &format!("{binary_hash}{prelude_hash}"))?;
        groups.extend(hash_groups(&self.command(&binary, &[OsStr::new("hash"), snapshot.as_os_str()])?)?);
        check_groups(&groups)?;
        let store = staging.0.join("store");
        self.command(&binary, &[OsStr::new("run"), snapshot.as_os_str(), OsStr::new("--store"), store.as_os_str()])?;
        let mut names: Vec<(String, Hash)> = Vec::new();
        let mut constructors: BTreeMap<String, Hash> = BTreeMap::new();
        let mut order: Vec<String> = Vec::new();
        let mut seen = BTreeSet::new();
        for entry in groups.iter().flatten() {
            if seen.insert(entry.name.clone()) {
                names.push((entry.name.clone(), entry.hash.clone()));
            }
            if constructors.insert(entry.name.clone(), entry.hash.clone()).is_none() {
                order.push(entry.name.clone());
            }
        }
        let constructors = order.into_iter().map(|name| (name.clone(), constructors[&name].clone())).collect();
        let metadata = Metadata { identity: identity.clone(), names, constructors, signatures, groups, files: core::tree(&staging.0)? };
        std::fs::write(staging.0.join("program.json"), metadata.to_json()).map_err(io_error("cannot write program.json"))?;
        if let Err(error) = std::fs::rename(&staging.0, &directory) {
            // A concurrent preparation published the same artifact first.
            if !directory.exists() {
                return Err(Error::preparation(format!("cannot publish {}: {error}", directory.display())));
            }
        }
        drop(staging);
        self.load(&directory, &identity, &binary)
    }

    fn load(&self, directory: &Path, identity: &BTreeMap<String, String>, binary: &Path) -> Result<Program, Error> {
        let fail = |detail: &str| {
            Error::preparation(format!(
                "prepared cache {} is incomplete or changed ({detail}); remove that cache entry and prepare again",
                directory.display()
            ))
        };
        let text = std::fs::read(directory.join("program.json")).map_err(|error| fail(&error.to_string()))?;
        let metadata = Metadata::from_json(&text).ok_or_else(|| fail("unreadable program.json"))?;
        let mut observed = core::tree(directory).map_err(|error| fail(&error.to_string()))?;
        observed.remove("program.json");
        if &metadata.identity != identity || observed != metadata.files {
            return Err(fail("cached artifact digests do not match"));
        }
        check_groups(&metadata.groups)?;
        Program::new(self.clone(), directory.to_owned(), metadata, binary)
    }
}

#[derive(Clone, Debug)]
struct EffectGroup {
    identity: Hash,
    operations: Vec<Entry>,
}

/// A prepared, verified program.
pub struct Program {
    runtime: Runtime,
    directory: PathBuf,
    data: Arc<Data>,
    host: Host,
    interfaces: BTreeMap<String, Interface>,
    effects: BTreeMap<String, EffectGroup>,
    services: BTreeMap<String, Operations>,
}

impl fmt::Debug for Program {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Program").field("directory", &self.directory).field("functions", &self.interfaces.keys()).finish_non_exhaustive()
    }
}

impl Program {
    fn new(runtime: Runtime, directory: PathBuf, metadata: Metadata, binary: &Path) -> Result<Program, Error> {
        let names: BTreeMap<String, Hash> = metadata.names.into_iter().collect();
        let constructors = metadata.constructors.into_iter().collect();
        let prelude = runtime.inner.prelude.to_str().expect("checked UTF-8").to_owned();
        let env = std::env::vars_os()
            // The carrier takes UTF-8 pairs; other variables are not passed.
            .filter_map(|(key, value)| Some((key.into_string().ok()?, value.into_string().ok()?)))
            .filter(|(key, _)| key != "JACQUARD_PRELUDE")
            .chain([("JACQUARD_PRELUDE".to_owned(), prelude)])
            .collect();
        let store = directory.join("store");
        let carrier = Options {
            command: vec![binary.to_str().ok_or_else(|| Error::binding("the Core executable path must be UTF-8"))?.to_owned()],
            env: Some(env),
            deadline: runtime.inner.deadline,
        };
        let host =
            Host::new(store.to_string_lossy(), HostOptions { carrier, limits: Some(runtime.inner.limits) }).map_err(Error::binding)?;
        let mut interfaces = BTreeMap::new();
        let mut used = BTreeSet::new();
        for (name, signature) in &metadata.signatures {
            if let Ok(interface) = parse_signature(signature, &names) {
                used.extend(interface.effects.iter().cloned());
                interfaces.insert(name.clone(), interface);
            }
        }
        let mut effects = BTreeMap::new();
        for group in &metadata.groups {
            for entry in group.iter().filter(|entry| used.contains(&entry.hash)) {
                let operations = group.iter().filter(|other| other.hash != entry.hash).cloned().collect();
                effects.insert(entry.name.clone(), EffectGroup { identity: entry.hash.clone(), operations });
            }
        }
        let data = Arc::new(Data::new(names, constructors, &metadata.groups, runtime.inner.limits));
        Ok(Program { runtime, directory, data, host, interfaces, effects, services: BTreeMap::new() })
    }

    /// The runtime that prepared this program.
    pub fn runtime(&self) -> &Runtime {
        &self.runtime
    }

    /// The verified cache entry.
    pub fn directory(&self) -> &Path {
        &self.directory
    }

    /// The program's names and identities.
    pub fn data(&self) -> &Data {
        &self.data
    }

    /// Test-only: replaces the worker command for functions bound
    /// afterwards.
    #[doc(hidden)]
    pub fn set_worker_command(&mut self, command: Vec<String>) {
        let store = self.host.store.clone();
        let carrier = Options { command, env: None, deadline: self.runtime.inner.deadline };
        self.host = Host::new(store, HostOptions { carrier, limits: Some(self.host.limits) }).expect("valid options");
    }

    /// Configures an effect's service for every function bound afterwards,
    /// by effect name such as `"Inventory"`.
    pub fn provide(&mut self, effect: &str, operations: Operations) -> Result<&mut Self, Error> {
        if !self.effects.contains_key(&normal(effect)) {
            return Err(Error::binding(format!("unknown effect {effect}")));
        }
        self.services.insert(normal(effect), operations);
        Ok(self)
    }

    /// Binds a checked public function, using the provided services.
    pub fn function(&self, name: &str) -> Result<Function, Error> {
        self.function_with(name, [])
    }

    /// Binds a checked public function with services for its effects, by
    /// effect name, overriding [`Program::provide`]. Every operation of each
    /// checked root effect must have a handler.
    pub fn function_with<'a>(&self, name: &str, effects: impl IntoIterator<Item = (&'a str, Operations)>) -> Result<Function, Error> {
        let Some(signature) = self.interfaces.get(name) else {
            return Err(Error::binding(format!("{name}: absent or unsupported closed v0 signature")));
        };
        let Some(target) = self.data.name(name) else {
            return Err(Error::binding(format!("{name}: no public identity")));
        };
        let selected: BTreeMap<&String, &EffectGroup> =
            self.effects.iter().filter(|(_, effect)| signature.effects.contains(&effect.identity)).collect();
        if selected.len() != signature.effects.len() {
            return Err(Error::binding(format!("{name}: effect metadata is unavailable; use the low-level host module")));
        }
        let mut services = self.services.clone();
        for (effect, operations) in effects {
            if !selected.contains_key(&normal(effect)) {
                return Err(Error::binding(format!("{name} does not use {effect}")));
            }
            services.insert(normal(effect), operations);
        }
        let mut registry = OperationRegistry::new();
        let mut missing = Vec::new();
        for (effect_name, effect) in &selected {
            let service = services.get(*effect_name);
            // One service may serve several effects, but every name it
            // binds must be some effect's operation.
            if let Some(key) = service.and_then(|service| {
                service
                    .handlers
                    .keys()
                    .find(|key| !self.effects.values().flat_map(|effect| &effect.operations).any(|operation| &operation.name == *key))
            }) {
                return Err(Error::binding(format!("{name}: {effect_name} has no operation {key}")));
            }
            for operation in &effect.operations {
                let Some(handler) = service.and_then(|service| service.handlers.get(&operation.name)).cloned() else {
                    missing.push(format!("{effect_name}.{}", operation.name));
                    continue;
                };
                let data = self.data.clone();
                registry
                    .bind(effect.identity.clone(), operation.hash.clone(), move |context, arguments| handler(&data, context, arguments))
                    .map_err(Error::binding)?;
            }
        }
        if !missing.is_empty() {
            return Err(Error::binding(format!(
                "{name}: missing handlers: {}. Bind every operation in each declared effect before calling",
                missing.join(", ")
            )));
        }
        Ok(Function {
            name: name.to_owned(),
            signature: signature.clone(),
            target: target.clone(),
            registry,
            host: self.host.clone(),
            data: self.data.clone(),
        })
    }
}

/// A function's arguments: a tuple of [`Encode`] values, such as `(35,)`,
/// or a slice of `&dyn Encode`.
pub trait Arguments {
    #[doc(hidden)]
    fn encode_all(&self, data: &Data, name: &str, first: usize) -> Result<Vec<Value>, InvalidArgument>;
}

fn argument(data: &Data, name: &str, index: usize, value: &(impl Encode + ?Sized)) -> Result<Value, InvalidArgument> {
    value.encode(&Encoder::root(data, format!("{name}.argument[{index}]"), 3)?)
}

impl Arguments for [&dyn Encode] {
    fn encode_all(&self, data: &Data, name: &str, first: usize) -> Result<Vec<Value>, InvalidArgument> {
        self.iter().enumerate().map(|(index, value)| argument(data, name, first + index, *value)).collect()
    }
}

impl<const N: usize> Arguments for [&dyn Encode; N] {
    fn encode_all(&self, data: &Data, name: &str, first: usize) -> Result<Vec<Value>, InvalidArgument> {
        self.as_slice().encode_all(data, name, first)
    }
}

impl<A: Arguments + ?Sized> Arguments for &A {
    fn encode_all(&self, data: &Data, name: &str, first: usize) -> Result<Vec<Value>, InvalidArgument> {
        (**self).encode_all(data, name, first)
    }
}

macro_rules! arguments {
    ($(($($name:ident $index:tt),*)),*) => {$(
        impl<$($name: Encode),*> Arguments for ($($name,)*) {
            #[allow(unused_variables)]
            fn encode_all(&self, data: &Data, name: &str, first: usize) -> Result<Vec<Value>, InvalidArgument> {
                Ok(vec![$(argument(data, name, first + $index, &self.$index)?),*])
            }
        }
    )*};
}

arguments!((), (A 0), (A 0, B 1), (A 0, B 1, C 2), (A 0, B 1, C 2, D 3), (A 0, B 1, C 2, D 3, E 4), (A 0, B 1, C 2, D 3, E 4, F 5));

/// A bound checked function. Clones share their handlers.
#[derive(Clone)]
pub struct Function {
    name: String,
    signature: Interface,
    target: Hash,
    registry: OperationRegistry,
    host: Host,
    data: Arc<Data>,
}

impl fmt::Debug for Function {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Function").field("name", &self.name).field("target", &self.target).finish_non_exhaustive()
    }
}

impl Function {
    /// The public name.
    pub fn name(&self) -> &str {
        &self.name
    }

    /// The checked signature.
    pub fn signature(&self) -> &Interface {
        &self.signature
    }

    /// Validates encoded arguments against the signature and the frame
    /// limits, before any worker starts.
    fn check(&self, values: Vec<Value>) -> Result<Vec<Value>, Error> {
        let expected = self.signature.parameters.len();
        if values.len() != expected {
            return Err(
                InvalidArgument { message: format!("{}: expected {expected} arguments, received {}", self.name, values.len()) }.into()
            );
        }
        for (index, (value, parameter)) in values.iter().zip(&self.signature.parameters).enumerate() {
            self.data.conform(value, parameter, &format!("{}.argument[{index}]", self.name))?;
        }
        let limits = &self.host.limits;
        invoke_frame(&self.target, &self.signature, &values, &self.registry.entries(), limits)
            .and_then(|frame| frame_bytes(&frame, limits.max_frame_bytes, limits.max_json_depth))
            .map_err(|error| InvalidArgument {
                message: format!("{}: {error}; reduce input size or use smaller independent batches", self.name),
            })?;
        Ok(values)
    }

    fn arguments(&self, args: &(impl Arguments + ?Sized)) -> Result<Vec<Value>, Error> {
        self.check(args.encode_all(&self.data, &self.name, 0)?)
    }

    fn run(&self, values: Vec<Value>, cancel: &Cancel) -> Invocation {
        Invocation { raw: self.host.invoke(&self.target, &self.signature, &values, &self.registry, cancel), function: self.clone() }
    }

    /// Validates the arguments locally, then runs one worker. The error is
    /// only a local refusal; Core and carrier failures are in the
    /// [`Invocation`]. Nothing is retried.
    pub fn invoke(&self, args: impl Arguments) -> Result<Invocation, Error> {
        self.invoke_with(args, &Cancel::new())
    }

    /// [`Function::invoke`]; cancelling `cancel` kills the worker.
    pub fn invoke_with(&self, args: impl Arguments, cancel: &Cancel) -> Result<Invocation, Error> {
        Ok(self.run(self.arguments(&args)?, cancel))
    }

    /// Invokes the function and decodes its result.
    pub fn call<R: Decode>(&self, args: impl Arguments) -> Result<R, Error> {
        self.invoke(args)?.decode()
    }

    /// [`Function::call`]; cancelling `cancel` kills the worker.
    pub fn call_with<R: Decode>(&self, args: impl Arguments, cancel: &Cancel) -> Result<R, Error> {
        self.invoke_with(args, cancel)?.decode()
    }

    /// Runs a pure function whose final parameter and result are Lists over
    /// explicit chunks of `rows`, with `leading` as the other arguments.
    /// Every chunk is validated before the first runs. Nothing is retried.
    pub fn map_batches<R: Decode, T: Encode>(
        &self,
        leading: impl Arguments,
        rows: &[T],
        batch_size: usize,
    ) -> Result<Vec<R>, BatchError<R>> {
        self.map_batches_with(leading, rows, batch_size, &Cancel::new())
    }

    /// [`Function::map_batches`]; cancelling `cancel` kills the running
    /// worker.
    pub fn map_batches_with<R: Decode, T: Encode>(
        &self,
        leading: impl Arguments,
        rows: &[T],
        batch_size: usize,
        cancel: &Cancel,
    ) -> Result<Vec<R>, BatchError<R>> {
        let refuse = |error: Error| BatchError { index: None, completed: Vec::new(), error };
        if batch_size < 1 {
            return Err(refuse(InvalidArgument { message: "batch size must be positive".into() }.into()));
        }
        let list = self.data.name("list");
        let is_list = |t: &BoundaryType| matches!(t, BoundaryType::Nominal(identity, _) if Some(identity) == list);
        if !self.signature.effects.is_empty() || !self.signature.parameters.last().is_some_and(is_list) || !is_list(&self.signature.result)
        {
            return Err(refuse(Error::binding("map_batches needs a pure function with a final List parameter and a List result")));
        }
        let leading = leading.encode_all(&self.data, &self.name, 0).map_err(|error| refuse(error.into()))?;
        let last = leading.len();
        let batch = |chunk: &[T]| -> Result<Vec<Value>, Error> {
            let mut values = leading.clone();
            values.push(argument(&self.data, &self.name, last, chunk)?);
            self.check(values)
        };
        batch(&[]).map_err(refuse)?;
        let batches = rows.chunks(batch_size).map(batch).collect::<Result<Vec<_>, _>>().map_err(refuse)?;
        let mut completed = Vec::new();
        for (index, values) in batches.into_iter().enumerate() {
            match self.run(values, cancel).decode::<Vec<R>>() {
                Ok(values) => completed.extend(values),
                Err(error) => return Err(BatchError { index: Some(index), completed, error }),
            }
        }
        Ok(completed)
    }
}

/// One completed invocation with its raw evidence.
#[derive(Clone)]
pub struct Invocation {
    /// The host's result: Core's frames, the host's frames, and evidence.
    pub raw: InvocationResult,
    function: Function,
}

impl fmt::Debug for Invocation {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Invocation").field("function", &self.function.name).field("raw", &self.raw).finish()
    }
}

impl Invocation {
    /// Whether Core reported success.
    pub fn ok(&self) -> bool {
        self.raw.ok()
    }

    /// The primary diagnostic or host code.
    pub fn code(&self) -> String {
        self.raw.code()
    }

    /// A summary for people.
    pub fn message(&self) -> String {
        self.raw.message()
    }

    /// The classified failure, or `None` on success.
    pub fn error(&self) -> Option<InvocationError> {
        (!self.ok()).then(|| InvocationError(Box::new(self.raw.clone())))
    }

    /// Decodes the result. A mapping failure after a successful Core result
    /// is an [`Error::Binding`] retaining the raw result.
    pub fn decode<R: Decode>(&self) -> Result<R, Error> {
        if let Some(error) = self.error() {
            return Err(error.into());
        }
        let Some(value) = self.raw.value.as_ref() else {
            return Err(Error::Binding {
                message: format!("{}: Core completed without a value", self.function.name),
                result: Some(Box::new(self.raw.clone())),
            });
        };
        self.function.data.decode(value).map_err(|error| Error::Binding {
            message: format!("{}: Core completed, but result mapping failed: {error}", self.function.name),
            result: Some(Box::new(self.raw.clone())),
        })
    }

    /// Decodes the result into native values.
    pub fn value(&self) -> Result<Dynamic, Error> {
        self.decode()
    }

    /// Lists the outcome and each effect request by operation name.
    pub fn render(&self) -> String {
        let status = if self.ok() { "ok".to_owned() } else { self.message() };
        let mut lines = vec![format!("{}: {status}", self.function.name)];
        for request in &self.raw.requests {
            let label = self.function.data.label(&request.operation).map_or_else(|| request.operation.to_string(), str::to_owned);
            lines.push(format!("  {}. {label}", request.ordinal));
        }
        lines.join("\n")
    }

    /// Writes the invocation's frames and host evidence. The record contains
    /// argument and result values; it is written only when called. A new
    /// file is created owner-only on Unix; an existing file keeps its
    /// permissions.
    pub fn save(&self, path: impl AsRef<Path>) -> std::io::Result<()> {
        let raw = &self.raw;
        let frames = |list: &[Object]| Json::Array(list.iter().cloned().map(Json::Object).collect());
        let host = Object::from([
            ("evidence".into(), Json::Object(raw.host_evidence.clone())),
            ("exit_status".into(), raw.exit_status.map_or(Json::Null, |status| Json::Int(i64::from(status)))),
            ("signal".into(), raw.signal.clone().map_or(Json::Null, Json::Str)),
            ("stderr_base64".into(), Json::Str(core::base64(&raw.stderr))),
        ]);
        let record = Object::from([
            ("core_frames".into(), frames(&raw.core_frames)),
            ("function".into(), Json::Str(self.function.name.clone())),
            ("host".into(), Json::Object(host)),
            ("host_frames".into(), frames(&raw.host_frames)),
            ("schema".into(), Json::Str("jacquard-host-inspection-v1".into())),
        ]);
        let text = stringify(&Json::Object(record), 128).map_err(std::io::Error::other)?;
        let mut options = std::fs::OpenOptions::new();
        options.write(true).create(true).truncate(true);
        #[cfg(unix)]
        std::os::unix::fs::OpenOptionsExt::mode(&mut options, 0o600);
        std::io::Write::write_all(&mut options.open(path)?, format!("{text}\n").as_bytes())
    }
}
