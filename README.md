# Jacquard Host

Call checked Jacquard functions from Python, TypeScript, Go, Rust, Java, or .NET and implement
their declared host operations with ordinary services. Jacquard owns the decision
logic; the host owns persistence, I/O, and application lifetimes.

The SDK prepares and caches checked programs, converts native Python values,
binds services to exact operation identities, and retains the raw evidence for
every invocation. Python 3.11+, Node.js 22+, Go 1.24+, Rust 1.88+, Java 21+, or
.NET 8+, with no third-party runtime dependencies. The
[TypeScript SDK](typescript/README.md), the [Go SDK](go/README.md), the
[Rust SDK](rust/README.md), the [Java SDK](java/README.md), and the
[.NET SDK](dotnet/README.md) implement the pinned protocol independently. The
TypeScript SDK supports async service methods, the Go SDK maps native Go types
and context cancellation, the Rust SDK maps native Rust types through `record!`
and `variants!` declarations, the Java SDK maps records, enums, and sealed
interfaces through explicit codecs, and the .NET SDK maps records, enumerations,
and type hierarchies through explicit codecs without reflection over members,
with `CancellationToken` cancellation.

**Experimental:** the SDK targets Core's pinned `jacquard-host-v0` protocol.
Core is installed separately. Each invocation runs one fresh, serial worker;
the host and operating system are trusted. See [limits](docs/LIMITS.md).

## Get started

For Node.js, follow the [TypeScript walkthrough](typescript/README.md); for Go,
the [Go walkthrough](go/README.md); for Rust, the [Rust walkthrough](rust/README.md);
for Java, the [Java walkthrough](java/README.md); for .NET, the
[.NET walkthrough](dotnet/README.md). For Python:

From a clone of this repository:

```sh
python3 -m venv .venv
. .venv/bin/activate
python -m pip install .
```

Follow [setup](docs/SETUP.md) to build the matching Core worker and configure
`JACQUARD_BIN` and `JACQUARD_PRELUDE`, then run:

```sh
python examples/quickstart/run.py
```

The quickstart puts a shipping policy in Jacquard:

```jacquard
shipping-cost : (Int) ->{} Int
shipping-cost(total) = if lt(total, 50) then 5 else 0
```

Python prepares it and calls the checked function:

```python
from jacquard_host import Runtime

program = Runtime().prepare("examples/quickstart/rules.jac")
shipping_cost = program.function("shipping-cost")

print(shipping_cost(35))  # 5
print(shipping_cost(75))  # 0
```

Use `function.invoke(...)` for an invocation object with `.unwrap()`, a readable
`.render()`, raw protocol evidence in `.raw`, and explicit `.save(path)` inspection.
Native calls and `.unwrap()` raise `JacquardError` subclasses on failure,
retaining the result and code.

## Examples

The [runnable projects](examples/README.md) use synthetic inputs and require no
external services:

| Project | What it demonstrates |
|---|---|
| [Quickstart](examples/quickstart/README.md) | One checked policy, ordinary Python integers |
| [Triage](examples/triage/README.md) | Dataclasses, enums, optional fields, independent batching |
| [Planner](examples/planner/README.md) | A bounded optimal assignment under capacities and deadlines |
| [Fulfillment](examples/fulfillment/README.md) | Named services, SQLite transactions, idempotency, explicit outbox recovery |

Run all four against the installed SDK and real Core:

```sh
python scripts/check_examples.py
```

The [SDK guide](docs/PYTHON-SDK.md) covers data mappings, service binding,
inspection, caching, and failure handling. Missing handlers and invalid inputs
are rejected before a worker starts. Services are synchronous, and no outside
action is retried automatically. Data mappings are explicit; generated bindings
await Core's structured host interface exporter.

## Compatibility and scope

[Jacquard Core](https://github.com/jbwinters/jacquard-lang) owns language
semantics, checked effects, canonical identities, grants, and the host contract.
This repository consumes that contract and owns the Python adapter, resource
lifetimes, host services, and integration evidence.

The [exact compatibility pin](docs/COMPATIBILITY.md) includes the experimental
v0 carrier, vendored fixtures, evidence pack, and pending Core decisions.
Adapters pin one Core release in [`core-pin.json`](core-pin.json); [setup](docs/SETUP.md)
installs that release and verifies its checksum. Version strings alone do not
establish compatibility.

The current boundary is for bounded first-party programs on a trusted host.
Nested lists reach the frame-depth limit quickly; independent batching helps
only when the application's semantics allow it. A synchronous Python callback
can outlast the carrier deadline. Read [limits](docs/LIMITS.md) and
[authority](docs/AUTHORITY.md) before integrating outside actions.

The [low-level adapter guide](docs/ADAPTER.md) documents exact protocol values,
the named study persistence operation, and the serial loopback socket/HTTP
integration. The study slice uses synthetic data. Public deployment and real
participant collection require separate application work.

## Development

See [CONTRIBUTING.md](CONTRIBUTING.md) for offline tests, installed-distribution
checks, and live runs. CI tests Python 3.11–3.13, Node.js 22/24, Go 1.24 and
stable, Rust 1.88 and stable, Java 21 and 25, and .NET 8 and 10. It
separately installs the pinned Core release for every adapter's live suite and
the public examples.

[Evidence](docs/EVIDENCE.md) records the checks and platform claims;
[development experiments](docs/development/README.md) preserve the usability
findings and local measurements behind the SDK. The
[roadmap](docs/ROADMAP.md), [Core requirements](docs/CORE-REQUIREMENTS.md), and
[changelog](CHANGELOG.md) describe remaining work and changes.

Licensed under [Apache 2.0](LICENSE). The vendored Core conformance artifacts
retain their upstream attribution in [NOTICE](NOTICE).
