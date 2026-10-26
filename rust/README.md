# Jacquard Host for Rust

An independent Rust adapter for Jacquard Core's experimental `jacquard-host-v0`
protocol. It implements the pinned protocol on its own, without calling Python,
Node.js, or Go, and uses only the standard library. Rust 1.88+ is required.

This is the protocol-level adapter, comparable to the Go `host` package. It
includes strict codecs, the host session state machine, and a bounded process
carrier for `jacquard host worker`. It also provides a `Host` with a closed
operation registry and a replayer for Core's pinned conformance kit. There is
no SDK layer yet. It does not compile programs, map native types, or cache
stores. You prepare a store with the Core CLI and pass exact identities and
boundary types.

**Experimental:** the protocol is Core's pinned `jacquard-host-v0`, and Core is
installed separately. Each invocation runs one fresh, serial worker. The host
and operating system are trusted. See [limits](../docs/LIMITS.md).

## Use

The crate is not published to crates.io. Depend on it from a clone or by Git:

```toml
[dependencies]
jacquard-host = { git = "https://github.com/jbwinters/jacquard-host" }
```

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

A handler returns a value or a `HandlerError`. The four structured categories
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

The live tests in `tests/live.rs` skip without `JACQUARD_BIN` and
`JACQUARD_PRELUDE` (set `JACQUARD_REQUIRE_LIVE` to make them fail instead).
The offline tests drive `jacquard-host-standin`, a Unix-only test fixture
binary that replays recordings or simulates faulty workers. It is not part of
the adapter's interface.

`src/compat.rs` is generated, and `kit/host-protocol-v0/` is a byte copy of the
pinned kit. `scripts/core_pin.py` maintains both; do not edit them by hand.
