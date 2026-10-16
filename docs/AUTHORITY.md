# Host authority inventory

What the synthetic trial host actually does outside Jacquard, recorded for
roadmap H2.0 and Core's authority work (Core task 207). Two columns matter: whether the
action is triggered by running Jacquard code or is host startup
configuration, and what authority it uses.

## Actions triggered by Jacquard

| Action | Trigger | Authority used | Bound |
|---|---|---|---|
| `study.save-response` | the running term reaches the `Study.save-response` operation | one SQLite file under the host's work directory, opened by the host at startup | validated inputs; one transaction; idempotent; no retry on unknown completion |

That is the complete list. No other outside action can be reached from a
running term: the registry is closed, an unconfigured operation is refused by
Core when reached (E1606), before that operation is dispatched, and the adapter
dispatches by exact identity only. Previously configured operations in the same
invocation can already have completed: a missing later binding is not a
transactional preflight guarantee. The multi-operation fulfillment experiment in
`docs/development/python-projects/` demonstrates this case.

## Host startup configuration (not reachable from Jacquard)

| Action | Where | Authority used |
|---|---|---|
| Spawn `jacquard host worker --store DIR` per invocation | `jacquard_host.worker` | process creation; three pipes; a reader thread drains stderr and retains at most 65536 bytes in memory; the parent's environment is passed to the child with `JACQUARD_PRELUDE` and `TMPDIR` overridden where the host sets them |
| Populate a Core store from the assembled first-party source | `jacquard_host.trial.TrialApp` (once per work directory) | `jacquard run FILE --store DIR`; filesystem write under the work directory |
| Resolve identities | `jacquard_host.identities` | `jacquard hash` and `jacquard check --print-sigs` on first-party files and on the Core prelude file `01-prim.jqd` |
| Bind and accept | `jacquard_host.socket_host` | loopback TCP only (`127.0.0.1`, `::1`, or the name `localhost`); anything else is refused at construction |
| Open the study database | `jacquard_host.study` | one SQLite file; WAL when the filesystem allows; no other filesystem access |
| Kill a worker at the deadline | `jacquard_host.worker` | signal to the child it spawned |

The optional Python SDK adds startup-owned preparation: public Core
check/hash/run commands, source snapshots, runtime/prelude digest reads, and
verified cache files under a caller-configured directory (default
`.scratch/jacquard-host`). Explicit `Invocation.save(path)` writes an operator
inspection, including raw argument/result frames, to the chosen path. These
are host/application actions, not operations reachable by passing a path to
Jacquard code. Applications own their mapped Python classes and service
objects. The separate SDK inventory example's SQLite and receipt-deduped
delivery authority is described in `examples/python_sdk/README.md`.

## Not used

- No outbound network connection of any kind.
- No secret, credential, token, or environment lookup beyond `JACQUARD_BIN`,
  `JACQUARD_PRELUDE`, and `TMPDIR`; child processes inherit the parent's
  environment plus overrides of `JACQUARD_PRELUDE` and `TMPDIR`.
- No filesystem access outside the work directory, the Core prelude and
  store paths given at startup, the package's own `src/jacquard_host/data/jacquard/` sources it
  assembles, temporary directories for kit-store construction, and a report path the
  operator names on the kit replayer's command line.
- No nested policy: the host has no handler chain of its own; every policy
  decision reachable from Jacquard is inside Jacquard code or Core's checker.
- No `cancel` frame is sent by the adapter today; a deadline ends the worker
  by signal and is recorded as host-owned carrier loss, never as a Core
  verdict.

## Evidence separation

Core evidence (target, interface, capabilities, request order, observations)
is returned unchanged in `InvocationResult.evidence`. Host-owned evidence is
kept beside it and never merged into Core's: `InvocationResult.host_evidence`
carries the schema name `jacquard-host-python-evidence-v0`, and the socket
host's `Served` records are plain host records with no schema field.
