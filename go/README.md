# Jacquard Host for Go

Call checked Jacquard functions from Go, bind Go services to their declared
operations, and inspect the exact protocol evidence. This is an independent Go
implementation of the pinned `jacquard-host-v0` boundary. It uses only the
standard library, and runs Core directly without Python or Node.js.

Go 1.24+. Linux is exercised. macOS should work but is not exercised in CI, and
Windows is not claimed. The SDK and process carrier are experimental. Core is a
separate executable, and the host and operating system are trusted.

## Install

From a clone, install the pinned Core release (see [setup](../docs/SETUP.md)),
then run the quickstart:

```sh
eval "$(python3 scripts/core_pin.py install)"
cd go
go run ./cmd/jacquard-examples quickstart
```

Expected output:

```text
Order total 35: shipping 5
Order total 75: shipping 0
```

In another module, `go get github.com/jbwinters/jacquard-host/go@latest`.

## Call a checked function

```go
runtime, err := jacquard.NewRuntime(jacquard.RuntimeOptions{})
if err != nil {
	log.Fatal(err)
}
program, err := runtime.Prepare(ctx, []string{"rules.jac"}, jacquard.PrepareOptions{})
if err != nil {
	log.Fatal(err)
}
shipping, err := program.Function("shipping-cost", jacquard.FunctionOptions{})
if err != nil {
	log.Fatal(err)
}
cost, err := jacquard.Call[int](ctx, shipping, 35)

invocation, err := shipping.Invoke(ctx, 75)
fmt.Println(invocation.Render())
err = invocation.Save(".scratch/shipping.json")
```

The shipping rule is the [quickstart source](../examples/quickstart/rules.jac).
`Prepare` takes `.jac` paths (or `.jqd` paths) in dependency order. It uses
Core's public `check`, `hash`, and `run` commands, and keeps a verified cache
keyed by:
- the source bytes;
- the Core executable bytes;
- every prelude file;
- the protocol and preparation format.

The cache defaults to `.scratch/jacquard-host-go` under the working directory;
`PrepareOptions.CacheDir` changes it. A changed or incomplete cache entry is
refused rather than repaired. Reuse one `Runtime` to share prelude discovery.
Each call runs one fresh worker. Prepare only first-party declaration sources:
preparation runs Core's `run` command, without service bindings, to populate
the store.

`RuntimeOptions` defaults to `$JACQUARD_BIN` and `$JACQUARD_PRELUDE`. The
runtime refuses a Core whose `--version` differs from the pinned release.

## Data

Arguments are converted from native Go values, guided by the checked
signature, and results decode into the type you ask for. Core always checks
the actual wire types.

| Go | Jacquard |
|---|---|
| `int`, `int64`, and the other integer kinds | `Int`; Go's `int64` covers Core's full 63-bit range; out-of-range values are refused |
| `float64` | `Real` as binary64 bits, including NaN payloads and signed zero; integers are accepted at a `Real` parameter only when exactly representable |
| `string`, `bool`, `protocol.Hash` | `Text` (valid UTF-8), `Bool`, `Hash` |
| `[]T` | `List T` |
| `*T` (nil is `None`) | `Option T`; `**T` keeps nested options distinct. Where the checked type is a non-optional data type, a non-nil pointer is dereferenced and nil is refused |
| `struct{}` | unit |
| `[N]T` | tuples; a `[]any` is also a tuple where the checked type is a tuple |
| registered named struct | a constructor (`Data.Record`), including nullary constructors from empty structs |
| registered string type | nullary constructors (`RegisterEnum`) |
| registered interface | a sum of registered records (`RegisterUnion`) |

