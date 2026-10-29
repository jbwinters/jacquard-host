# Jacquard Host for .NET

Call checked Jacquard functions from .NET, bind .NET handlers to their
declared operations, and inspect the exact protocol evidence. This is an
independent .NET implementation of the pinned `jacquard-host-v0` boundary. It
has no runtime dependencies beyond the .NET base class library and runs Core
directly, without Python, Node.js, Go, Rust, or Java. It targets .NET 8 and
runs on .NET 8 and 10.

The assembly has two layers:
- **The SDK** (`JacquardLang.Host.Sdk`) prepares and caches checked programs,
  maps .NET values through explicit codecs, and binds handlers to exact
  operation identities. It is comparable to the Python, TypeScript, Go, Rust,
  and Java SDKs.
- **The protocol-level host** (`JacquardLang.Host`) takes exact identities,
  boundary types, and protocol values for a store you prepare with the Core
  CLI. It is comparable to the Go `host` package, the Rust `host` module, and
  the Java adapter.

Linux is exercised. macOS should work but has no CI: the carrier and the SDK
call the C library (`poll(2)`, and for the SDK `kill(2)`, `geteuid(2)`,
`mkdir(2)`, and `stat(2)`; see [process carrier](#process-carrier) and
[Core's commands](#cores-commands)), and the macOS status calls are compiled
but not run by any test. Windows is not supported: the worker reports a spawn
failure and `Prepare` refuses. **Experimental:** the protocol is Core's pinned
`jacquard-host-v0`, and Core is installed separately. Each invocation runs one
fresh, serial worker. The host and operating system are trusted. See
[limits](../docs/LIMITS.md).

## Install

The package is not published to a registry. Build it from a clone
(`dotnet pack src/JacquardHost -c Release` in this directory) and reference
the `JacquardLang.Host` package or the project.

From a clone, install the pinned Core release (see [setup](../docs/SETUP.md)),
then run the quickstart:

```sh
eval "$(python3 scripts/core_pin.py install)"
cd dotnet
dotnet build examples/JacquardExamples
dotnet examples/JacquardExamples/bin/Debug/net8.0/JacquardExamples.dll quickstart
```

Expected output:

```text
Order total 35: shipping 5
Order total 75: shipping 0
```

The same command runs the other example projects; see
[run the projects](#run-the-projects).

## Call a checked function

```csharp
using JacquardLang.Host.Sdk;

public static class Shipping
{
    public static long Cost(string rules, string cacheDir, string inspection)
    {
        // Core is $JACQUARD_BIN and $JACQUARD_PRELUDE unless RuntimeOptions say otherwise.
        var runtime = new Runtime();
        PreparedProgram program = runtime.Prepare(new[] { rules }, new PrepareOptions { CacheDir = cacheDir });
        Function shipping = program.Function("shipping-cost");

        // A long converts to an Int argument; Call decodes the result.
        long free = shipping.Call(Codecs.Int, 75L);

        // Invoke keeps the raw frames and evidence, saved only on request.
        Invocation invocation = shipping.Invoke(35L);
        invocation.Save(inspection);
        return invocation.Decode(Codecs.Int) + free;
    }
}
```

The shipping rule is the [quickstart source](../examples/quickstart/rules.jac).
`Prepare` takes `.jac` paths (or `.jqd` paths) in dependency order. It uses
Core's public `check`, `hash`, and `run` commands, and keeps a verified cache
keyed by:
- the source bytes;
- the Core executable bytes;
- every prelude file;
- the protocol and preparation format.

At most 1,024 sources are taken (`Runtime.SourceFiles`); a longer list is
refused from its `Count` before any element is read, and the token is
observed before each element is checked. A path that is empty, holds a NUL,
or is longer than 4,096 chars as given or made absolute (a source, the cache
directory, the prelude, the executable, or a save path) is refused with an
excerpt before the file system sees it. Each source must be a regular file (a
symbolic link to one is followed), and all of them together at most 16 MiB
(`Runtime.SourceLimit`); both are checked from the files' status before a
source is read, so a FIFO or device is refused rather than opened, and no read
goes past the ceiling. The Core executable is fingerprinted in 64 KiB pieces
up to 512 MiB: a larger file is refused from its size before it is read, the
ceiling is enforced again while it is read, and the token is observed before
each piece. The prelude and cache walks stream each directory on an explicit
stack (no directory depth can exhaust the call stack) and read regular files
only (no symbolic links, FIFOs, or devices), at most 100,000 entries (empty
files and directories count) and 256 MiB under one root; each file's size is
checked before it is read and the ceiling again while it is read, and the
token is observed before each entry and after each piece. A source replaced by a
FIFO between that check and its open can still block the open.

A preparation stages its entry in a `prepare-*` directory of the cache root,
created exclusively (`mkdir(2)`), so it never adopts a directory it did not
make. One that fails removes that directory in a streamed walk of at most
100,000 entries that ends at the first cancellation, so a stopped preparation
leaves its staging directory behind. Such a directory is never loaded:
entries are named by their identity digest. Nothing removes it later, because
a name and an age cannot show that the SDK made a directory or that no
preparation is using it; remove leftover `prepare-*` directories yourself when
no preparation is running. Once an entry is published, its vacated staging
path is not touched again. A cancellation that arrives as a preparation
finishes, a warm load included, is reported as the stop, not as a program.

A function whose checked signature nests types deeper than 64 levels (the
protocol's value-depth limit, `max_json_depth`, so no such value could be
passed), names more than 4,096 types, or has more than 16,384 tokens is
refused before the parser recurses past that bound. Like a polymorphic
function it is left unbound: preparation succeeds, the other functions bind,
and `PreparedProgram.Function` on it fails with a `Binding` exception naming
the limit. `check --print-sigs` prints no operation signatures, and the SDK
parses none; an operation's arguments are checked by the protocol's limits
when they arrive.

The cache defaults to `.scratch/jacquard-host-dotnet` under the working
directory; `PrepareOptions.CacheDir` changes it. A symbolic link at the cache
root is followed once, and the directory it names is checked and used;
nothing inside it is followed. A cache entry keeps Core's raw
`check --print-sigs` output and the `hash` output for the source and each
prelude file beside the store, and `program.json` records every file's digest.
Every binding (names, constructors, signatures, and declaration groups) is
derived from those outputs, on the first preparation and again on each load.
A load refuses an entry whose files do not match the recorded digests, or
whose `program.json` bindings differ from what the outputs give, rather than
repairing it. So an accidental or partial change is refused. No record inside
an entry can resist someone who can rewrite it (an output, its recorded
digest, and the bindings changed together load), so the cache's owner is
trusted and only the owner may write it: preparation refuses a cache root, an
entry, or any file or directory in one that the current user does not own or
that its group or others may write, both when it creates an entry and when it
loads one, and it makes each new entry's directories and files owner-only
(Core's store included). The owner and mode come from `statx(2)` on Linux and
`stat(2)` on macOS, without following links. The cache root is then used by
its path, so its device and inode, read at that check, are compared again
before the staging directory is made, before an entry is published, and
before and after each load; a root renamed away and replaced is refused. Its
parent directories are not checked, so a root replaced between one of those
checks and the step that follows it is not detected: keep the cache under
directories only you can write.

The executable and every prelude file and directory are fingerprinted by
path, and Core opens them by path afterwards. Each one's identity (device,
inode, size, mode, owner, and modification and status-change times) is read
as it is fingerprinted and must be unchanged once Core's commands have
finished: inside the version check and prelude discovery, before a new entry
is published, and before an existing one is loaded. A change refuses the
preparation, and a version check or prelude discovery it affected is not
kept. The `--version` check runs once per executable fingerprint (its digest
and its identity) and prelude discovery once per that fingerprint and the
prelude's digest, in the process, shared by every `Runtime` and thread. What
this does not cover is in [LIMITS](../docs/LIMITS.md). Each call runs one
fresh worker. Prepare only
first-party declaration sources: preparation runs Core's `run` command,
without service bindings, to populate the store.

`new Runtime()` uses `$JACQUARD_BIN` and `$JACQUARD_PRELUDE`; an empty option
means unset. The Core executable must be a regular file this user may execute
(`access(2)` with `X_OK`, not merely any execute bit); one this user may not
run is refused with a message saying so. The runtime refuses a Core whose
`--version` differs from the pinned release. Each Core command must finish within 30 seconds, and keeps at
most 8 MiB of stdout and 4 KiB of stderr; see [Core's commands](#cores-commands).
Cancelling the token `Prepare` takes kills the running command and its
process group, and fails the preparation; a preparation waiting for another
thread's version check or prelude discovery stops waiting too. The SDK
observes only the token: `Thread.Interrupt` is not a stop signal, and the
SDK's own waits (`poll(2)` sleeps) do not consume it.

Arguments are `Argument`s. A `long`, `int`, `uint`, `short`, `ushort`,
`sbyte`, or `byte` converts implicitly to an `Int`, a `double` to a `Real`, a
`string` to a `Text`, and a `bool`, a protocol `Hash`, a protocol `Value`, or
a `Dynamic` to itself; anything else, lists and records included, is
`codec.Arg(value)`. A `ulong`, `nuint`, `char`, `float`, `Half`, `decimal`,
`Int128`, `UInt128`, or `BigInteger` does not compile as an argument or in
`Dynamic.Of`: those conversions are marked obsolete as errors, with the
reason, because C# would otherwise convert a `ulong` through `double` (2^53 + 1
would arrive as 2^53) and a `char` to an `Int` (see [data](#data)). `Call`
decodes the result with the codec you pass; `Invoke` keeps the whole
`Invocation`. Overloads that take an argument list and a `CancellationToken`
kill the worker when it is cancelled. `MapBatches` reads the number of rows
once and refuses a list whose count differs before a later batch.
`Invocation.Save` writes the frames and evidence, including argument and
result values, to a new file created exclusively in the target's directory,
made owner-only (0600) and owned by you, and then renames it over the path:
whatever was there is replaced, never written through, so an existing file's
permissions and owner are not kept and a symbolic link there is replaced
rather than followed. The directory must be writable; in a sticky directory
such as `/tmp`, a file another user owns cannot be replaced and the save
fails. The API is synchronous; there are no `Task`-returning variants.

## Data

Values map through explicit `Codec<T>` objects, built with the factories on
`Codecs`. A record is mapped by naming its constructor and giving, for each
field in the constructor's positional order, a codec and a function that
reads it, then a function that builds the record from the decoded fields.
Records are mapped without reflection (enumerations use `Enum.GetValues` and
`Enum.GetName`, and a type hierarchy checks its cases' types with `Type`), and
there is no attribute or source generator: a codec is an
ordinary immutable, thread-safe object, so the library builds clean under the
trimming and ahead-of-time analyzers (`IsAotCompatible`; a trimmed or
native-AOT publish is not tested), and a binding that does not follow a
convention (a constructor whose name differs from the type's, say) is one
explicit expression. Before any worker starts, the encoded arguments are
checked against the function's checked signature; Core always checks the
actual wire types too.

| .NET | Jacquard |
|---|---|
| `long` (`Codecs.Int`) | `Int`, Core's full 63-bit range; a `long` outside it is refused. `int` (`Codecs.Int32`) refuses a result that does not fit |
| `double` (`Codecs.Real`) | `Real` as binary64 bits, including NaN payloads and signed zero; an integer at a `Real` parameter is refused |
| `string`, `bool`, protocol `Hash` | `Text` (a lone surrogate is refused), `Bool`, `Hash` |
| `IReadOnlyList<T>` (`Codecs.List`) | `List T`; decoding returns a new array |
| `Option<T>` (`Codecs.Option`) | `Option T`; `Option<Option<T>>` keeps nested options distinct. `Codecs.Nullable` and `Codecs.NullableValue` map `null` to `None` for one level |
| `Unit` (`Codecs.Unit`), value tuples (`Codecs.Tuple`, two to four items) | unit and tuples |
| a record (`Codecs.Record`, `Codecs.Field`) | one constructor, named by public name, its fields in the order given |
| an enumeration (`Codecs.Enum`) | nullary constructors by convention: member `PageNow` is the constructor `PageNow` (store spelling `page-now`), or an explicit map |
| a type hierarchy (`Codecs.Union`) | the constructors of one data type: each case is a record, enumeration, or union codec of a subtype |
| a recursive type (`Codecs.Lazy`) | a codec made once, thread-safely, on first use, so a field can refer to its own type's codec; a factory that throws keeps nothing and runs again on the next use |
| `Dynamic` (`Codecs.Dynamic`) | any of the above by public name, for code with no application type |
| protocol `Value` (`Codecs.Value`) | passed through unchanged |

```csharp
using System.Collections.Generic;
using JacquardLang.Host.Sdk;

// type Incident = | Incident(id: Text, service: Text, severity: Int, age: Int, visible: Bool, owner: Option Text)
// type Urgency = | PageNow | WorkToday | WatchOnly
// type Triage = | Routed Text Text Urgency (List Text) | InvalidIncident Text
public static class Incidents
{
    public sealed record Incident(string Id, string Service, long Severity, long Age, bool Visible, string? Owner);

    public enum Urgency
    {
        PageNow,
        WorkToday,
        WatchOnly,
    }

    public abstract record Triage;

    public sealed record Routed(string Id, string Team, Urgency Urgency, IReadOnlyList<string> Reasons) : Triage;

    public sealed record InvalidIncident(string Id) : Triage;

    // A record is one constructor: its fields in order, each with a codec and
    // a reader, and a function that builds it from the decoded fields.
    public static readonly RecordCodec<Incident> IncidentCodec = Codecs.Record(
        "Incident",
        Codecs.Field("id", Codecs.Text, (Incident incident) => incident.Id),
        Codecs.Field("service", Codecs.Text, (Incident incident) => incident.Service),
        Codecs.Field("severity", Codecs.Int, (Incident incident) => incident.Severity),
        Codecs.Field("age", Codecs.Int, (Incident incident) => incident.Age),
        Codecs.Field("visible", Codecs.Bool, (Incident incident) => incident.Visible),
        Codecs.Field("owner", Codecs.Nullable(Codecs.Text), (Incident incident) => incident.Owner),
        (id, service, severity, age, visible, owner) => new Incident(id, service, severity, age, visible, owner));

    // An enumeration's members name nullary constructors: PageNow is page-now.
    // A union's cases are records, enumerations, or unions of its subtypes.
    public static readonly UnionCodec<Triage> TriageCodec = Codecs.Union<Triage>(
        Codecs.Record(
            "Routed",
            Codecs.Field("id", Codecs.Text, (Routed routed) => routed.Id),
            Codecs.Field("team", Codecs.Text, (Routed routed) => routed.Team),
            Codecs.Field("urgency", Codecs.Enum<Urgency>(), (Routed routed) => routed.Urgency),
            Codecs.Field("reasons", Codecs.List(Codecs.Text), (Routed routed) => routed.Reasons),
            (id, team, urgency, reasons) => new Routed(id, team, urgency, reasons)),
        Codecs.Record("InvalidIncident", Codecs.Field("id", Codecs.Text, (InvalidIncident invalid) => invalid.Id), id => new InvalidIncident(id)));

    // triage.batch : (TriagePolicy, List Incident) ->{} List Triage, run in
    // explicit batches of eight; each batch is checked before any runs.
    public static IReadOnlyList<Triage> Route(Function batch, Dynamic policy, IReadOnlyList<Incident> incidents) =>
        batch.MapBatches(TriageCodec, IncidentCodec, incidents, 8, new Argument[] { policy });
}
```

The fields given must follow the Jacquard constructor's positional order; the
pinned CLI exports no field types, so the order is not checked against Core.
Constructor names may use display spelling (`WorkToday`) or store spelling
(`work-today`). A constructor that is not in the prepared program is refused
when a value is encoded, and a result built by a different constructor fails
to decode. `null` is never a Jacquard value (except through a nullable
codec). **Money crosses as integer minor units** (cents, say) in an `Int`:
Jacquard's `Int` is 63-bit and its `Real` is a binary64 `double`, and there is
no decimal type. The SDK has no `decimal` codec, and a `decimal` argument or
`Dynamic.Of(decimal)` does not compile (the conversion is obsolete as an error
saying why), so a `decimal` is never converted silently; a test checks both.

Every encoding is bounded by the selected limits as it goes: the JSON depth
the wire form needs, `max_collection_items` for each list, tuple, and
constructor, `max_value_nodes` values, `max_text_bytes` (in UTF-8 bytes) for
each text, and `max_frame_bytes` for all texts together. A list, tuple,
record, or constructor over its count limit is refused from its count before
its items or fields are read, a text over its limit before it is scanned, and
nesting before the nested value is encoded, so a small .NET value that denotes
a huge one (1024 references to one tuple of 1024 references, say) costs no
more than one frame, and no value or codec nesting can recurse past the depth
limit. `ProgramData.Decode` measures a value within the same node and depth
limits before it decodes it. A custom codec may return a `Value` it built
itself rather than through the `Encoder`; such a value is still measured:
before a function call checks the arguments' types, it walks every argument
value under one shared budget (nodes, collection items, text bytes alone and
together, and depth), whatever codec built it, so a small shared graph that
stands for a huge value is refused before the type check walks it. The invoke
frame is then measured as it is rendered, before any worker starts, and the
host measures a handler's result the same way. A value returned by
`ProgramData.Encode` is not measured again. Raw values, decoded values,
nested unions (flattened on first use to their distinct cases), chains of
lazy codecs, and a `Dynamic`'s equality and text are walked iteratively, so
none can exhaust the call stack; equality compares each pair of shared
sub-values once, so values that share sub-values compare in time
proportional to their distinct nodes. `PreparedProgram.Data` gives each public
name's exact identity. `ProgramData.Make` builds a `Dynamic.Variant` after
checking that its constructor exists, and `Invocation.ToDynamic` decodes any
result into `Dynamic`. A `Dynamic` list, tuple, or variant with more items
than the v0 hard limit (1,024) is refused when it is made, from its count
where the collection gives one and otherwise after at most one item past the
limit.

## Services and failures

```csharp
using System.Collections.Generic;
using JacquardLang.Host;
using JacquardLang.Host.Sdk;

public static class Documents
{
    // summarize : (Text) ->{Documents} Text, where Documents has one operation,
    // read-document : (Text) -> Text.
    public static Function Summarize(PreparedProgram program)
    {
        Operations documents = new Operations().On("read-document", Codecs.Text, Codecs.Text, (context, key) =>
        {
            // The context reports a cancelled or ended invocation; its
            // CancellationToken is cancelled then too.
            if (context.IsCancelled)
            {
                throw HandlerException.CompletionUnknown("cancelled");
            }

            return key == "example" ? "A synthetic document supplied by .NET." : throw HandlerException.Refused("unknown document");
        });
        return program.Function("summarize", new Dictionary<string, Operations> { ["Documents"] = documents });
    }
}
```

An `Operations` value maps operation names (store spelling such as
`read-document`, or display spelling; `_` is accepted for `-`) to handlers.
One `Operations` may serve several effects.

- **Parameters:** `On` takes one codec per parameter (up to six) and one for
  the result. The handler may take the `HandlerContext` first; the remaining
  parameters are decoded from the request. The context's `IsCancelled` and
  `CancellationToken` report a cancelled, expired, or ended invocation (see
  [handlers and cancellation](#handlers-and-cancellation)).
- **Results:** the handler returns a value for its result codec
  (`Unit.Value` for `Codecs.Unit`).
- **Failures:** throw `HandlerException.Unsupported`, `Refused`,
  `OutsideFailed`, or `CompletionUnknown` to report that failure. Any other
  exception is reported to Core as completion unknown without its message.
- **Binding:** every operation in each checked root effect needs a handler
  before the first invocation, including operations a given path never
  reaches. A name that is no operation of the program is refused, and a
  service binding more names than the program has operations is refused from
  its count before its handlers are copied. `PreparedProgram.Provide` shares
  services across functions; each function receives only its own checked
  effects.

Handlers run on their own threads, so share state through a lock, as the
fulfillment example's warehouse does. `RuntimeOptions.Deadline` (default
30 s) bounds each worker lifetime.

## Errors

Every failure to prepare, bind, encode, call, or decode is a
`JacquardException`, including any exception (other than
`OutOfMemoryException`) thrown by your own codec, a record field's reader, a
record's constructor function, or a lazy codec's factory: while a value is
encoded, by a function or by `ProgramData.Encode`, it is an `InvalidArgument`
naming the value, and while a value is decoded, by `ProgramData.Decode`,
`Invocation.Decode`, `Call`, or `MapBatches`, a `Binding` failure (from
`Invocation.Decode`, `Call`, and `MapBatches`, with the raw result kept);
either way your exception is its `InnerException`. Names and exception
messages the SDK quotes (a constructor, type, field, effect, operation, or
executable name, or your exception's message) appear as an excerpt of at most
64 chars and its length. A function, effect, operation, public, or constructor
name longer than 4,096 chars is refused before it is looked up or hashed. A message you pass to `Encoder.Invalid` or
`Decoder.Mapping` is kept as written, and no message is longer than
`JacquardException.MessageLimit` (8,192 chars), the start kept and the whole
length noted, once, however many prefixes are joined to it. Some failures
keep their usual .NET types: passing `null` where an object is required
throws `ArgumentNullException`; building a codec that cannot work (an
enumeration map missing a member, a union case that is not a subtype) or a
`Dynamic` over the hard limit throws `ArgumentException` at construction;
`Invocation.Save` throws `IOException` or `UnauthorizedAccessException`; and
an exception from a collection you pass (its `Count`, indexer, or
enumerator) propagates unchanged, except for `MapBatches` rows, which are read
while a batch is encoded and so are reported as a codec failure. A
`JacquardException`'s `Kind` is:
- `InvalidArgument`: a local refusal, such as a value of the wrong type or
  one too large or too deep for a frame. No worker starts. `Invoke` throws
  only this; otherwise the `Invocation` holds the raw result.
- `Invocation`: the invocation ran and did not succeed, from `Call`,
  `Invocation.Decode`, and `MapBatches`. `Failure` classifies it as
  `DeadlineExceeded`, `WorkerLost`, `OperationFailed`, or `Other`, and
  `Result` keeps the raw result.
- `Binding`: such as an unknown name, a missing handler, invalid runtime
  options, or a result that does not decode into the requested type. In the
  decode case, `Result` keeps the successful Core result.
- `Preparation`: checking, the cache, and the Core commands.

`MapBatches` runs a pure function whose final parameter and result are Lists
over explicit batches of rows, encoded with the row codec you pass, with any
other arguments first. It validates every batch before the first runs and
retries nothing. Validation keeps no batch's encoding: each batch is encoded
and checked by a step that returns nothing, and encoded and checked again
just before it runs. Before each batch, and once more after the last, it
observes the token. As in the other SDKs, validation has no deadline of its
own. A cancellation there is reported as the host's aborted invocation,
without a worker. A failed batch's exception names it in `BatchIndex` (-1 when
nothing ran: a batch was refused, or validation stopped) and keeps the decoded
values of the batches that completed in `Completed`, read-only.

A request argument that does not decode into a handler's parameter, or a
wrong argument count, is reported as an outside failure, and that handler
does not run. Neither does a handler whose invocation was cancelled, or whose
lifetime passed, while its arguments were decoded. A result that cannot be
encoded is also reported as an outside failure, but only after the handler
returned, so any action it took has happened. None of these says anything
about earlier operations in the invocation.

## Run the projects

```sh
cd dotnet
dotnet build examples/JacquardExamples
RUN="dotnet examples/JacquardExamples/bin/Debug/net8.0/JacquardExamples.dll"
$RUN planner
$RUN triage
$RUN fulfillment
$RUN fulfillment
```

- Planner returns score 16.
- Triage returns PAGE, TODAY, and WATCH.
- Fulfillment returns Fulfilled, then DuplicateOrder with the same receipt.

The command reads the rules and synthetic inputs from the repository's
[`examples/`](../examples/README.md), from the repository root or `dotnet/`
(or `JACQUARD_EXAMPLES=DIR`), so it runs from a clone. Custom input JSON may
follow the project name. `--inspect PATH` saves frames; triage adds a
numbered suffix per batch. State defaults to `.scratch/dotnet-examples/`. The
exit status is 1 for a Jacquard or SDK failure and 2 for usage or other
errors.

Try explicit recovery in a fresh work directory:

```sh
$RUN --workdir .scratch/dotnet-recovery --fault publish-failed fulfillment
$RUN --workdir .scratch/dotnet-recovery --deliver-pending fulfillment
$RUN --workdir .scratch/dotnet-recovery --deliver-pending fulfillment
```

The first command exits 1 with E1613 and one pending booking. Reconciliation
then delivers one receipt, and the next run delivers zero. The `after-commit`
and `after-delivery` faults exercise E1612 around the snapshot commits.

The example's `Warehouse` is a single-process service. It writes bookings and
outbox entries together as one atomic JSON snapshot (a temporary file it
creates exclusively, then renames; it removes that file only if it created it
and did not rename it), and a simulated delivery sink deduplicates receipts.
It has no cross-process locking and makes no power-loss durability claim.

## Namespaces

| Namespace | Contents |
|---|---|
| `JacquardLang.Host.Sdk` | The SDK: `Runtime`, `PreparedProgram`, `ProgramData`, `Function`, `Invocation`, `Operations`, `Codec<T>` and `Codecs`, `Dynamic`, `Option<T>`, `Unit`, `Argument`, and `JacquardException` |
| `JacquardLang.Host` | Low-level `Host`, `OperationRegistry`, `HandlerException`, `HandlerContext`, and `InvocationResult` for exact identities |
| `JacquardLang.Host.Carrier` | The bounded process carrier for `jacquard host worker` |
| `JacquardLang.Host.Protocol` | Strict JSON, v0 values and types, limits, the `Session` state machine |
| `JacquardLang.Host.Conformance` | The embedded copy of the pinned conformance kit and its replayer |

`Compat` holds the generated pin constants.

## The protocol-level host

The example below invokes a checked function whose single operation is served
by a .NET handler:

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

These rules apply to SDK handlers and to low-level handlers. A handler returns a value or throws a `HandlerException`. The four structured
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
`HandlerContext.CancellationToken` is cancelled when the call stops or ends,
and never earlier: however long the lifetime, the deadline alone does not
cancel it before it passes. While a handler runs, the caller's thread
rechecks the deadline and the caller's token between poll waits of at most
5 ms. That is the requested poll timeout, not a bound on the interval:
reading pending stderr (up to 1 MiB per pass) and thread scheduling can
lengthen it. Whichever check sees the stop, the token's cancellation starts
before the worker is killed. Otherwise the token is cancelled shortly after
the invocation ends. The host cancels it on a short-lived thread of its own,
one per invocation, so a busy thread pool does not delay it. If no thread can
be created, it queues the cancellation as a thread-pool work item instead,
which runs when a pool thread is free; if that cannot be queued either, the
host gives up: the token is never cancelled, `IsCancelled` still turns true,
and the result's `HandlerTokenNotCancelled` is set (its `ToString()` then
ends with `handler token not cancelled`). The flag is not part of
`HostEvidence`, which describes how the invocation ended in the schema the
adapters share and stays empty on success; giving up can follow any outcome,
including a successful one. `Invoke` still returns its result and disposes
the worker. Callbacks registered on the token run on
that thread (or that pool thread), never on the caller's thread or the thread
that cancelled the caller's token; the host does not wait for them, and
anything they throw is discarded. A callback that blocks holds only that
thread. A callback
registered after the token is already cancelled runs at once on the
registering (handler) thread, as with any `CancellationToken`, and its
exception is thrown there. A handler that ignores both keeps running on its
thread until it returns. Nothing is retried.

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
  `poll(2)` (the one native call the carrier makes) whether a pipe is ready,
  with a poll timeout of at most 5 ms that never ends after the deadline, and
  only then reads the bytes already in the pipe,
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
  disposal of a running worker, and when the spawned worker's pipes cannot
  be set up (redirected streams that are not pipe streams), the host calls
  `Process.Kill(true)` through one kill path, which first starts the
  cancellation of the handlers' token. Disposal waits up to one second for a
  killed worker to exit. So does a failed setup, which reports a spawn
  failure and says so if the worker was still running then. .NET stops the
  worker, lists its children from the process table, kills it, and
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

## Core's commands

The SDK runs Core's `--version`, `check`, `hash`, and `run` commands through
its own bounded runner, which uses the carrier's approach to pipes: stdout and
stderr are read only after `poll(2)` reports them ready, on the caller's
thread, in waits of 1 to 5 ms that recheck the 30-second timeout and the
token; .NET's own stream reads are never left waiting. Each read step takes
at most 1 MiB of each stream, keeps at most 8 MiB of stdout (the rest is read
and counted, and refused as "output exceeds 8 MiB") and 4 KiB of stderr, and
after the exit at most 4 MiB more of each is read. The command's stdin is a
pipe the host closes at once.

On Linux, when a `setsid` is installed at `/usr/bin/setsid` or `/bin/setsid`
(util-linux's, or a compatible one such as BusyBox's; any executable there is
used), each command runs as `setsid -- <core>`. .NET's child is never a
process group leader, so `setsid` starts a new session and execs Core without
forking: Core keeps its pid and leads a new process group whose id is that pid
(a test checks both). Every descendant that does not leave the group shares
it. The host kills that group with `kill(-pgid, SIGKILL)` when the command
exits, and on a timeout or cancellation (then also killing Core itself,
waiting up to one second for its exit, and killing the group again for
members forked meanwhile). So `sh -c 'sleep 300 & echo $!'` succeeds, as in
the Rust SDK, and its child has been sent SIGKILL by the time it returns. Once .NET has reaped
Core, a live process holding Core's pid means the id was reused, so the old
group is empty and nothing is killed; a process that took Core's pid after a
full pid wraparound, made its own group, and forked between that check and the
kill could still have that group killed, the same window as the Rust SDK's
`kill(-pgid)`. When `setsid` cannot exec Core (exit status 126 or 127 with its
own `setsid: ` message), the command is reported as a start failure. A
descendant that leaves the group (calls `setsid` itself) is neither detected
nor killed; the host does not wait for the end of the output after the exit,
and closes its ends, so that descendant's next write fails and it cannot hold
the caller.

Without `setsid` (Linux without one at those paths), the command's
descendants are recorded from `/proc` about every 10 ms, by pid and start
time, and every recorded descendant still alive with that start time is
killed when the command returns (the start time is read just before the
kill, as the JDK's process handles do). Each record reads at most 16,384
`/proc` entries, 4 MiB of `stat` files, and 1 KiB of each, at most 16,384
descendants are kept, and a record ends at the deadline or a cancellation,
checked before each entry; the kills at the exit check them before each
descendant, so a stop there leaves the rest running. On a timeout or
cancellation, a last record bounded by the same caps (but not ended by the
stop) is made, then the command is killed, then every recorded descendant;
.NET's `Process.Kill(true)`, which reads every process on the machine, is not
used. A descendant past the caps, or one started and reparented between two
records, keeps running. macOS has no `/proc`, so nothing is recorded there:
only .NET's tree kill on a stop applies.

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

The live tests (`LiveTests`, `SdkLiveTests`, and the live `ExamplesTests`)
are ignored without `JACQUARD_BIN` and `JACQUARD_PRELUDE` (set
`JACQUARD_REQUIRE_LIVE` to make them fail instead). The offline tests drive
`test/JacquardHost.StandIn`, a console program that replays recordings or
simulates faulty workers in a fresh process, and a fake Core (a shell script)
for the SDK's preparation. Their descendant modes use `sh` and `sleep`, so
those tests run on Unix only, and the descriptor, Core command, and
preparation tests run on Linux only. The build treats warnings as errors,
with nullable reference types and the `8.0-recommended` analyzers.

`src/JacquardHost/Compat.cs` is generated, and
`src/JacquardHost/kit/host-protocol-v0/` is a byte copy of the pinned kit.
`scripts/core_pin.py` maintains both; do not edit them by hand. `NOTICE` must
stay identical to the repository's `NOTICE`, and the README's examples must
stay identical to `test/JacquardHost.Tests/Readme/` (`Shipping.cs`,
`Incidents.cs`, `Documents.cs`, and `Echo.cs`, in that order), which the live
tests run; tests check both.
