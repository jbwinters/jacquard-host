# Roadmap

Status: working integration plan after Jacquard Core 0.2. Task Master mirrors
these tasks locally. Cross-repository gates are written explicitly because
Task Master cannot enforce dependencies in another repository.

## Sequence

### H0 — Contract consumer

1. **H0.0 Bootstrap and pin the boundary.** Establish repository ownership,
   trust assumptions, limitations, compatibility, CI, and development rules.
2. **H0.1 Python conformance adapter.** Consume the released Core protocol and
   fixtures through a bounded, serial, fail-closed adapter. This cannot begin
   until Jacquard Core Tasks 203–206 publish the contract and evidence.

### H1 — Minimal serial integration

3. **H1.0 Bounded serial socket host.** Bind loopback, accept one request at a
   time, enforce host resource ceilings, and pass raw bounded bytes through
   the host boundary.
4. **H1.1 HTTP parser and router in Jacquard.** Support the deliberately narrow
   HTTP/1.1 subset and reject every unsupported or ambiguous form.
5. **H1.2 Named SQLite persistence.** Expose only the versioned operation needed
   to save a synthetic response; do not expose raw SQL or filesystem access.
6. **H1.3 End-to-end synthetic slice.** Serve one deterministic synthetic
   trial, validate one response, persist it, and return a receipt. This is not
   the real readability application or participant collection.

### H2 — Evidence and independent implementation

7. **H2.0 Evidence, limits, and Core requirements.** Publish reproducible
   integration evidence and a concrete authority report consumed by Core Task
   207 and then Task 202.
8. **H2.1 Rust adapter.** Reimplement the same pinned fixtures independently;
   language-specific behavior is a defect, not a compatibility feature.
9. **H2.2 C4 requirements.** Measure where serial execution fails and supply
   the concrete scheduling, readiness, cancellation, trace, platform, and leak
   requirements for Core Task 208 and, only afterward, Task 139.

## Repository handoff

The real readability application moves to its own repository before it gains
participant data, public deployment, authentication, operational retention,
or research publication duties. The reusable host contract and adapters stay
here; language changes and their evidence return to `jacquard-lang`.
