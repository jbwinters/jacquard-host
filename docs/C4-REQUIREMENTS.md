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
worker per request, driven by client threads only. Connect wait (until the
kernel accepts the connection) is reported separately from the wait after
connect, and every burst runs twice: with the host's default accept backlog
of 1, and with a backlog at least as large as the burst.

| Measurement | Result |
|---|---|
| Spawn a worker, negotiate, shut down (no evaluation) | about 18 ms median |
| `GET /trial`, one client at a time | about 46 ms per request; 12 requests in 0.55 s (21.8/s) |
| `POST /response` (parse, `save-response`, SQLite commit), one client | about 68 ms per request; 12 in 0.83 s (14.5/s) |
| Bursts of 4, 8, 12, and 40 `GET` clients with a matching backlog | connect wait under 1 ms for every client; request wall grows by about 45 ms per queued request: max 0.18 s, 0.36 s, 0.54 s, 1.84 s |
| Burst of 12 `POST` clients with a matching backlog | max 0.82 s, about 68 ms per queued request |
| Bursts of 4, 8, 12 `GET` clients with backlog 1 | clients beyond the backlog wait 1 s, then 2 s, before the kernel accepts them (SYN retransmit), so wall times step to 1.09 s, 1.35 s, 2.17 s while the wait after connect stays at the 45 ms line |
| Burst of 40 `GET` clients with backlog 1, clients giving up after 10 s | 35 served; 5 clients completed a handshake on a retransmit but were never accepted by the host and timed out; wall 13.1 s for about 1.8 s of work (in the recorded run those five report their whole wait as connect wait; the script now separates the handshake time) |

Every accepted request in every scenario was served with the expected
status.

## Where serial execution fails

1. **Admission before the queue.** With the socket host's default accept
   backlog of 1, the kernel drops the SYNs of a burst beyond the backlog
   (this Linux does not reset them: `tcp_abort_on_overflow` is 0). Those
   clients retransmit after 1 s, then 2 s, 4 s, and so on, so the host sits
   idle while clients back off: 40 clients took 13 s for under 2 s of work
   and five gave up (they completed a handshake on a retransmit, were never
   accepted by the host, and timed out). The failure mode is starvation, not
   refusal, and no host record exists for a client the host never accepted.
2. **Head-of-line wait once admitted.** With a backlog that admits the
   burst, wall time is queue wait growing linearly with position: about
   45 ms per queued `GET`, about 68 ms per queued `POST`. A single slow
   request (a handler waiting on the study store's busy timeout, up to 1 s;
   a maximal head parse, about 5 s) adds its whole duration to every client
   behind it.
3. **Per-request process spawn is not the bottleneck.** Spawn plus
   negotiation is under 20 ms; evaluation and the SQLite commit dominate.
   Pooling workers would not change the shape above.
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
same way. The demand margin is a chosen rule (half the measured serial
throughput); the burst figures follow from the measured per-request cost.

| Threshold | Serial is enough while |
|---|---|
| Accept backlog | the host is started with an accept backlog at least as large as the expected burst; with backlog 1 every burst above 3 clients puts some client past 1 s, and large bursts starve (finding 1) |
| Sustained demand | under 10 requests per second for `GET`-shaped work, under 7 for `POST`-shaped work |
| Burst tolerance | p90 wall time under 1 s at the expected burst size: about 20 queued `GET` or 14 queued `POST` requests at the measured cost (measured: 12 clients p90 0.46 s `GET`, 0.68 s `POST`; 40 clients p90 1.66 s `GET`) |
| Slow operations | no configured operation can block longer than 1 s (the busy timeout), so no queued client waits more than that on another client's outside action |
| Unaccepted clients | starvation of clients beyond the backlog is acceptable to the application, or the backlog is sized so it does not occur |

## Requirements for Core SC.18

If C4 is adopted, the host needs these from the contract, in this order of
importance:

1. **Readiness suspension only for named operations, host-side.** No new
   wire message is needed under the frozen contract: each worker still has
   at most one outstanding request (HP0.1), and a host may leave that
   request unanswered while it serves other workers. What C4 needs is the
   host scheduling several workers and deferring only the operations it
   declares blocking (here only `save-response`); pure invocations stay
   untouched. Core's part is the evidence below, not a protocol change.
2. **Scheduler and readiness trace.** For each invocation: the ordinal of
   every request, when it was suspended and resumed, and by which host
   event, so that a multi-invocation run can be replayed as the existing
   per-invocation transcripts can. The trace must be host-owned evidence,
   separate from Core evidence as today.
3. **Replay.** The existing deterministic transcript per invocation is
   sufficient input; C4 must preserve it and add only the interleaving.
4. **Platform.** Linux first (this repository's CI and evidence); macOS
   development only; no Windows claim. As a host design rule, readiness
   will be expressed with file-descriptor readiness (`selectors`) so no
   thread is required in the host; Core need only keep every worker's
   frames on its own pipes.
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
says the need has not yet arrived for the synthetic slice. The socket host's
default accept backlog of 1 is left as it is: whether a serial host should
default to a larger backlog (a kernel FIFO in front of the serial loop) or
keep 1 as deliberate admission control is an owner decision recorded in the
capsule; `TrialApp.server(backlog=...)` exposes the choice.
