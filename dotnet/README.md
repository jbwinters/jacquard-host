# Jacquard Host for .NET

An independent .NET adapter for Jacquard Core's experimental `jacquard-host-v0`
protocol. It implements the pinned protocol on its own, without calling
Python, Node.js, Go, Rust, or Java, and has no runtime dependencies beyond
the .NET base class library. It targets .NET 8 and runs on .NET 8 and 10. It
runs workers on Linux and macOS: the carrier calls the C library's `poll(2)`
(see "Process carrier" below).

This is the protocol-level adapter, comparable to the Go `host` package, the
Rust `host` module, and the Java adapter. It includes strict codecs, the host
session state machine, and a bounded process carrier for `jacquard host
worker`. It also provides a `Host` with a closed operation registry and a
replayer for Core's pinned conformance kit. There is no SDK layer yet. It does
not compile programs, map native types, or cache stores. You prepare a store
with the Core CLI and pass exact identities and boundary types.

**Experimental:** the protocol is Core's pinned `jacquard-host-v0`, and Core is
installed separately. Each invocation runs one fresh, serial worker. The host
and operating system are trusted. Linux is exercised; macOS should work but has
no CI; Windows is not supported (the worker reports a spawn failure). See
[limits](../docs/LIMITS.md).

## Use

The package is not published to a registry. Build it from a clone
(`dotnet pack src/JacquardHost -c Release` in this directory) and reference
the `JacquardLang.Host` package or the project. The example below invokes a
checked function whose single operation is served by a .NET handler:

```csharp
using System;
using JacquardLang.Host;
using JacquardLang.Host.Carrier;
using JacquardLang.Host.Protocol;

public static class Echo
{
    public static Value Run(string store, Hash target, Hash text, Hash world, Hash send)
    {
        // The command defaults to $JACQUARD_BIN, then `jacquard`.
        var options = new HostOptions { Carrier = new WorkerOptions { Deadline = TimeSpan.FromSeconds(5) } };
        var host = new Host(store, options);
        OperationRegistry registry = new OperationRegistry().Bind(world, send, (context, args) =>
        {
            if (context.IsCancelled)
            {
                throw HandlerException.CompletionUnknown("cancelled");
            }

            if (args is [TextValue input])
            {
                return new TextValue(input.Value + "!");
            }

            throw HandlerException.OutsideFailed("expected one text argument");
        });
        var textType = new NominalType(text);
        var spec = new CallInterface(new[] { textType }, textType, new[] { world });
        return host.Invoke(target, spec, new[] { new TextValue("ping") }, registry).ValueOrThrow();
    }
}
```

`Invoke` returns an `InvocationResult` in every case; only a null argument
throws (`ArgumentNullException`), before any worker starts. The result keeps
Core's frames, diagnostics, and evidence. It also keeps the host's frames,
exit status, bounded stderr, and host evidence
(`jacquard-host-dotnet-evidence-v0`). `ValueOrThrow` returns the value, or
throws an `InvocationException` whose `Failure` is one of `DeadlineExceeded`,
`WorkerLost`, `OperationFailed`, or `Other`.

## Values and numbers

Values are `IntValue`, `TextValue`, `RealValue`, `HashValue`, `TupleValue`
(`Value.Unit` is the empty tuple), and `ConstructorValue`; they compare
structurally.

- **Integers are exact.** `IntValue` holds Core's signed 63-bit integer in a
  `long`. On the wire an `Int` is decimal text, and JSON integers in frames
  are decimal tokens; both are parsed from their text as a `long`, never
  through a `double` or a `decimal`, so every value in `-2^62..2^62-1`
  crosses unchanged. A non-canonical text (`"01"`, `"-0"`, `"+1"`), a
  fraction or exponent, and anything outside the range are refused (E1601),
  as the Rust and Java adapters refuse them. A `long` outside the range has
  no encoding and is refused with E1604.
- **Money crosses as integer minor units**: an amount of 12.34 in a currency
  with two decimal places is `IntValue(1234)`. Do not use `RealValue` for
  money.
- **Reals are binary64.** `RealValue` keeps the exact IEEE 754 bits, so NaN
  payloads and signed zero survive; `RealValue.Of(double)` and `ToDouble()`
  convert.