```go
type Incident struct {
	ID, Service   string
	Severity, Age int
	Visible       bool
	Owner         *string
}
type Urgency string
type Triage interface{ triage() }
type Routed struct {
	ID, Team string
	Urgency  Urgency
	Reasons  []string
}

d := program.Data
d.Record("Incident", Incident{})
d.Record("Routed", Routed{})
d.Record("InvalidIncident", InvalidIncident{})
jacquard.RegisterEnum(d, map[Urgency]string{"PAGE": "PageNow", "TODAY": "WorkToday", "WATCH": "WatchOnly"})
jacquard.RegisterUnion[Triage](d, Routed{}, InvalidIncident{})
results, err := jacquard.MapBatches[Triage](ctx, route, 8, policy, incidents)
```

A record's exported fields, in declaration order, must follow the Jacquard
constructor's positional order. Register types before invoking functions;
registration is not synchronized with concurrent calls. `Data.Make("ConstructorName", fields...)`
builds a `jacquard.Variant` for dynamic code.

`Call[any]` or `Invocation.Value` returns native values:
- `int64`, `float64`, `string`, `bool`, and `protocol.Hash`;
- `[]any` for lists and non-empty tuples;
- `nil` for `None`, and `jacquard.Some` only where a nested option would
  otherwise be ambiguous;
- `jacquard.Unit` for unit;
- registered types;
- `jacquard.Variant` for other constructors.

Unknown or ambiguous public names are refused. Prelude discovery covers the
primitive types, Bool/List/Option, and Hash, plus the prepared definitions.

## Services and failures

```go
type Documents struct{}

func (Documents) ReadDocument(ctx context.Context, key string) (string, error) {
	if key != "example" {
		return "", host.Refused("unknown document")
	}
	return "A synthetic document supplied by Go.", nil
}

summarize, err := program.Function("summarize", jacquard.FunctionOptions{
	Effects: map[string]any{"Documents": Documents{}},
})
```

Services are ordinary values. An operation such as `read-document` is served
by the method `ReadDocument`. Alternatively, pass `jacquard.Operations`, a map
from operation names to functions.

- **Parameters:** a handler may take a leading `context.Context`. The remaining
  parameters decode from the request into their Go types.
- **Results:** a handler returns `T`, `(T, error)`, `error`, or nothing for a
  unit result.
- **Failures:** return `host.Unsupported`, `host.Refused`, `host.OutsideFailed`,
  or `host.CompletionUnknown` to report that failure. Any other error, or a
  panic, is reported to Core as completion unknown without its message.
- **Binding:** every operation in each checked root effect needs a handler
  before the first invocation, including operations a given path never
  reaches. `Program.Provide` shares services across functions; each function
  receives only its own checked effects.

`RuntimeOptions.Deadline` (default 30 s) bounds each worker lifetime:
- spawn, every pipe operation, each handler, and exit all count against it;
- cancelling the caller's context has the same effect.

When the deadline passes or the context is cancelled, the worker's process
group is killed and its pipes are closed. The handler's context is cancelled,
and no late response is sent. A handler that ignores its context keeps running
until it returns. Nothing is retried, and this does not prove that an outside
action stopped.

- **Classification.** A deadline set on the caller's own context is reported as
  an abort (`reason` `aborted`), like any other cancellation.
  `ErrDeadlineExceeded` means the worker's own lifetime deadline expired.
- **Descendants after exit.** On Unix the worker runs in its own process group,
  and its stderr is a pipe the host owns. If a descendant still holds that pipe
  one second after the worker exits, a successful exit is reported as a carrier
  loss. On Linux the host also stops the remaining group, while the exited
  worker is still unreaped so its group id cannot be reused. Other platforms
  only stop reading the pipe.
- **Escaped descendants.** A descendant that left the group, for example through
  `setsid`, is not stopped. The host stops reading its pipe after a second grace
  period and returns.

## Errors

- `Function.Invoke` returns an error only for local refusals: a
  `*jacquard.InvalidArgument` (such as input too large or too deep for a frame)
  before any worker starts.
