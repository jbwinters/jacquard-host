# Where serial execution costs, and what C4 would need

Roadmap H2.2 (Task Master 9). This is a measurement and a requirements list
for Core's SC.18 (the opt-in host-scheduler contract), not an implementation:
no event loop, threads in the host, actors, or async filesystem work were
added. The numbers come from `scripts/measure_serial.py` against the pinned
Core build on 2026-09-12 (Linux, Python 3.14, one machine under normal load)
and are recorded in [measurements-serial-2026-09-12.json](measurements-serial-2026-09-12.json).
They are one machine's numbers; the shape of the results, not the digits, is
the finding.

## What was measured

The synthetic trial (`trial.serve`) behind the serial socket host, one fresh
worker per request, driven by client threads only.

| Measurement | Result |
|---|---|
| Spawn a worker, negotiate, shut down (no evaluation) | about 18 ms median |
| `GET /trial`, one client at a time | about 45 ms per request; 12 requests in 0.56 s (21.6/s) |
| `POST /response` (parse, `save-response`, SQLite commit), one client | about 63 ms per request; 12 in 0.78 s (15.5/s) |
| 12 `GET` clients connecting at once | same 45 ms of work each, but request wall time median 1.2 s, p90 1.5 s, max 2.1 s |
| 12 `POST` clients connecting at once | median 1.3 s, p90 2.0 s, max 3.2 s |

Every request in every scenario was served with the expected status; nothing
was refused or reset, because the kernel backlog absorbed the burst.

## Where serial execution fails

1. **Head-of-line blocking is the only failure mode observed.** Per-request
   work is small and constant; wall time under a burst is queue wait, and it
   grows linearly with the burst size (about 0.1 s per queued request here).
   A single slow request (a handler waiting on the study store's busy
   timeout, up to 1 s; a large head parse, about 6.5 s at the ceilings) adds
   its whole duration to every queued client.
2. **Per-request process spawn is not the bottleneck.** Spawn plus
   negotiation is under 20 ms; the evaluation and the SQLite commit dominate.
   Pooling workers would not change the shape above.
3. **The accept backlog is the only admission control.** Beyond the kernel
   backlog, clients see a refused or reset connection with no host record.
   The host records only accepted connections.
4. **Cancellation is coarse.** The host can only kill the worker at its
   deadline; it never sends `cancel`, because nothing in the serial model
   waits on a host-side readiness condition that could time out
   independently of the worker.

## Blocked resources and readiness

Of everything the trial reaches, exactly one operation blocks on an outside
resource: `study.save-response` (SQLite `BEGIN IMMEDIATE`, at most
`busy_timeout` seconds). Parsing, routing, and rendering are pure compute in
the worker. So the set of operations that would need readiness suspension
under a scheduler is `{save-response}`; nothing else in H1 would benefit.

## Thresholds for adopting C4

Serial execution is sufficient while all of these hold; C4 becomes a
requirement when any one is exceeded for a real application, measured the
same way:

| Threshold | Serial is enough while |
|---|---|
| Sustained demand | under 10 requests per second for `GET`-shaped work, under 8 for `POST`-shaped work (half the measured serial throughput) |
| Burst tolerance | p90 wall time under 1 s at the application's expected burst size (here: bursts of 8 or fewer) |
| Slow operations | no configured operation can block longer than 1 s (the busy timeout), so no queued client waits more than that on another client's outside action |
| Backlog loss | refused connections at the backlog are acceptable to the application |

## Requirements for Core SC.18

If C4 is adopted, the host needs these from the contract, in this order of
importance:

1. **Readiness suspension only for named operations.** A way for the host to
   answer an `effect_request` with "not ready, resume me later" for the
   operations it declares blocking (here only `save-response`), leaving pure
   invocations untouched. Everything else stays serial per invocation.
2. **Scheduler and readiness trace.** For each invocation: the ordinal of
   every request, when it was suspended and resumed, and by which host
   event, so that a multi-invocation run can be replayed as the existing
   per-invocation transcripts can. The trace must be host-owned evidence,
   separate from Core evidence as today.
3. **Replay.** The existing deterministic transcript per invocation is
   sufficient input; C4 must preserve it and add only the interleaving.
4. **Platform.** Linux first (this repository's CI and evidence); macOS
   development only; no Windows claim. Readiness must be expressible with
   `selectors`-style file-descriptor readiness so no thread is required in
   the host.
5. **Shutdown.** Drain: stop accepting, finish or cancel in-flight
   invocations with `cancel`/`host_shutdown` and an honest completion claim,
   then exit. The current host has the first and last steps only.
6. **Leaks.** The descriptor-count and process-reaping invariants the serial
   host proves per request must hold across a scheduled run of N
   invocations, and the study store's connection must survive suspension.

## Non-claims

No throughput or latency claim is made beyond the shape described; the
numbers are one machine's. Nothing here changes `LIMITS.md`'s serial-execution
row; it supplies the "measured need" that row asks for, and the measurement
says the need has not yet arrived for the synthetic slice.
