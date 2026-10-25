# Jacquard Host for Go

An independent Go implementation of the pinned `jacquard-host-v0` boundary:
strict protocol codecs, the host session state machine, a bounded process
carrier for `jacquard host worker`, a low-level `Host` with a closed operation
registry, and a replayer for Core's conformance kit. It runs Core directly and
uses only the Go standard library. It does not call Python or Node.js.

Go 1.24+. Linux is exercised; macOS should work but is not exercised in CI, and
Windows is not claimed. The carrier is experimental, Core is a separate
executable, and the host and operating system are trusted. A higher-level SDK
(prepared programs, typed data, services, batching, examples) is planned to
follow; this module currently exposes the protocol-level API.

## Install

From a clone, configure the pinned Core release (see [setup](../docs/SETUP.md)):

```sh
eval "$(python3 scripts/core_pin.py install)"
cd go
go test ./...
```

In another module:

```sh
go get github.com/jbwinters/jacquard-host/go@latest
```

## Packages

| Package | Contents |
|---|---|
| `protocol` | Strict JSON (duplicate keys, lone surrogates, non-integers, and out-of-range integers refused), v0 values and types, limits, frame validation, the `Session` state machine, and the generated pin constants |
| `host` | `Host`, `OperationRegistry`, `Handler`, `Failure`, and `InvocationResult` with Core frames, host frames, requests, exit status, bounded stderr, and host evidence |
| `kit` | The embedded pinned kit, verified against its digests at load, and `ReplayOne`/`ReplayKit` |

## Invoke a checked term

```go
h, err := host.New(store, host.Options{
	Command:  []string{os.Getenv("JACQUARD_BIN")},
	Deadline: 30 * time.Second,
})
if err != nil {
	log.Fatal(err)
}
registry := host.NewRegistry()
err = registry.Bind(worldEffect, sendOperation, func(ctx context.Context, args []protocol.Value) (protocol.Value, error) {
	text := args[0].(protocol.Text)
	if text == "" {
		return nil, host.Refused("empty message")
	}
	return text + "!", nil
})
if err != nil {
	log.Fatal(err)
}
text := protocol.Nominal{Identity: textType}
result := h.Invoke(ctx, echo, protocol.Interface{
	Parameters: []protocol.BoundaryType{text}, Result: text, Effects: []protocol.Hash{worldEffect},
}, []protocol.Value{protocol.Text("ping")}, registry)
value, err := result.Unwrap()
```

Identities are exact `HASH_V0` values from Core (`jacquard hash`). The
complete invoke frame is validated before a worker starts; a frame that cannot
be sent is reported as a local `host_error` with no process started.

## Values

| Go | Jacquard |
|---|---|
| `protocol.Int` | Signed 63-bit `Int`; out-of-range values are refused |
| `protocol.Real` | Binary64 bits, including NaN payloads and signed zero (`RealOf`, `Float64`) |
| `protocol.Text` | UTF-8 `Text` within `max_text_bytes` |
| `protocol.Hash` | Explicit `Hash` |
| `protocol.Tuple` | Positional tuple; `Tuple{}` is unit |
| `protocol.Constructor` | Constructor identity and positional arguments |

## Failures, deadlines, and cancellation

A handler answers with a value or a `*host.Failure`: `Unsupported`, `Refused`,
`OutsideFailed`, or `CompletionUnknown`, the four pairs the protocol permits.
Any other error, or a panic, is reported to Core as completion unknown without
exposing its message. Nothing is retried.

`Options.Deadline` bounds the whole worker lifetime: spawn, every pipe read and
write, each handler, and process exit. When it expires, or the caller's context
is cancelled, the host kills and reaps the worker, cancels the handler's
context, and sends no late response. A handler that ignores its context keeps
running in its goroutine until it returns; the host has stopped waiting for it.
This does not prove that an outside action stopped or rolled back.

`Unwrap` returns an `*host.InvocationError`. Classify it with `errors.Is`:
`host.ErrDeadlineExceeded`, `host.ErrWorkerLost`, or `host.ErrOperationFailed`.
`Origin()` reports whether Core produced the diagnostics. Host evidence uses the
schema `jacquard-host-go-evidence-v0` and is kept separate from Core's frames.

## Conformance

```go
replays, err := kit.ReplayKit(ctx, store, kit.Options{Command: []string{bin}}, nil)
```

Install the store first with `jacquard run` on `kit.File("kit/fixtures.jac")`.
Each `Replay` reports differences from the recording and any diagnostic prose
drift separately; prose is never normalized.

## Check the implementation

```sh
cd go
go vet ./...
go test -race ./...                       # offline: stand-in workers
# With JACQUARD_BIN and JACQUARD_PRELUDE from scripts/core_pin.py install:
go test -count=1 ./...                    # adds the live tests against Core
```

The offline tests use the test binary as stand-in workers. They cover strict
JSON and Unicode, exact Int and Real codecs, limits, session ordering, hostile
and truncated workers, stderr retention, write backpressure, cancellation, and
all 21 recordings. The live tests replay all 21 recordings against Core and
cover pure and effectful invocations, the four failure pairs, handler errors
and panics, and cooperative and abandoned handlers at the deadline.

The pin constants in `protocol/compat.go` and the kit under `kit/data/` are
generated and vendored by `scripts/core_pin.py`; do not edit them by hand.
