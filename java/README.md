# Jacquard Host for Java

Call checked Jacquard functions from Java, bind Java handlers to their declared
operations, and inspect the exact protocol evidence. This is an independent
Java implementation of the pinned `jacquard-host-v0` boundary. It has no
third-party runtime dependencies and runs Core directly, without Python,
Node.js, Go, or Rust. Java 21+ is required, and on Unix the `mkfifo` command
must be on the JVM's `PATH` (see [process carrier](#process-carrier)).

The artifact has two layers:
- **The SDK** (`org.jacquardlang.host.sdk`) prepares and caches checked
  programs, maps Java values through explicit codecs, and binds handlers to
  exact operation identities. It is comparable to the Python, TypeScript, Go,
  and Rust SDKs.
- **The protocol-level host** (`org.jacquardlang.host`) takes exact
  identities, boundary types, and protocol values for a store you prepare with
  the Core CLI. It is comparable to the Go `host` package and the Rust `host`
  module.

Linux is exercised. macOS should work but has no CI, and Windows is not
claimed. **Experimental:** the protocol is Core's pinned `jacquard-host-v0`,
and Core is installed separately. Each invocation runs one fresh, serial
worker. The host and operating system are trusted. See
[limits](../docs/LIMITS.md).

## Install

The artifact is not published to a package registry. Build and install it from
a clone (`mvn -B install` in this directory), then depend on it:

```xml
<dependency>
  <groupId>org.jacquardlang</groupId>
  <artifactId>jacquard-host</artifactId>
  <version>0.2.0</version>
</dependency>
```

From a clone, install the pinned Core release (see [setup](../docs/SETUP.md)),
then run the quickstart:

```sh
eval "$(python3 scripts/core_pin.py install)"
cd java
mvn -B -q test-compile
java -cp target/classes:target/test-classes org.jacquardlang.host.examples.JacquardExamples quickstart
```

Expected output:

```text
Order total 35: shipping 5
Order total 75: shipping 0
```

