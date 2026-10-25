# Changelog

## 0.2.0 — unreleased

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

The protocol remains experimental v0. Core is installed separately; automatic
binding generation still awaits Core's structured host interface exporter.

## 0.1.0

- Package the Python adapter, typing marker, application sources, conformance
  kit, and installed command entry points.
- Bound worker writes and stderr collection, enforce carrier deadlines, and
  verify wheel and source-distribution installations outside the checkout.
