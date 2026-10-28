# Jacquard Host for Java

An independent Java adapter for Jacquard Core's experimental `jacquard-host-v0`
protocol. It implements the pinned protocol on its own, without calling Python,
Node.js, Go, or Rust, and has no third-party runtime dependencies. Java 21+
is required, and on Unix the `mkfifo` command must be on the JVM's `PATH`
(see "Process carrier" below).

This is the protocol-level adapter, comparable to the Go `host` package and the
Rust `host` module. It includes strict codecs, the host session state machine,
and a bounded process carrier for `jacquard host worker`. It also provides a
`Host` with a closed operation registry and a replayer for Core's pinned
conformance kit. There is no SDK layer yet. It does not compile programs, map
native types, or cache stores. You prepare a store with the Core CLI and pass
exact identities and boundary types.

**Experimental:** the protocol is Core's pinned `jacquard-host-v0`, and Core is
installed separately. Each invocation runs one fresh, serial worker. The host
and operating system are trusted. Linux is exercised; macOS should work but has
no CI; Windows is not claimed. See [limits](../docs/LIMITS.md).

## Use

The artifact is not published to a package registry. Build and install it from
a clone (`mvn -B install` in this directory), then depend on it:

```xml
<dependency>
  <groupId>org.jacquardlang</groupId>
  <artifactId>jacquard-host</artifactId>
  <version>0.2.0</version>
</dependency>
```

The example below invokes a checked function whose single operation is served
by a Java handler:

```java
import java.time.Duration;
import java.util.List;

import org.jacquardlang.host.HandlerException;
import org.jacquardlang.host.Host;
import org.jacquardlang.host.HostOptions;
import org.jacquardlang.host.InvocationException;
import org.jacquardlang.host.OperationRegistry;
import org.jacquardlang.host.carrier.Cancel;
import org.jacquardlang.host.carrier.Options;
import org.jacquardlang.host.protocol.BoundaryType;
import org.jacquardlang.host.protocol.Hash;
import org.jacquardlang.host.protocol.Interface;
import org.jacquardlang.host.protocol.Value;

public final class Echo {
    public static Value echo(String store, Hash target, Hash text, Hash world, Hash send) throws InvocationException {
        // The command defaults to $JACQUARD_BIN, then `jacquard`.
        Options carrier = Options.defaults().withDeadline(Duration.ofSeconds(5));
        Host host = new Host(store, new HostOptions(carrier, null));
        OperationRegistry registry = new OperationRegistry().bind(world, send, (context, args) -> {
            if (context.isCancelled()) {
                throw HandlerException.completionUnknown("cancelled");
            }
            if (args.size() == 1 && args.get(0) instanceof Value.Text input) {
                return new Value.Text(input.value() + "!");
            }
            throw HandlerException.outsideFailed("expected one text argument");
        });
        BoundaryType textType = new BoundaryType.Nominal(text, List.of());
        Interface spec = new Interface(List.of(textType), textType, List.of(world));
        return host.invoke(target, spec, List.of(new Value.Text("ping")), registry, new Cancel()).valueOrThrow();
    }
}
```

`invoke` returns an `InvocationResult` in every case; only a null argument
throws (`NullPointerException`), before any worker starts. The result keeps
Core's frames, diagnostics, and evidence. It also keeps the host's frames, exit
status, bounded stderr, and host evidence (`jacquard-host-java-evidence-v0`).
`valueOrThrow` returns the value, or throws an `InvocationException`. The
exception's `failure()` is one of `DEADLINE_EXCEEDED`, `WORKER_LOST`,
`OPERATION_FAILED`, or `OTHER`.

