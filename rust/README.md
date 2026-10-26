# Jacquard Host for Rust

Call checked Jacquard functions from Rust, bind Rust closures to their
declared operations, and inspect the exact protocol evidence. This is an
independent Rust implementation of the pinned `jacquard-host-v0` boundary. It
uses only the standard library, and runs Core directly without Python,
Node.js, or Go. Rust 1.88+ is required.

The crate has two layers:
- **The SDK** (`jacquard_host::sdk`) prepares and caches checked programs,
  maps native Rust values, and binds closures to exact operation identities.
  It is comparable to the Python, TypeScript, and Go SDKs.
- **The protocol-level host** (`jacquard_host::host`) takes exact identities,
  boundary types, and protocol values for a store you prepare with the Core
  CLI. It is comparable to the Go `host` package.

Linux is exercised. macOS should work but is not exercised in CI, and Windows
is not claimed. **Experimental:** the protocol is Core's pinned
`jacquard-host-v0`, and Core is installed separately. Each invocation runs one
fresh, serial worker. The host and operating system are trusted. See
[limits](../docs/LIMITS.md).

## Install

The crate is not published to crates.io. Depend on it from a clone or by Git:

```toml
[dependencies]
jacquard-host = { git = "https://github.com/jbwinters/jacquard-host" }
```

From a clone, install the pinned Core release (see [setup](../docs/SETUP.md)),
then run the quickstart:

```sh
eval "$(python3 scripts/core_pin.py install)"
cd rust
cargo run --example jacquard-examples -- quickstart
```

Expected output:

```text
Order total 35: shipping 5
Order total 75: shipping 0
```

