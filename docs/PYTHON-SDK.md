# Python SDK

The `Runtime` API adds preparation, application data conversion, complete
effect-service binding, and consistent failure handling to the pinned v0
adapter. The low-level `Host`, `OperationRegistry`, and protocol values remain
available. Core is installed separately; its worker and prelude must match
[the compatibility pin](COMPATIBILITY.md).

## Prepare once, call by name

```python
from jacquard_host import Runtime

runtime = Runtime()  # JACQUARD_BIN and JACQUARD_PRELUDE, or binary=/prelude=
program = runtime.prepare("rules.jac")
decision = program.function("decide")
answer = decision(42)
```

`prepare` accepts a path or an ordered sequence of source paths, concatenated
in dependency order. It checks the source, resolves its public identities,
populates a private Core store, and publishes a complete cache entry. Default
cache location: `.scratch/jacquard-host`; pass `cache_dir=` to choose another.

The cache key includes source bytes, Core executable bytes, all prelude files,
the protocol, and the preparation-format version. Cached source/store payloads
are digest-checked against the prepared manifest on reuse. Changed source/runtime/prelude produces a different
entry; a missing or modified artifact is refused with `PreparationError`.
Interrupted builds do not publish partial entries. Caches are ordinary trusted
host files, not authenticated artifacts or a hostile-code boundary.

Preparation runs the public Core check/hash/run commands on first-party source.
Use declaration-only application files. It does not grant a top-level program
outside effects or silently execute Python handlers. Reuse one `Runtime` when
preparing several programs to share prelude identity discovery.

Each function invocation still starts and reaps a fresh worker. Warm preparation
avoids repeated checking; it does not introduce a persistent worker or pool.

## Python values

Primitive arguments are checked against the checked entry's interface before a
worker starts. Field annotations are checked against explicit Python mappings;
Core remains the authority for constructor types and positional field layouts.

| Python | Jacquard boundary |
|---|---|
| `int` | 63-bit `Int`; `bool` is never accepted as an integer |
| `float` | `Real`, preserving NaN, infinities, and signed-zero bits |
| `str` | Unicode-scalar `Text`, checked against the selected UTF-8 byte ceiling |
| `bool` | Prelude `True` / `False` constructors |
| `list[T]` | Prelude `Cons` / `Nil` |
| `None` | Prelude `None` |
| `Some(value)` | An explicit `Some`, including `Some(None)` for nested options |
| `tuple` | Positional tuple; unit is `()` |
| `Hash` from `jacquard_host` | Exact hash value |
| Registered dataclass or enum | A constructor selected by its exact prepared identity |

At a checked `Option T` parameter/result, ordinary `T | None` is convenient.
Nested options retain `Some` so `None`, `Some(None)`, and `Some(value)` remain
distinct. Explicit dataclass optional annotations and handler annotations also
select this conversion. Without type context, decoding `Some` returns the
explicit wrapper.

For a quick application, use explicit named factories:

```python
policy = program.data.TriagePolicy(4, 60)
incident = program.data.Incident("inc-1", "api", 5, 90, True, None)
report = program.function("triage.batch")(policy, [incident])
# [routed(...)] — named variants with decoded native fields
```

For application code, register explicit dataclasses and enums:

```python
from dataclasses import dataclass
from enum import Enum

@program.data.record
@dataclass(frozen=True)
class Incident:
    id: str
    service: str
    severity: int
    age: int
    visible: bool
    owner: str | None

@program.data.enum
class Urgency(Enum):
    PAGE = "PageNow"
    TODAY = "WorkToday"
    WATCH = "WatchOnly"

@program.data.record
@dataclass(frozen=True)
class Routed:
    id: str
    team: str
    urgency: Urgency
    reasons: list[str]
```

Fields must follow the constructor's Jacquard positional order. Python names
are application-owned; they are not inferred Core labels. A differently named
Python class can use `@program.data.record(constructor="Incident")`. Enums use
constructor names as string values. Mapping an unsupported value is a local
`InvalidArgument`, usually naming the offending field, for example
`fulfillment.place.argument[0].lines[0].units: expected int, received bool`.

Identity discovery covers the entry source and the pinned primitive/data/hash
prelude declarations (`01-prim.jqd`, `02-data.jqd`, `19-audit.jqd`). Other prelude
nominal types currently need explicit identities through the low-level API.
The SDK refuses unresolved interfaces rather than guessing their identities.

The current Core export lacks enough structured information to generate or
prove these layouts. This explicit mapping API does not complete the separate
generated-binding milestone. If Core completed but a Python result mapping
fails, `BindingError.result` retains that successful raw result; the SDK does
not repeat the call.

## Effect services

