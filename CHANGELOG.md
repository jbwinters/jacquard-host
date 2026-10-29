# Changelog

## Unreleased

- Rust adapter: an independent, standard-library-only crate in `rust/`
  (`jacquard-host`, Rust 1.88+). It provides strict codecs, the session state
  machine, a bounded process carrier for `jacquard host worker`, and a `Host`
  with a closed operation registry. It also embeds the pinned kit and replays
  all 21 recordings. `scripts/core_pin.py` now generates `rust/src/compat.rs`
  and keeps `rust/kit/` in sync, and CI runs the crate's offline and live
  tests.
- Rust SDK (`jacquard_host::sdk`): prepares and caches checked programs with
  Core's public commands, maps native Rust values through the `Encode` and
  `Decode` traits (with `record!` and `variants!` for application types, and
  `Dynamic` for untyped code), binds closures to exact operation identities,
  checks arguments against the checked signature before a worker starts, and
  keeps each invocation's raw evidence. `map_batches` runs explicit batches.
  The four example projects run as `cargo run --example jacquard-examples`.
  The low-level host gains `From<E: Error> for HandlerError`, so `?` in a
  handler reports completion unknown. As a consequence `HandlerError` cannot
  implement `std::error::Error`, and a downstream `From` impl for a type that
  implements `Error` conflicts with it.
- Java adapter: an independent Maven project in `java/`
  (`org.jacquardlang:jacquard-host`, Java 21+) with no third-party runtime
  dependencies (on Unix it runs the system `mkfifo`).
  It provides strict codecs, the session state machine, a bounded process
  carrier for `jacquard host worker`, and a `Host` with a closed operation
  registry. It carries the pinned kit as class-path resources and replays all
  21 recordings. Java cannot
  start the worker in its own process group or interrupt a pipe read, so the
  carrier never blocks on a read, reads the worker's output through FIFOs it
  creates with `mkfifo` on Unix (so the JDK's exit-time pipe drain cannot hold
  the caller), kills the worker's process tree from a `ProcessHandle`
  snapshot, and documents what that misses in `docs/LIMITS.md`. `scripts/core_pin.py` now generates
  `java/src/main/java/org/jacquardlang/host/Compat.java` and keeps the Java kit
  copy in sync, and CI runs the offline tests on Java 21 and 25 and the live
  tests against the pinned Core.
- Java SDK (`org.jacquardlang.host.sdk`): prepares and caches checked programs
  with Core's public commands, maps Java values through explicit `Codec`
  objects (derived from records, enums, and sealed interfaces through their
  declarations, and `Dynamic` for untyped code), binds typed handlers to exact
  operation identities, checks arguments against the checked signature and
  the selected limits before a worker starts, and keeps each invocation's raw
  evidence. Encoding is bounded by the selected limits as it goes, so a small
  Java value that denotes a huge one is refused after about one frame's work.
  Every argument value is measured under one shared budget before its type
  is checked, whatever codec built it. `mapBatches` runs explicit batches,
  validating every batch first without keeping any batch's encoding, and
  observing the `Cancel` and the interrupt between batches and after the
  last; as in the other SDKs, validation has no deadline. Preparation takes
  a `Cancel` and stops on the caller's interrupt; it reads only regular
  files, at most 16 MiB of sources, 512 MiB of Core executable, and 100,000
  entries and 256 MiB under the prelude or a cache entry, observing stops as
  it reads. A cache entry keeps Core's raw `check` and `hash` output, and
  every binding is derived from it again on load, so a changed `program.json`
  is refused; on POSIX, a cache root or entry that another user owns or that
  others may write is refused, and new entries are owner-only. On Linux with
  a `setsid` at `/usr/bin` or `/bin`, each Core command runs in its own
  process group, which is killed when the command exits and on every stop,
  and a `setsid` that cannot exec Core is reported as a start failure;
  elsewhere its recorded descendants are killed. Preparation takes at most
  1,024 sources, refused from the list's size; a failed preparation's
  cleanup is bounded and ends at a stop, leaving an unpublished staging
  directory, which nothing removes later; a preparation that finishes as a
  stop arrives reports the stop; and a symbolic
  link to the cache root is followed to the directory it names. A function
  whose signature nests types deeper than 64 levels, the protocol's value
  depth, is left unbound with a binding failure naming the limit, while the
  program's other functions still bind. The four example projects run through the
  `JacquardExamples` command. The low-level adapter gains `carrier.Command`,
  the bounded runner the SDK uses for Core's commands, which shares the
  worker's output FIFOs, and `HandlerContext.of` for calling a handler
  outside an invocation. CI runs the SDK's offline and live tests and the
  four projects.
