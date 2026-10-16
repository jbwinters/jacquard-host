# Requirements reported to Jacquard Core

Version: `jacquard-host-core-requirements-v1` (2026-09-12), from implementing
H0.1 through H1.3 against Core commit `61575c7` (kit) with the worker of
`128fbaf`. Each item names the limitation observed, the host-side workaround
in place, and the Core change that would remove it. Items are requests, not
demands; the host keeps working without them.

| # | Observation | Host workaround today | Core change requested |
|---|---|---|---|
| 1 | `jacquard-host-v0` has no bytes value; raw request bytes cross as Latin-1 `Text`, doubling UTF-8 size and capping the host's request ceiling at 131072 bytes (`max_text_bytes` 262144) | Latin-1 mapping, documented in LIMITS.md | a frozen `bytes` boundary value in a later protocol version |
| 2 | No import syntax, and the pinned worker predates the store-reopen repair (Core's local planning item 241, reviewed but unpublished at the time of writing), so several first-party files cannot populate one store separately | the host assembles `http.jac`, `study.jac`, `trial.jac` into one source per work directory | merge 241; document a multi-file `jacquard run --store` recipe |
| 3 | `jacquard check --print-sigs` prints a one-item tuple type as `(Int)`, indistinguishable from `Int` | the signature parser documents the gap; such interfaces are built by hand | print `(Int,)` as the surface syntax does |
| 4 | `jacquard hash` prints a type and its same-named constructor as two lines without a kind | the host keeps the first (the type) and exposes the full list | print the member kind, or add a `jacquard names` command listing name, kind, and hash for a store |
| 5 | Prelude type identities (`Int`, `Text`, `Hash`, `Option`, `Bool`) must be recovered by hashing prelude files | `hash_file` on `01-prim.jqd` and friends | same as 4 |
| 6 | `Session.request` in Core matches registry bindings by operation hash only, not `(effect, operation)` | none needed | pin the matching rule in the spec or match on both |
| 7 | Pending decision `noncanonical-target-hash` (vector E1603, shipped E1601) | the adapter reproduces the recorded divergence | a reviewed resolution |
| 8 | No frozen `effect_failure` pair describes "the outside action completed but its result cannot be represented" | reported as `outside_failure`/`failed`, documented | a category for completed-but-unrepresentable, or guidance |
| 9 | The protocol has no host-side handler deadline; a slow handler holds the worker | documented as a host limit; deadline kill is host-owned evidence | none required; confirms HP0.8 |
| 10 | Resolved: Core's Task 206 evidence pack is published at `49ed2d0` | COMPATIBILITY.md pins both the kit and the pack | none; the provisional v0 and second-adapter limits remain |

## Authority requirements (for Core task 207)

For the trial slice the closed `once` registry plus Core's E1606 preflight
refusal was sufficient. The single outside action was `study.save-response`,
refused before action on invalid input and never retried on unknown
completion. No path, host, port, or database-row containment was needed or
claimed, and the host requests none from Core for this slice. What the host
does need from Core is unchanged from the frozen contract: exact identities,
the E16xx classification, and the separate evidence schemas, all of which
held in practice.
