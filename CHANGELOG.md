# Changelog

## 0.2.0 — unreleased

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