- .NET adapter: an independent project in `dotnet/` (package
  `JacquardLang.Host`, targeting .NET 8 and tested on .NET 8 and 10) with no
  runtime dependencies beyond the .NET base class library. It provides strict
  codecs that parse integers exactly from their text, the session state
  machine, a bounded process carrier for `jacquard host worker`, and a `Host`
  with a closed operation registry. It carries the pinned kit as embedded
  resources and replays all 21 recordings. .NET cannot start the worker in its own process group or
  interrupt a pipe read or write, so the carrier never calls one that can
  block: it waits on the C library's `poll(2)` and runs workers on Linux and
  macOS only. It kills the worker's process tree with `Process.Kill(true)`,
  reports a descendant that keeps the pipes open after the worker exits as a
  carrier loss, and documents what it misses in `docs/LIMITS.md`.
  `scripts/core_pin.py` now generates `dotnet/src/JacquardHost/Compat.cs` and
  keeps the .NET kit copy in sync, and CI runs the offline tests on .NET 8 and
  10 and the live tests against the pinned Core.
- .NET SDK (`JacquardLang.Host.Sdk`): prepares and caches checked programs
  with Core's public commands, maps .NET values through explicit `Codec<T>`
  objects (records through field readers and a constructor function,
  enumerations, type hierarchies, `Option<T>`, `Unit`, lazy codecs for
  recursive types, and `Dynamic` for untyped code) without reflection over
  members (enumeration codecs read `Enum` names and values, and hierarchy
  codecs check case types with `Type`), binds
  handlers to exact operation identities with the `HandlerContext` and its
  `CancellationToken`, checks arguments against the checked signature before a
  worker starts, and keeps each invocation's raw evidence. `MapBatches` runs
  explicit batches. There is no `decimal` mapping: money crosses as integer
  minor units, and `decimal`, `ulong`, `nuint`, `char`, `float`, and the
  other numeric types with no Jacquard type do not compile as arguments.
  Caller names over 4,096 chars are refused before they are looked up.
  Core's commands run on the caller's thread through `poll(2)`, bounded in
  time and output, and on Linux through `setsid`, whose process group is
  killed when a command exits or is stopped; without `setsid`, descendants
  are recorded from `/proc` within entry and byte caps, and on a stop Core
  is frozen with `SIGSTOP` before the last record and the cleanup ends
  within 750 ms. Only the cache's owner may write it: the root, each entry,
  and each file must be owned by the current user and not group- or
  world-writable, every directory above the root must be owned by the user
  or root and not group- or world-writable unless it is sticky or the group
  is the user's private group (Debian OpenSSH's rule, which looks for
  another account with that primary group in the password database, listed
  here by `getent passwd` in a separate process, bounded to 5 seconds and
  16 MiB, stopped by the token, and refused on any failure or malformed
  entry; with stricter conditions, so a clone made with a `umask` of 002
  under user-private groups is accepted), a refusal names
  every such directory and creates nothing, and the root's device and inode
  are checked again before each use, each worker a prepared program starts
  included. Replacement by the same user is not covered.
  Every binding is derived again from Core's cached output on load, and a
  preparation during which the Core executable or prelude changed is
  refused. `Invocation.Save` refuses a target directory another user could
  write, by the same directory rule, writes a new owner-only file through one
  exclusively created handle, flushes it, checks the file at its path is the
  one written, and renames it over the target.
  The four example projects run through a `JacquardExamples` command in
  `dotnet/examples/`, and CI runs them and the SDK's live tests against the
  pinned Core.

## 0.2.0 — 2026-10-25