The same command runs the other example projects; see
[run the projects](#run-the-projects).

## Call a checked function

```java
import java.io.IOException;
import java.nio.file.Path;
import java.util.List;

import org.jacquardlang.host.sdk.Codec;
import org.jacquardlang.host.sdk.Function;
import org.jacquardlang.host.sdk.Invocation;
import org.jacquardlang.host.sdk.JacquardException;
import org.jacquardlang.host.sdk.PrepareOptions;
import org.jacquardlang.host.sdk.Program;
import org.jacquardlang.host.sdk.Runtime;
import org.jacquardlang.host.sdk.RuntimeOptions;

public final class Shipping {
    public static long shipping(Path rules, Path cache, Path saved) throws JacquardException, IOException {
        // Core and its prelude default to $JACQUARD_BIN and $JACQUARD_PRELUDE.
        Runtime runtime = new Runtime(RuntimeOptions.defaults());
        Program program = runtime.prepare(List.of(rules), new PrepareOptions(cache));
        Function shipping = program.function("shipping-cost");
        long cost = shipping.call(Codec.INT, 35);

        Invocation invocation = shipping.invoke(75);
        System.out.println(invocation.render());
        invocation.save(saved);
        return cost;
    }
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

The cache defaults to `.scratch/jacquard-host-java` under the working
directory; `PrepareOptions` changes it. A changed or incomplete cache entry is
refused rather than repaired. The `--version` check and prelude discovery run
once per Core executable and prelude in the JVM, shared by every `Runtime`
and thread. Each call runs one fresh worker. Prepare only first-party
declaration sources: preparation runs Core's `run` command, without service
bindings, to populate the store.

`RuntimeOptions.defaults()` uses `$JACQUARD_BIN` and `$JACQUARD_PRELUDE`; an
empty option means unset. The runtime refuses a Core whose `--version`
differs from the pinned release. Each Core command must finish within 30
seconds, keeps at most 8 MiB of stdout and 4 KiB of stderr, and runs through
the same host-owned output FIFOs as the worker (see
[process carrier](#process-carrier)). `prepare` also takes a `Cancel`;
cancelling it, or interrupting the calling thread, kills the running command
and its process tree and fails the preparation. The interrupt status stays
set, and a preparation waiting for another thread's version check or prelude
discovery stops waiting too. Once a command exits, at most 4 MiB more of each
output is read and the host closes its ends, so a descendant that keeps the
output open holds neither the caller nor the heap; as for the worker, a
descendant that was already reparented keeps running.

Arguments are Java values encoded by their runtime class (see [data](#data)),
or `codec.arg(value)` where the runtime class does not say enough, such as an
empty list of records. `call` decodes the result with the codec you pass;
`invoke` keeps the whole `Invocation`. Overloads that take a `Cancel` first
kill the worker when it is cancelled, as does interrupting the calling
thread. `Invocation.save` writes the frames and evidence, including argument
and result values; a new file is created owner-only where the file system has
POSIX permissions, and an existing file keeps its permissions.

## Data

Values map through explicit `Codec<T>` objects. The constants and factories
on `Codec` cover the builtin types, and `Codec.of(Class)` derives a codec from
a type's declaration: a record component's generic type, read through
`java.lang.reflect.RecordComponent`, gives its field codec, so `List<Job>`
and `Optional<String>` components need nothing more. There are no
annotations, no annotation processors, and no generated code; a codec is an
ordinary immutable, thread-safe object, so a binding you cannot derive (a
constructor whose name differs from the record's, say) is one explicit
expression. Before any worker starts, the encoded arguments are checked
against the function's checked signature; Core always checks the actual wire
types too.

| Java | Jacquard |
|---|---|
| `long`/`Long` (`Codec.INT`) | `Int`, Core's full 63-bit range; a `long` outside it is refused. `int`/`Integer` (`Codec.INT32`) refuses a result that does not fit |
| `double`/`Double` (`Codec.REAL`) | `Real` as binary64 bits, including NaN payloads and signed zero; an integer at a `Real` parameter is refused |
| `String`, `boolean`/`Boolean`, `protocol.Hash` | `Text` (a lone surrogate is refused), `Bool`, `Hash` |
| `List<T>` (`Codec.list`) | `List T` |
| `Optional<T>` (`Codec.optional`) | `Option T`; `Optional<Optional<T>>` keeps nested options distinct |
| `Codec.UNIT` (Java value `null`), `Codec.tuple(...)` | unit and tuples; `Codec.tuple(Pair.class)` maps a record's components to a tuple |
| a record (`Codec.record`) | one constructor named by the record's simple name, its components in declaration order |
| an enum (`Codec.enumeration`) | nullary constructors by convention: `PAGE_NOW` is `PageNow` (or an explicit map) |
| a sealed interface (`Codec.union`) | the constructors of one data type: each permitted record or enum is its cases |
| `sdk.Dynamic` | any of the above by public name, for code with no application type |
| `protocol.Value` (`Codec.VALUE`) | passed through unchanged |

```java
import java.util.List;
import java.util.Optional;

import org.jacquardlang.host.sdk.Codec;

public final class Incidents {
    public record Incident(String id, String service, long severity, long age, boolean visible, Optional<String> owner) {}

    public enum Urgency { PAGE_NOW, WORK_TODAY, WATCH_ONLY }

    public sealed interface Triage {}

    public record Routed(String id, String team, Urgency urgency, List<String> reasons) implements Triage {}

    public record InvalidIncident(String id) implements Triage {}

    public static final Codec<Incident> INCIDENT = Codec.record(Incident.class);
    public static final Codec<Triage> TRIAGE = Codec.union(Triage.class);
}
```

Record components must follow the Jacquard constructor's positional order;
the pinned CLI exports no field types, so the order is not checked against
Core. Constructor names may use display spelling (`WorkToday`) or store
spelling (`work-today`). A constructor that is not in the prepared program is
refused when a value is encoded, and a result built by a different
constructor fails to decode. `null` is never a Jacquard value. Money crosses
as integer minor units (cents, say) in an `Int`: Jacquard's `Int` is 63-bit
and its `Real` is a binary64 `double`, and there is no decimal type.

Every encoding is bounded by the selected limits as it goes: the JSON depth
the wire form needs, `max_collection_items` for each list and tuple,
`max_value_nodes` values, `max_text_bytes` for each text, and
`max_frame_bytes` for all texts together. A list or text over its limit is
refused before its elements or characters are read, so a small Java value
that denotes a huge one (1024 references to one tuple of 1024 references,
say) costs no more than one frame. `Data.decode` measures a value within the
same node and depth limits before it decodes it. `Program.data()` gives each
public name's exact identity. `Data.make` builds a `Dynamic.Variant` after
checking that its constructor exists, and `Invocation.value` decodes any
result into `Dynamic`.

## Services and failures

```java
import java.util.Map;

import org.jacquardlang.host.HandlerContext;
import org.jacquardlang.host.HandlerException;
import org.jacquardlang.host.sdk.Codec;
import org.jacquardlang.host.sdk.Function;
import org.jacquardlang.host.sdk.JacquardException;
import org.jacquardlang.host.sdk.Operations;
import org.jacquardlang.host.sdk.Program;

public final class Documents {
    public static Function summarize(Program program) throws JacquardException {
        Operations documents = new Operations().on("read-document", Codec.TEXT, Codec.TEXT, (HandlerContext context, String key) -> {
            if (context.isCancelled()) {
                throw HandlerException.completionUnknown("cancelled");
            }
            if (key.equals("example")) {
                return "A synthetic document supplied by Java.";
            }
            throw HandlerException.refused("unknown document");
        });
        return program.function("summarize", Map.of("Documents", documents));
    }
}
```

An `Operations` value maps operation names (store spelling such as
`read-document`, or display spelling; `_` is accepted for `-`) to handlers.
One `Operations` may serve several effects.

- **Parameters:** `on` takes one codec per parameter (up to six) and one for
  the result. The handler may take a leading `HandlerContext`; the remaining
  parameters are decoded from the request.
- **Results:** the handler returns a value for its result codec (`null` for
  `Codec.UNIT`).
- **Failures:** throw `HandlerException.unsupported`, `refused`,
  `outsideFailed`, or `completionUnknown` to report that failure. Any other
  exception or error is reported to Core as completion unknown without its
  message.
- **Binding:** every operation in each checked root effect needs a handler
  before the first invocation, including operations a given path never
  reaches. A name that is no operation of the program is refused.
  `Program.provide` shares services across functions; each function receives
  only its own checked effects.

Handlers run on their own threads, so share state through a lock, as the
fulfillment example's synchronized warehouse does. `RuntimeOptions` takes a
deadline (default 30 s) that bounds each worker lifetime; see
[handlers and cancellation](#handlers-and-cancellation).

## Errors

Every SDK failure is a checked `JacquardException`, whose `kind()` is:
- `INVALID_ARGUMENT`: a local refusal, such as a value of the wrong type or
  one too large or too deep for a frame. No worker starts. `invoke` throws
  only this; otherwise the `Invocation` holds the raw result.
- `INVOCATION`: the invocation ran and did not succeed, from `call`,
  `Invocation.decode`, and `mapBatches`. `failure()` classifies it as
  `DEADLINE_EXCEEDED`, `WORKER_LOST`, `OPERATION_FAILED`, or `OTHER`, and
  `result()` keeps the raw result.
- `BINDING`: an unknown name, a missing handler, or a result that does not
  decode into the requested type. In the decode case, `result()` keeps the
  successful Core result.
- `PREPARATION`: checking, the cache, and the Core commands.

`mapBatches` runs a pure function whose final parameter and result are Lists
over explicit chunks of rows. It validates every batch before the first runs
and retries nothing. A failed batch's exception names it in `batchIndex()`
(-1 when a batch was refused before any worker started) and keeps the decoded
values of the batches that completed in `completed()`.

A request argument that does not decode into a handler's parameter, or a
wrong argument count, is reported as an outside failure, and that handler
does not run. Neither does a handler whose invocation was cancelled, whose
caller was interrupted, or whose lifetime passed while its arguments were
decoded. A result that cannot be encoded is also reported as an outside
failure, but only after the handler returned, so any action it took has
happened. None of these says anything about earlier operations in the
invocation.

## Run the projects

```sh
cd java
mvn -B -q test-compile
CP=target/classes:target/test-classes
java -cp $CP org.jacquardlang.host.examples.JacquardExamples planner
java -cp $CP org.jacquardlang.host.examples.JacquardExamples triage
java -cp $CP org.jacquardlang.host.examples.JacquardExamples fulfillment
java -cp $CP org.jacquardlang.host.examples.JacquardExamples fulfillment
```

- Planner returns score 16.
- Triage returns PAGE, TODAY, and WATCH.
- Fulfillment returns Fulfilled, then DuplicateOrder with the same receipt.

The command reads the rules and synthetic inputs from the repository's
[`examples/`](../examples/README.md), from the repository root or `java/`
(or `-Djacquard.examples=DIR`), so it runs from a clone. Custom input JSON may
follow the project name. `--inspect PATH` saves frames; triage adds a
numbered suffix per batch. State defaults to `.scratch/java-examples/`.

Try explicit recovery in a fresh work directory:

```sh
java -cp $CP org.jacquardlang.host.examples.JacquardExamples --workdir .scratch/java-recovery --fault publish-failed fulfillment
java -cp $CP org.jacquardlang.host.examples.JacquardExamples --workdir .scratch/java-recovery --deliver-pending fulfillment
java -cp $CP org.jacquardlang.host.examples.JacquardExamples --workdir .scratch/java-recovery --deliver-pending fulfillment
```

The first command exits 1 with E1613 and one pending booking. Reconciliation
then delivers one receipt, and the next run delivers zero. The `after-commit`
and `after-delivery` faults exercise E1612 around the snapshot commits.

The example's `Warehouse` is a single-process service. It writes bookings and
outbox entries together as one atomic JSON snapshot, and a simulated delivery
sink deduplicates receipts. It has no cross-process locking and makes no
power-loss durability claim.

## Packages

| Package | Contents |
|---|---|
| `org.jacquardlang.host.sdk` | The SDK: `Runtime`, `Program`, `Data`, `Function`, `Invocation`, `Operations`, `Codec`, `Dynamic`, and `JacquardException` |
| `org.jacquardlang.host` | Low-level `Host`, `OperationRegistry`, `HandlerException`, `HandlerContext`, and `InvocationResult` for exact identities |
| `org.jacquardlang.host.carrier` | The bounded process carrier for `jacquard host worker`, the bounded `Command` runner the SDK uses for Core's commands, and `Cancel` |
| `org.jacquardlang.host.protocol`, `org.jacquardlang.host.json` | Strict JSON, v0 values and types, limits, the `Session` state machine |
| `org.jacquardlang.host.kit` | The class-path copy of the pinned conformance kit and its replayer |

`Compat` holds the generated pin constants.

## The protocol-level host

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

These rules apply to SDK handlers and to low-level handlers. A handler
returns a value or throws a `HandlerException`. The four structured
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

**Core's commands.** The SDK runs Core's `--version`, `check`, `hash`, and
`run` through `Command`, with the same output FIFOs and polls, its stdin the
null device. It waits for the command's exit, not for its output to close:
once the command has exited, it reads at most 4 MiB more of each stream and
closes its ends. A timeout, a cancellation, or the caller's interrupt kills
a snapshot of the command's process tree, as for the worker; a descendant
already reparented keeps running. Tests show that a reparented descendant
flooding stderr after the command exits holds neither the caller nor the heap
(on the JDK's own pipes the same test took 6.4 s in a local run), that
timeout, cancellation, and interruption kill the command's child, the last
keeping the interrupt status, and, on Linux, that repeated preparations with
an escaped descendant holding Core's output leak no descriptor or thread.

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

The live tests in `LiveTest`, `sdk.SdkLiveTest`, and `examples.ExamplesTest`
skip without `JACQUARD_BIN` and `JACQUARD_PRELUDE` (set
`JACQUARD_REQUIRE_LIVE` to make them fail instead). The offline tests drive
`StandIn`, a test-source main class that replays recordings or simulates
faulty workers in a fresh JVM, and `sdk.FakeCore`, a shell script standing in
for Core's commands. Their descendant modes use `sh` and `sleep`, so those
tests run on Unix only, and the descriptor tests run on Linux only. Surefire
starts a fresh JVM for each test class. `JacquardExamples`, the example
command, is a test source, so it needs `mvn test-compile`.

`src/main/java/org/jacquardlang/host/Compat.java` is generated, and
`src/main/resources/org/jacquardlang/host/kit/host-protocol-v0/` is a byte copy
of the pinned kit. `scripts/core_pin.py` maintains both; do not edit them by
hand. `NOTICE` must stay identical to the repository's `NOTICE`, and each
Java example in this README must stay identical to its compiled source under
`src/test/java/org/jacquardlang/host/readme/`; tests check both, and the live
tests run the examples.