- Otherwise the `Invocation` holds the raw result.
- `Invocation.Err`, `Call`, and `MapBatches` return a `*host.InvocationError`
  for Core or carrier failures. Classify it with `errors.Is` against
  `host.ErrDeadlineExceeded`, `host.ErrWorkerLost`, or `host.ErrOperationFailed`.
- A `*jacquard.BindingError` reports a missing handler or a result that does
  not decode into the requested type. In the decode case, `Result` keeps the
  successful Core result.
- `*jacquard.PreparationError` covers checking and the cache.
- `MapBatches` returns the completed values with a `*jacquard.BatchError`
  naming the failed batch. It validates every batch first and retries nothing.

`Invocation.Save` writes argument and result values; it runs only when called.

## Run the projects

```sh
cd go
go run ./cmd/jacquard-examples planner
go run ./cmd/jacquard-examples triage
go run ./cmd/jacquard-examples fulfillment
go run ./cmd/jacquard-examples fulfillment
```

- Planner returns score 16.
- Triage returns PAGE, TODAY, and WATCH.
- Fulfillment returns Fulfilled, then DuplicateOrder with the same receipt.

Rules and synthetic inputs are embedded in the command, and custom input JSON
may follow the project name. `--inspect PATH` saves frames; triage adds a
numbered suffix per batch. State defaults to `.scratch/go-examples/`.

Try explicit recovery in a fresh work directory:

```sh
go run ./cmd/jacquard-examples --workdir .scratch/go-recovery --fault publish-failed fulfillment
go run ./cmd/jacquard-examples --workdir .scratch/go-recovery --deliver-pending fulfillment
go run ./cmd/jacquard-examples --workdir .scratch/go-recovery --deliver-pending fulfillment
```

The first command exits 1 with E1613 and one pending booking. Reconciliation
then delivers one receipt, and the next run delivers zero. The `after-commit`
and `after-delivery` faults exercise E1612 around the snapshot commits.

The Go warehouse (`examples.Warehouse`) is a single-process example. It writes
bookings and outbox entries together as one atomic JSON snapshot, and a
simulated delivery sink deduplicates receipts. It has no cross-process locking
and makes no power-loss durability claim.

## Packages

| Package | Contents |
|---|---|
| `jacquard` | The SDK: `Runtime`, `Program`, `Data`, `Function`, `Invocation`, `Call`, `MapBatches` |
| `examples` and `cmd/jacquard-examples` | Example mappings, the warehouse service, and the command |
| `host` | Low-level `Host`, `OperationRegistry`, `Failure`, and `InvocationResult` for exact identities |
| `protocol` | Strict JSON, v0 values and types, limits, the `Session` state machine, and the generated pin constants |
| `kit` | The embedded pinned conformance kit and its replayer |

## Check the implementation

```sh
cd go
go vet ./...
go test -race ./...           # offline
# With JACQUARD_BIN and JACQUARD_PRELUDE from scripts/core_pin.py install:
go test -count=1 ./...        # adds the live tests against Core
```

The offline tests cover:
- strict JSON and codecs;
- session ordering;
- hostile, truncated, and descendant-holding workers;
- all 21 recordings through a stand-in worker;
- signature parsing and identity groups;
- data mapping and service adaptation;
- that the embedded example files match `examples/`.

The live tests cover:
- all 21 recordings against Core;
- records, enums, unions, options, and 64-row batching;
- refusal of invalid input before any worker starts;
- binding completeness and services;
- unknown completion without retry;
- 25 cases checked against an independent planning oracle;
- cache reuse, invalidation, corruption, and prelude fingerprinting;
- concurrent preparation;
- the full Int range, Real bits, and nested options;
- spawn failure and cancellation;
- output overflow;
- result-mapping failure that keeps the raw result;
- all four commands, and the three fulfillment faults with explicit recovery
  across a restart.

`protocol/compat.go`, `kit/data/`, and `examples/data/` are generated or copied
(see [CONTRIBUTING](../CONTRIBUTING.md)); do not edit them by hand.