The first jacquard-host release: Python, TypeScript, and Go adapters and SDKs
for Core's experimental `jacquard-host-v0` protocol, pinned to
`jacquard-core-0.3.0`. Tags: `jacquard-host-0.2.0` (repository and Python/npm
assets) and `go/v0.2.0` (Go module). Packages are attached to the
[GitHub release](https://github.com/jbwinters/jacquard-host/releases/tag/jacquard-host-0.2.0);
nothing is published to a package registry. Earlier work was never tagged, so
this entry covers the repository's whole history.

- `scripts/core_pin.py` validates the repository and kit paths in the pin,
  refuses malformed kit decisions and non-release tags before any network
  fetch, and regenerates files through staged renames. A failed adoption
  restores the pin, kit, and generated files and raises the original error; any
  file it cannot restore or temporary file it cannot remove is reported as a
  warning when it happens.
- Go SDK: a constructor may map to a record or an enum, not both. `nil` at a
  checked non-optional data type, and a registered empty record at a unit
  position, are refused or kept distinct locally. An unsupported handler
  signature is a binding error naming the problem. Version checks and prelude
  discovery run once per key, and waiters observe their own context. The Go
  host owns all three worker pipes and shortens the exit grace after
  cancellation.
- The Go worker's stderr is now a pipe the host owns. After the worker exits, a
  descendant that still holds it is stopped with the process group on Linux;
  other platforms only stop reading the pipe. A clean
  worker that is merely slow to exit no longer triggers a group kill. A deadline
  that expires during the exit grace is reported as the deadline. A descendant
  that escaped the group no longer holds the host.
- The Go and TypeScript adapters decode boundary descriptors in Core's order,
  with identity checked before the count, so error codes agree with Core and
  Python. TypeScript no longer coerces evidence fields with `String()`. Python
  checks evidence collections against `max_effects`, `max_operations`, and
  `max_effect_requests`. The Go kit replay treats absent expectations as the
  TypeScript replayer does.
- All three adapters now count an outcome frame's evidence types and result
  value against one `max_value_nodes` budget, as Core encodes it; previously the
  result started a fresh budget. The Python adapter also validates the evidence's
  interface types, as the TypeScript and Go adapters already did.
- Add the Go SDK (`go/jacquard`): cached, verified preparation; native Go type
  mapping with registered records, enums, and unions; reflection-bound
  services with context cancellation; explicit batching; raw evidence and
  inspection files; and all four example projects (`go/cmd/jacquard-examples`)
  with explicit fulfillment recovery.
- Add an independent Go adapter (`go/`, standard library only): strict v0
  codecs, the host session state machine, a bounded process carrier, a
  low-level `Host` with a closed operation registry, context cancellation, and
  an embedded kit replayer. It replays all 21 pinned recordings against Core.
- Pin all adapters to the Jacquard Core `jacquard-core-0.3.0` release (was a
  development commit reporting 0.2.0). The host protocol and recordings are
  unchanged; only the kit's recorded Core version differs.
- Record the pin once in `core-pin.json`. `scripts/core_pin.py` generates the
  Python and TypeScript constants and the compatibility table, checks for drift
  in CI, installs the checksum-verified release binary, reports newer releases,
  and adopts a new release tag. CI installs the release instead of building Core.
- Add an independent TypeScript/Node.js adapter and SDK, async services, explicit
  record/union codecs, lossless bigints, cached preparation, and npm tarball
  installation checks. Both adapters replay the same 21 pinned recordings.
- Add `Runtime.prepare`, cached checked programs, scoped service binding, and
  native Python calls with explicit dataclass and enum mappings.
- Validate arguments and required handlers before starting a worker. Provide
  consistent exceptions, readable invocation summaries, and saved raw evidence.
- Add explicit batching for independent pure list operations, without retries.
- Include a quickstart and runnable triage, planning, and fulfillment projects.
  Fulfillment demonstrates a transactional outbox and explicit reconciliation.
- Validate outgoing frame depth and bounds; preserve low-level protocol access
  and the existing pinned v0 fixtures.
- Package the Python adapter, typing marker, application sources, conformance
  kit, and installed command entry points; bound worker writes and stderr
  collection, enforce carrier deadlines, and verify wheel and source-archive
  installations outside the checkout.
- Earlier, unversioned groundwork: the Python adapter, which pins Core's v0
  conformance kit and replays it, invokes checked terms, and serves their
  operations; a bounded serial loopback socket host; an HTTP/1.1 subset parser,
  router, and renderer written in Jacquard; one named, host-owned SQLite study
  persistence operation; the synthetic trial assembled end to end; and
  integration evidence, an authority inventory, measurements of serial
  execution with the C4 concurrency requirements, the Core requirements report,
  and the record of Core's published host-boundary evidence pack.

The protocol remains experimental v0. Core is installed separately; automatic
binding generation still awaits Core's structured host interface exporter.