Values are `Value.Int` (Core's signed 63-bit integer, held in a `long`),
`Value.Text`, `Value.Real` (the exact binary64 bits, so NaN payloads and signed
zero survive), `Value.HashValue`, `Value.Tuple` (`Value.UNIT` is the empty
tuple), and `Value.Constructor`. A `long` outside `-2^62..2^62-1` has no
encoding and is refused with E1604; a string with a lone surrogate, which Java
strings can hold, is refused with E1601, as in the TypeScript adapter, unless
it is already too long for `max_text_bytes` or the frame, which is refused
with E1602 before it is scanned.

## Handlers and cancellation

A handler returns a value or throws a `HandlerException`. The four structured
categories are `unsupported`, `refused`, `outsideFailed`, and
`completionUnknown`. Any other exception or error a handler throws is reported
to Core as completion unknown, and its message is never sent. A returned value
with no encoding within the selected limits, including `null`, is reported as
an outside failure. A message longer than `max_host_message_bytes` is cut at a
code point, and a lone surrogate in it becomes U+FFFD.

Each handler runs on its own daemon thread, bounded by the worker's lifetime
deadline. No handler starts once the call is cancelled, interrupted, or past
its lifetime: the host checks after decoding the request, and the handler
thread checks again, caller's interrupt included, just before calling the
handler. A stop that lands after that check is seen by the handler through
its context. At the deadline, when
the caller's `Cancel` is triggered, or when the calling thread is interrupted,
the host kills the worker's process tree and sends no late response. An
interrupt is reported as an abort, and the caller's interrupt status is
restored before `invoke` returns. Cancellation is cooperative.
`HandlerContext.isCancelled` turns true as soon as the call is cancelled, the
caller interrupted, or the lifetime passed, and stays true once the invocation
ends, but a handler that ignores it keeps running on its thread until it
returns. Nothing is retried.

## Process carrier

Java cannot start a process in its own process group, and a blocking read
from a pipe cannot be interrupted. The carrier is built around both:

- **The host owns the output pipes on Unix.** The JDK's own process pipes
  drain themselves on its process reaper thread when the process exits, under
  the stream lock that `available()` and `close()` also take, for as long as a
  descendant keeps writing, and into a buffer with no bound. So on Unix the
  host creates two FIFOs with `mkfifo -m 600` in a private temporary
  directory, opens each read-write (the open never waits, and the host always
  holds a read end), redirects the worker's stdout and stderr to them, and
  removes the directory once the worker starts. The JDK never touches those
  descriptors, and output a descendant keeps writing stays in the kernel's
  pipe buffer (64 KiB by default on Linux; a writer can enlarge it), where
  the writer blocks, until the host disposes the worker and closes its ends;
  the writer's next write then fails. If `mkfifo` is missing, fails, or takes
  more than 5 s, the worker does not start and the invocation reports a
  carrier loss (`worker_spawn_failed`). Making the FIFOs obeys the same deadline,
  `Cancel`, and interrupt status as the rest of the call, which then reports
  `deadline` or `aborted` and does not start the worker. On other systems the
  JDK's pipes and their drain are used.
- **Reads never block.** stdout and stderr are read on the caller's thread,
  only after `available()` reports bytes in the pipe. Between polls the
  thread parks for 0.1 to 5 ms. Every wait, whether for a frame, a write, a
  handler, or the exit, rechecks the one lifetime deadline, the `Cancel`, and
  the interrupt status. No thread reads stdout or stderr, so none can be
  left blocked on them, and at most 1 MiB of stderr is read per poll, of which
  only the bounded prefix is kept.
- **Writes run on one stdin thread**, because a pipe write can block. The
  caller waits for each write under the lifetime. Each frame is written in
  chunks of at most 4096 bytes. Each chunk starts only while input is open,
  the deadline has not passed, cancellation has not been requested, and the
  calling thread is not interrupted. A
  chunk already started may still complete, and the worker may read it,
  until the caller's next poll kills the worker: at most one poll slice
  (5 ms) after a cancellation, and less after the deadline.
- **Expiry kills a process tree, not a group.** At expiry, cancellation, or
  disposal of a running worker, the host snapshots `Process.descendants()`
  and calls `destroyForcibly()` (SIGKILL on Unix) on the worker's
  `ProcessHandle` and on every process in the snapshot. It signals through the
  handle because `Process.destroyForcibly()` also closes stdin on the calling
  thread, which would wait behind a blocked write. Killing the worker and a
  descendant that holds stdin ends such a write. A descendant that was
  already reparented is not in the snapshot and keeps running: one whose
  parent exited, or one that daemonized. So is one started after the
  snapshot.
- **The JDK owns the exit.** It reaps the worker as soon as it exits. So
  stdout ends when the worker has exited and its pipe is empty, not when the
  worker closes stdout. A descendant still holding a pipe after the exit is
  neither observed nor waited for. The
  Go and Rust hosts report a descendant that keeps stderr open after a
  successful exit as a carrier loss and, on Linux, stop it with the worker's
  process group. The Java host reports the worker's own result, and the
  descendant keeps running.
- **Signals are inferred.** The JDK reports a process ended by signal N as
  exit status 128 + N. The host reports `signal() == "SIGKILL"` only when it
  killed the worker itself and the status is 137; any other value is
  reported as the exit status.

What this guarantees: an invocation returns at its deadline plus bounded
cleanup. Each cleanup wait is capped at one second: for a killed worker's exit
to be recorded, for a still-running worker to exit at disposal, and for the
stdin thread to stop. So in the worst case `invoke` returns about three seconds
after the deadline. On Unix this holds even while a descendant that escaped
the kill floods stdout or stderr, and the flood does not reach the heap. The
carrier leaves no thread or pipe descriptor behind, with one exception: a
stdin write, or the flush and close that end the stdin thread, blocked on a
pipe whose read end is held by a descendant that escaped the kill and never
reads. That stdin thread stays blocked until the descendant exits or reads,
and so does the JDK's process reaper thread for that worker, which drains the
same stream under its lock. The caller is not held. On non-Unix systems the
JDK's output pipes are used, so a descendant that keeps writing after the
worker exits holds the caller, and grows the heap, until it stops. Separately, a handler that ignores cancellation keeps its own thread
until it returns (see above). Linux tests count descriptors and adapter threads
across successful and failed invocations, and check that a blocked write ends
when expiry or cancellation kills a live worker and the child holding its
stdin, and that a reparented descendant flooding stdout or stderr holds
neither the caller nor the heap, at a worker's own exit, at expiry, and at
cancellation.

## Conformance kit

`Kit.load()` verifies the class-path copy of the kit against the pinned
digests. `Replayer.replayKit` replays all 21 recordings, including the hostile
host inputs, against a worker. It reports any semantic differences and any
drift in non-normative diagnostic prose.

## Development

From this directory, with Maven 3.9+ and JDK 21 or later:

```sh
mvn -B verify
# With the pinned Core configured (eval "$(python3 ../scripts/core_pin.py install)"):
JACQUARD_REQUIRE_LIVE=1 mvn -B verify
```

The live tests in `LiveTest` skip without `JACQUARD_BIN` and
`JACQUARD_PRELUDE` (set `JACQUARD_REQUIRE_LIVE` to make them fail instead).
The offline tests drive `StandIn`, a test-source main class that replays
recordings or simulates faulty workers in a fresh JVM. Its descendant modes
use `sh` and `sleep`, so those tests run on Unix only, and the descriptor test
runs on Linux only. Surefire starts a fresh JVM for each test class.

`src/main/java/org/jacquardlang/host/Compat.java` is generated, and
`src/main/resources/org/jacquardlang/host/kit/host-protocol-v0/` is a byte copy
of the pinned kit. `scripts/core_pin.py` maintains both; do not edit them by
hand. `NOTICE` must stay identical to the repository's `NOTICE`, and the
README example must stay identical to `src/test/java/org/jacquardlang/host/readme/Echo.java`;
tests check both.
