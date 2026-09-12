# Host authority inventory

What the host actually does outside Jacquard, recorded for roadmap H2.0 and
for Core's authority work (Core task 207). Two columns matter: whether the
action is triggered by running Jacquard code or is host startup
configuration, and what authority it uses.

## Actions triggered by Jacquard

| Action | Trigger | Authority used | Bound |
|---|---|---|---|
| `study.save-response` | the running term reaches the `Study.save-response` operation | one SQLite file under the host's work directory, opened by the host at startup | validated inputs; one transaction; idempotent; no retry on unknown completion |

That is the complete list. No other outside action can be reached from a
running term: the registry is closed, an unconfigured operation is refused by
Core before any request leaves the worker (E1606), and the adapter dispatches
by exact identity only.

## Host startup configuration (not reachable from Jacquard)

| Action | Where | Authority used |
|---|---|---|
| Spawn `jacquard host worker --store DIR` per invocation | `jacquard_host.worker` | process creation; three pipes; a temp file for bounded stderr |
| Populate a Core store from the assembled first-party source | `jacquard_host.trial.TrialApp` (once per work directory) | `jacquard run FILE --store DIR`; filesystem write under the work directory |
| Resolve identities | `jacquard_host.identities` | `jacquard hash` and `jacquard check --print-sigs` on first-party files |
| Bind and accept | `jacquard_host.socket_host` | loopback TCP only (`127.0.0.1` or `::1`); non-loopback is refused at construction |
| Open the study database | `jacquard_host.study` | one SQLite file; WAL when the filesystem allows; no other filesystem access |
| Kill a worker at the deadline | `jacquard_host.worker` | signal to the child it spawned |

## Not used

- No outbound network connection of any kind.
- No secret, credential, token, or environment lookup beyond `JACQUARD_BIN`,
  `JACQUARD_PRELUDE`, and `TMPDIR`.
- No filesystem access outside the work directory, the Core prelude and
  store paths given at startup, and the temp directory for stderr capture.
- No nested policy: the host has no handler chain of its own; every policy
  decision reachable from Jacquard is inside Jacquard code or Core's checker.
- No `cancel` frame is sent by the adapter today; a deadline ends the worker
  by signal and is recorded as host-owned carrier loss, never as a Core
  verdict.

## Evidence separation

Core evidence (target, interface, capabilities, request order, observations)
is returned unchanged in `InvocationResult.evidence`. Host-owned evidence
(`InvocationResult.host_evidence`, `Served` records of the socket host) is
kept beside it under its own schema name and never merged into Core's.
