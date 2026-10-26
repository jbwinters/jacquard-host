# Changelog

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