- **Text is Unicode scalars.** A string with a lone surrogate, which .NET
  strings can hold, is refused with E1601, unless it is already too long for
  `max_text_bytes` or the frame, which is refused with E1602 before it is
  scanned.

## Bounded work

Work on the caller's thread is bounded by the selected limits, not by the
size of what a handler or caller hands over:

- Frame encoding counts UTF-8 bytes as it writes them and stops as soon as
  the count passes `max_frame_bytes`. A handler result of 256 references to
  one 256 KiB text (within every value limit, over 384 MiB once escaped) is
  refused as an outside failure after about one frame of work.
- Text validation refuses a text whose length in chars already exceeds
  `max_text_bytes` before scanning it, and charges every text in a frame
  against `max_frame_bytes`, so validating a value scans at most about one
  frame of text however often a long text is referenced.
- A frame given as a caller's own dictionary (rather than a `JsonObject`) is
  refused by its field count and key lengths before it is copied or sorted.
  A registry with more than `max_operations` bindings is refused before a
  worker starts.
- Reads are bounded by `max_frame_bytes`, and stderr by `max_stderr_bytes`.

## Handlers and cancellation

A handler returns a value or throws a `HandlerException`. The four structured
categories are `Unsupported`, `Refused`, `OutsideFailed`, and
`CompletionUnknown`. Any other exception a handler throws is reported to Core
as completion unknown, and its message is never sent. A returned value with
no encoding within the selected limits, including `null`, is reported as an
outside failure. A message longer than `max_host_message_bytes` is cut at a
code point, and a lone surrogate in it becomes U+FFFD.

Each handler runs on its own background thread, bounded by the worker's
lifetime deadline. No handler starts once the call is cancelled or past its
lifetime: the host checks after decoding the request, before it creates the
handler thread, and the thread checks again just before calling the handler.
A stop that lands after that check is seen by the handler through its
context. At the deadline, or when the caller's `CancellationToken` is
cancelled, the host kills the worker's process tree and sends no late
response. Cancellation is cooperative: `HandlerContext.IsCancelled` turns true
as soon as the call is cancelled or its lifetime passes (it reads the
deadline exactly), and stays true once the invocation ends.
`HandlerContext.CancellationToken` is cancelled when the caller's token is,
shortly after the deadline (a timer, so up to a few milliseconds late), and
when the invocation ends. Callbacks registered on that token run on a
thread-pool thread, never on the caller's thread, the thread that cancelled
the caller's token, or the timer; the host does not wait for them, and
anything they throw is discarded. A callback that blocks holds only that
pool thread. A handler that ignores both keeps running on its thread until it
returns. Nothing is retried.

`Invoke` advertises and dispatches one snapshot of the registry, taken when
it is called: an operation bound during the invocation is not served by it.
`HandlerException` refuses a category outside the four
(`ArgumentOutOfRangeException`); one that reaches dispatch anyway is reported
as completion unknown.

## Process carrier

.NET cannot start a process in its own process group, and its pipe streams
offer no read or write that a deadline or a cancellation can interrupt: on
Unix an anonymous pipe's asynchronous read is a blocking read on a pool
thread. The carrier is built around both:

- **No read or write ever blocks.** The caller's thread asks the C library's
  `poll(2)` (the one native call the adapter makes) whether a pipe is ready,
  in waits of at most 5 ms that never end after the deadline, and only then reads the bytes already in the pipe,
  or writes at most `PIPE_BUF` bytes (4096 on Linux, 512 on macOS), which a
  pipe that poll reports writable accepts at once. Every wait, whether for a
  frame, a write, a handler, or the exit, rechecks the one lifetime deadline,
  the `CancellationToken`, and disposal (`Worker.Dispose` may come from
  another thread). No thread reads or writes the worker's pipes, so none can
  be left blocked on them, and a worker that never reads its input cannot
  hold a write past the deadline.
- **Nothing drains the pipes behind the host's back.** .NET's `Process` reads
  redirected output on its own only when asked to (`BeginOutputReadLine`),
  which the carrier never does. stderr is read whenever the caller waits, at
  most 1 MiB per read pass, and only a bounded prefix
  (`max_stderr_bytes`) is kept.