```python
class Warehouse:
    def booking_status(self, purchase): ...
    def lookup_stock(self, sku): ...
    def reserve_order(self, purchase): ...

class Dispatch:
    def publish_booking(self, order_id, receipt): ...

place = program.function("fulfillment.place", effects={
    "Inventory": Warehouse(),
    "Dispatch": Dispatch(),
})
receipt = place(purchase)
```

Kebab-case operation names select snake_case methods. A mapping from operation
names to callables works too. Handler parameters/results use the same native
data conversion; optional and scalar annotations are honored. Deliberate
outside failures still use `HostFailure`; unexpected exceptions are unknown
completion and are never retried. A handler result with no valid Python/wire
mapping follows the adapter's existing `outside_failure`/`failed` convention;
that classification does not promise earlier external work was rolled back.

All operations in every declared root effect must be present, including
operations a particular path may not reach. This is a conservative completeness
check using checked effect-row identities and the declaration groups published
by `jacquard hash`. It is not a new reachability analysis. Missing or async
handlers are refused while binding, before an invocation or outside action.
Effects whose metadata is unavailable in the prepared source require the
low-level API until the structured interface exporter is available.

A type and its same-named constructor are supported. Other shared public
spellings across distinct declaration groups are refused when the pinned CLI
cannot distinguish their kinds; use distinct names or explicit low-level
identities. The SDK does not guess which identity a checked type/effect name
intends. The pinned printer's ambiguous singleton tuples also still require an
explicit low-level interface.

Use `program.provide(Inventory=warehouse, Dispatch=dispatch)` for shared
services. Each `program.function(name)` includes only services in that term's
checked effect row. Function-local `effects=` overrides are explicit; unrelated
explicit entries are rejected. Bound methods are captured at function creation.

Complete bindings do not make several outside actions a transaction. A handler
can fail after an earlier handler committed. The inventory example records a
booking and outbox entry in one named transaction, then uses a receipt-deduped
delivery simulation with explicit reconciliation.

## Failure handling and inspection

```python
from jacquard_host import JacquardError

try:
    run = place.invoke(purchase)
    print(run.unwrap())
except JacquardError as error:
    print(error)
    # InvocationError subclasses retain error.result and error.code.
```

`InvalidArgument`, `BindingError`, and `PreparationError` name local setup or
conversion failures. `InvocationError` retains a terminal or host failure;
`OperationFailed`, `DeadlineExceeded`, and `WorkerLost` distinguish common
cases. Their `origin` says whether the code came from Core diagnostics or host
evidence. Spawn failure is returned by `.invoke()` as host-owned evidence and
raised by `.unwrap()`/normal calls as `WorkerLost`.

`run.ok`, `run.code`, `run.message`, and `run.value` provide one result shape.
`run.raw` retains `InvocationResult`, all Core/host frames, diagnostics, evidence,
stderr, and exit status. Low-level results also gain `.code`, `.message`, and
`.unwrap()`; existing `primary_code` continues to mean a Core diagnostic only.

`run.render()` shows the function, outcome, and operation order without argument
values. `run.save(path)` explicitly saves raw frames, including argument/result
values, plus separately labeled host evidence and base64 operator bytes. No
trace fields are added to Core's protocol.

## Larger pure workloads

```python
reports = program.function("triage.batch").map_batches(policy, incidents)
```

This explicitly chunks the final List parameter, calls the pure List-returning
function, and concatenates results in order. It is for independent per-item
work: chunking a global sort, ranking, or optimizer can change meaning. The
SDK does not claim to prove batch equivalence. Default chunk size is eight;
choose another with `batch_size=` when its input and output shapes fit.

Every input chunk is validated before the first call. Output depth cannot in
general be predicted; an oversized output stops at that batch with the exact
Core failure. The exception exposes `batch_index` and `completed_values`.
There is no automatic retry with smaller batches. Effectful entries are refused
by this helper; their orchestration must be explicit.

The default v0 nesting ceiling remains 64. The experiment's `List Int` fits 30
items, while its rich triage output fits 28. Input byte/depth/node ceilings are
checked locally before spawning; raising frozen ceilings or adding a flat bulk
value requires a Core contract change.

## Runtime controls

```python
runtime = Runtime(binary="/path/to/jacquard", prelude="/path/to/prelude",
                  deadline_seconds=10, limits={"max_text_bytes": 8192})
```

The SDK accepts partial limit overrides and validates the complete v0 limits.
The low-level adapter still accepts an exact limits object. The deadline is one
carrier lifetime budget; synchronous Python callbacks cannot be interrupted by
it. No hostile-code isolation, public deployment, or concurrent throughput is
claimed. See [LIMITS.md](LIMITS.md).