The same command runs the other example projects; see
[run the projects](#run-the-projects).

## Call a checked function

```rust,no_run
use jacquard_host::sdk::{PrepareOptions, Runtime, RuntimeOptions};

fn shipping() -> Result<(), jacquard_host::sdk::Error> {
    let runtime = Runtime::new(RuntimeOptions::default())?;
    let program = runtime.prepare(&["rules.jac"], &PrepareOptions::default())?;
    let shipping = program.function("shipping-cost")?;
    let cost: i64 = shipping.call((35,))?;

    let invocation = shipping.invoke((75,))?;
    println!("{}", invocation.render());
    invocation.save(".scratch/shipping.json").expect("saved");
    Ok(())
}
```

The shipping rule is the [quickstart source](../examples/quickstart/rules.jac).
`prepare` takes `.jac` paths (or `.jqd` paths) in dependency order. It uses
Core's public `check`, `hash`, and `run` commands, and keeps a verified cache
keyed by:
- the source bytes;
- the Core executable bytes;
- every prelude file;
- the protocol and preparation format.

The cache defaults to `.scratch/jacquard-host-rust` under the working
directory; `PrepareOptions::cache_dir` changes it. A changed or incomplete
cache entry is refused rather than repaired. Clones of one `Runtime` share its
version check and prelude discovery, including across threads. Each call runs
one fresh worker. Prepare only first-party declaration sources: preparation
runs Core's `run` command, without service bindings, to populate the store.

`RuntimeOptions` defaults to `$JACQUARD_BIN` and `$JACQUARD_PRELUDE`. The
runtime refuses a Core whose `--version` differs from the pinned release. Each
Core command must finish within 30 seconds. It runs in its own process group,
which is stopped when the command times out, and on Linux also when it exits.
If its output stays open for a second after it exits (a descendant that left
the group holds it), preparation fails, and the host stops reading and closes
the pipes; the descendant keeps running.

Arguments are a tuple, such as `(35,)` or `(&policy, &rows)`, or a slice of
`&dyn Encode`. `call` decodes the result into the type you ask for; `invoke`
keeps the whole `Invocation`. The `_with` variants take a `Cancel`.
`Invocation::save` writes the frames and evidence, including argument and
result values; a new file is created owner-only on Unix, and an existing
file keeps its permissions.

## Data

Values map by their Rust type. Before any worker starts, the encoded
arguments are checked against the function's checked signature; Core always
checks the actual wire types too.

| Rust | Jacquard |
|---|---|
| `i8` to `i64`, `u8` to `u64`, `isize`, `usize` | `Int`; `i64` covers Core's full 63-bit range; out-of-range values are refused |
| `f64` (`f32` to encode) | `Real` as binary64 bits, including NaN payloads and signed zero; an integer at a `Real` parameter is refused |
| `String` (`&str` to encode), `bool`, `protocol::Hash` | `Text`, `Bool`, `Hash` |
| `Vec<T>` (`&[T]` to encode) | `List T` |
| `Option<T>` | `Option T`; `Option<Option<T>>` keeps nested options distinct |
| `()` and tuples up to six items | unit and tuples |
| a struct with `record!` | one constructor, its listed fields in order |
| an enum with `variants!` | the constructors of one data type: unit variants are nullary constructors; tuple and struct variants list their fields in order |
| `sdk::Dynamic` | any of the above by public name, for code with no application type |
| `protocol::Value` | passed through unchanged |

```rust
use jacquard_host::{record, variants};

struct Incident {
    id: String,
    service: String,
    severity: i64,
    age: i64,
    visible: bool,
    owner: Option<String>,
}
record!(Incident = "Incident" { id, service, severity, age, visible, owner });

enum Urgency {
    Page,
    Today,
    Watch,
}
variants!(Urgency { Page = "PageNow", Today = "WorkToday", Watch = "WatchOnly" });

enum Triage {
    Routed { id: String, team: String, urgency: Urgency, reasons: Vec<String> },
    InvalidIncident(String),
}
variants!(Triage {
    Routed { id, team, urgency, reasons } = "Routed",
    InvalidIncident(id) = "InvalidIncident",
});
```

The listed fields must follow the Jacquard constructor's positional order;
the pinned CLI exports no field types, so the order is not checked against
Core. Constructor names may use display spelling (`WorkToday`) or store
spelling (`work-today`). A constructor that is not in the prepared program is
refused when a value is encoded, and a result built by a different
constructor fails to decode. The macros expand to ordinary `Encode` and
`Decode` implementations, which you may also write by hand with `Encoder` and
`Decoder`.

Text, collection sizes, and nesting depth are bounded by the selected limits
before a worker starts. `Program::data()` gives each public name's exact
identity. `Data::make` builds a `Dynamic::Variant` after checking that its
constructor exists, and `Invocation::value` decodes any result into `Dynamic`.

## Services and failures

```rust
use jacquard_host::host::{HandlerContext, HandlerError};
use jacquard_host::sdk::{Error, Operations, Program};

fn summarize(program: &Program) -> Result<(), Error> {
    let documents = Operations::new().on("read-document", |context: &HandlerContext, key: String| {
        if context.is_cancelled() {
            return Err(HandlerError::completion_unknown("cancelled"));
        }
        match key.as_str() {
            "example" => Ok("A synthetic document supplied by Rust.".to_owned()),
            _ => Err(HandlerError::refused("unknown document")),
        }
    });
    let summarize = program.function_with("summarize", [("Documents", documents)])?;
    Ok(())
}
```

An `Operations` value maps operation names (store spelling such as
`read-document`, or Rust spelling such as `read_document`) to closures. One
`Operations` may serve several effects.

- **Parameters:** a closure may take a leading `&HandlerContext`. The
  remaining parameters decode from the request into their Rust types.
- **Results:** a closure returns `Result<T, HandlerError>`, where `T` encodes
  the result (`()` for unit).
- **Failures:** return `HandlerError::unsupported`, `refused`,
  `outside_failed`, or `completion_unknown` to report that failure. Any other
  error raised with `?`, or a panic, is reported to Core as completion unknown
  without its message.
- **Binding:** every operation in each checked root effect needs a handler
  before the first invocation, including operations a given path never
  reaches. A name that is no operation of the program is refused.
  `Program::provide` shares services across functions; each function receives
  only its own checked effects.

Closures run on their own threads, so they are `Send + Sync + 'static`; share
state through `Arc` and a lock, as the fulfillment example's warehouse does.
`RuntimeOptions::deadline` (default 30 s) bounds each worker lifetime; see
[handlers and cancellation](#handlers-and-cancellation).

## Errors

All SDK failures are an `sdk::Error`:
- `InvalidArgument` is a local refusal, such as a value of the wrong type or
  one too large or too deep for a frame. No worker starts. `invoke` returns
  only this error; otherwise the `Invocation` holds the raw result.
- `Invocation` wraps an `InvocationError` from `call`, `Invocation::decode`,
  and `map_batches`. `Error::failure` classifies it as `DeadlineExceeded`,
  `WorkerLost`, `OperationFailed`, or `Other`.
- `Binding` reports an unknown name, a missing handler, or a result that does
  not decode into the requested type. In the decode case, `result` keeps the
  successful Core result.
- `Preparation` covers checking and the cache.

`map_batches` runs a pure function whose final parameter and result are Lists
over explicit chunks. It validates every batch before the first runs and
retries nothing. Its `BatchError` names the failed batch and keeps the decoded
values of the batches that completed.

A request argument that does not decode into a closure's parameter type, or a
wrong argument count, is reported as an outside failure, and that closure does
not run. A result that cannot be encoded is also reported as an outside
failure, but only after the closure returned, so any action it took has
happened. Neither says anything about earlier operations in the invocation.

## Run the projects

```sh
cd rust
cargo run --example jacquard-examples -- planner
cargo run --example jacquard-examples -- triage
cargo run --example jacquard-examples -- fulfillment
cargo run --example jacquard-examples -- fulfillment
```

- Planner returns score 16.
- Triage returns PAGE, TODAY, and WATCH.
- Fulfillment returns Fulfilled, then DuplicateOrder with the same receipt.

The command reads the rules and synthetic inputs from the repository's
[`examples/`](../examples/README.md), so it runs from a clone. Custom input
JSON may follow the project name. `--inspect PATH` saves frames; triage adds a
numbered suffix per batch. State defaults to `.scratch/rust-examples/`.

Try explicit recovery in a fresh work directory:

```sh
cargo run --example jacquard-examples -- --workdir .scratch/rust-recovery --fault publish-failed fulfillment
cargo run --example jacquard-examples -- --workdir .scratch/rust-recovery --deliver-pending fulfillment
cargo run --example jacquard-examples -- --workdir .scratch/rust-recovery --deliver-pending fulfillment
```

The first command exits 1 with E1613 and one pending booking. Reconciliation
then delivers one receipt, and the next run delivers zero. The `after-commit`
and `after-delivery` faults exercise E1612 around the snapshot commits.

The example's `Warehouse` is a single-process service. It writes bookings and
outbox entries together as one atomic JSON snapshot, and a simulated delivery
sink deduplicates receipts. It has no cross-process locking and makes no
power-loss durability claim.

## Modules

| Module | Contents |
|---|---|
| `sdk` | The SDK: `Runtime`, `Program`, `Data`, `Function`, `Invocation`, `Operations`, `Dynamic`, and the `Encode` and `Decode` traits, with the `record!` and `variants!` macros at the crate root |
| `host` | Low-level `Host`, `OperationRegistry`, `HandlerError`, and `InvocationResult` for exact identities |
| `carrier` | The bounded process carrier for `jacquard host worker`, and `Cancel` |
| `protocol`, `json` | Strict JSON, v0 values and types, limits, the `Session` state machine |
| `compat`, `kit` | The generated pin constants, and the embedded pinned conformance kit and its replayer |

## The protocol-level host

The example below invokes a checked function whose single operation is served
by a Rust handler:

```rust
use std::time::Duration;

use jacquard_host::carrier::{Cancel, Options};
use jacquard_host::host::{HandlerError, Host, HostOptions, OperationRegistry};
use jacquard_host::protocol::{BoundaryType, Hash, Interface, Value};

fn echo(store: &str, target: &Hash, text: &Hash, world: &Hash, send: &Hash) -> Result<Value, Box<dyn std::error::Error>> {
    let host = Host::new(store, HostOptions {
        // The command defaults to $JACQUARD_BIN, then `jacquard`.
        carrier: Options { deadline: Some(Duration::from_secs(5)), ..Options::default() },
        limits: None,
    })?;
    let mut registry = OperationRegistry::new();
    registry.bind(world.clone(), send.clone(), |context, args| match args {
        _ if context.is_cancelled() => Err(HandlerError::completion_unknown("cancelled")),
        [Value::Text(text)] => Ok(Value::Text(format!("{text}!"))),
        _ => Err(HandlerError::outside_failed("expected one text argument")),
    })?;
    let text = BoundaryType::Nominal(text.clone(), vec![]);
    let spec = Interface { parameters: vec![text.clone()], result: text, effects: vec![world.clone()] };
    let result = host.invoke(target, &spec, &[Value::Text("ping".into())], &registry, &Cancel::new());
    Ok(result.into_value()?)
}
```

`invoke` returns an `InvocationResult` in every case. The result keeps Core's
frames, diagnostics, and evidence. It also keeps the host's frames, exit
status, bounded stderr, and host evidence (`jacquard-host-rust-evidence-v0`).
`into_value` returns the value, or an `InvocationError`. The error's
`failure()` is one of `DeadlineExceeded`, `WorkerLost`, `OperationFailed`, or
`Other`.

## Handlers and cancellation

These rules apply to SDK closures and to low-level handlers. A handler returns a value or a `HandlerError`. The four structured categories
are `unsupported`, `refused`, `outside_failed`, and `completion_unknown`.
`HandlerError::Unknown` and a panic are both reported to Core as completion
unknown, and their message is never sent. Panics are caught only with the
default `panic = "unwind"`; under `panic = "abort"` a panicking handler aborts
the host process. A value with no encoding within the
selected limits is reported as an outside failure.

Each handler runs on its own thread, bounded by the worker's lifetime deadline.
At the deadline, or when the caller's `Cancel` is triggered, the host kills the
worker's process group and sends no late response. Cancellation is cooperative.
`HandlerContext::is_cancelled` turns true, but a handler that ignores it keeps
running on its thread until it returns. Nothing is retried.

## Conformance kit

`kit::load()` verifies the embedded kit against the pinned digests.
`kit::replay_kit` replays all 21 recordings, including the hostile host inputs,
against a worker. It reports any semantic differences and any drift in
non-normative diagnostic prose.

## Development

From this directory:

```sh
cargo fmt --check
cargo clippy --all-targets -- -D warnings
cargo test
# With the pinned Core configured (eval "$(python3 ../scripts/core_pin.py install)"):
cargo test -- --nocapture
```

The live tests in `tests/live.rs`, `tests/sdk_live.rs`, and
`tests/sdk_examples.rs` skip without `JACQUARD_BIN` and
`JACQUARD_PRELUDE` (set `JACQUARD_REQUIRE_LIVE` to make them fail instead).
The offline tests drive `jacquard-host-standin`, a Unix-only test fixture
binary that replays recordings or simulates faulty workers. It is not part of
the adapter's interface.

`src/compat.rs` is generated, and `kit/host-protocol-v0/` is a byte copy of the
pinned kit. `scripts/core_pin.py` maintains both; do not edit them by hand.