- **Expiry kills a process tree, not a group.** At expiry, cancellation, or
  disposal of a running worker, the host calls `Process.Kill(true)`: .NET
  stops the worker, lists its children from the process table, kills it, and
  does the same for each child. A descendant that was already reparented is
  not found and keeps running: one whose parent exited, or one that
  daemonized. So is one started after its parent was listed. Descendants are
  found and signalled by pid, so a descendant that exits, and is reaped,
  between the listing and the signal can have its pid reused by an unrelated
  process, which would then be signalled. The window is short, but the Java
  host (process handles) and the Rust and Go hosts (process groups) do not
  have it.
- **A descendant holding the pipes after the exit is detected.** The runtime
  reaps the worker when it exits. If stdout or stderr still has a writer
  after that, a descendant holds it: the host waits up to one second for it to
  close, then reports a carrier loss ("worker descendants kept its pipes open
  after exit"), as the Go and Rust hosts do. It cannot stop that descendant;
  closing its ends of the pipes at disposal makes the descendant's next write
  fail.
- **Signals are inferred.** .NET reports a process ended by signal N as exit
  status 128 + N. The host reports `Signal == "SIGKILL"` only when it killed
  the worker itself and the status is 137; any other value is reported as the
  exit status.

What this guarantees: an invocation returns at its deadline plus bounded
cleanup. `WorkerOptions.Deadline` accepts any positive `TimeSpan`, up to
`TimeSpan.MaxValue`; one longer than `Worker.MaxDeadline` (about 73 years) is
clamped to it, and cancellation and disposal end the call promptly whatever
the deadline. Each cleanup wait is capped at one second: for a killed worker's exit
to be recorded, and for a still-running worker to exit at disposal. So in the
worst case `Invoke` returns about two seconds after the deadline, plus the
time .NET takes to list the process table for the kill (the tests allow three
seconds). This holds
while a descendant that escaped the kill floods stdout or stderr, or holds
stdin without reading, and the flood does not reach the heap. The carrier
leaves no thread or pipe descriptor behind; a handler that ignores
cancellation keeps its own thread until it returns (see above). Linux tests
count descriptors and adapter threads across successful and failed
invocations, and check that a reparented descendant flooding stdout or
stderr holds neither the caller nor the heap, at a worker's own exit, at
expiry, and at cancellation.

## Conformance kit

`Kit.Load()` verifies the embedded copy of the kit against the pinned digests.
`Replayer.ReplayKit` replays all 21 recordings, including the hostile host
inputs, against a worker. It reports any semantic differences and any drift in
non-normative diagnostic prose. It keeps at most one frame past each
recording's, so a worker that keeps sending cannot fill memory.

## Development

From this directory, with the .NET 8 or 10 SDK:

```sh
dotnet test test/JacquardHost.Tests -f net8.0
# With a .NET 10 SDK, also:
dotnet test test/JacquardHost.Tests -f net10.0
# With the pinned Core configured (eval "$(python3 ../scripts/core_pin.py install)"):
JACQUARD_REQUIRE_LIVE=1 dotnet test test/JacquardHost.Tests -f net8.0
```

The live tests in `LiveTests` are ignored without `JACQUARD_BIN` and
`JACQUARD_PRELUDE` (set `JACQUARD_REQUIRE_LIVE` to make them fail instead).
The offline tests drive `test/JacquardHost.StandIn`, a console program that
replays recordings or simulates faulty workers in a fresh process. Its
descendant modes use `sh` and `sleep`, so those tests run on Unix only, and
the descriptor tests run on Linux only. The build treats warnings as errors,
with nullable reference types and the `8.0-recommended` analyzers.

`src/JacquardHost/Compat.cs` is generated, and
`src/JacquardHost/kit/host-protocol-v0/` is a byte copy of the pinned kit.
`scripts/core_pin.py` maintains both; do not edit them by hand. `NOTICE` must
stay identical to the repository's `NOTICE`, and the README example must stay
identical to `test/JacquardHost.Tests/Readme/Echo.cs`; tests check both.
