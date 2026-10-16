# Python host application experiments

This is the recorded **0.1.0 baseline**. The same application rules now have
[polished SDK clients](../../../examples/python_sdk/README.md), with cached preparation, native
dataclasses, checked services, and an outbox-backed inventory example. The
baseline `results.json` retains its original version and source digests; local machine paths are replaced with descriptive placeholders. On
0.2 the low-level runner also accepts local outgoing-depth refusal, which
replaces the original Core fatal for the same oversized input.

Three small but complete command-line applications built against the installed
`jacquard-host` 0.1.0 wheel, using the pinned Core worker. They use synthetic
inputs, a local SQLite file, and an in-process dispatch simulation. No network,
participant data, external API, extra dependency, or new language contract is
involved. The library implementation was left unchanged for this evaluation.

- **Inventory fulfillment:** validate multi-line orders in Jacquard, query
  stock through typed callbacks, reserve inventory in a Python-owned SQLite
  transaction, publish a simulated dispatch, and return a content-hash receipt.
  Reordered identical requests are idempotent; conflicting reuse of an order ID
  is a business rejection. Duplicate checks precede stock checks, so an order
  still repeats correctly after its stock is exhausted.
- **Work planner:** choose the maximum-value subset of up to six jobs across
  one to three capacity-limited slots, subject to job deadlines. The exhaustive
  Jacquard search produces explicit assignments; the exercise checks both their
  feasibility and optimum score against a separate Python enumerator.
- **Incident triage:** translate ordinary JSON incidents into nested nominal
  values with booleans and optional owners, apply an explicit policy in
  Jacquard, and return teams, urgency, and multiple reasons. The command line
  uses batches of eight under the current protocol's depth ceiling.

These are application experiments, not production services. In particular,
fulfillment deliberately separates reservation from dispatch so the exercise
can expose partial completion. Its dispatch sink is an in-memory simulation;
there is no durable outbox or delivery guarantee.

## Run them

From the `jacquard-host` repository root, install a regular wheel rather than
using `PYTHONPATH=src`. To keep the experiment installation local:

```sh
mkdir -p .scratch/wheels
python3 -m pip wheel . --no-deps --wheel-dir .scratch/wheels
python3 -m pip install --no-index --no-deps --target .scratch/project-library \
  .scratch/wheels/jacquard_host-0.2.0-py3-none-any.whl
export PYTHONPATH="$PWD/.scratch/project-library"
```

Set `JACQUARD_BIN` to the pinned worker executable and `JACQUARD_PRELUDE` to its
matching prelude. See [setup](../../SETUP.md) and
[the compatibility pin](../../COMPATIBILITY.md).

```sh
# Configure JACQUARD_BIN and JACQUARD_PRELUDE using docs/SETUP.md.

python3 docs/development/python-projects/app.py planner examples/planner/input.json
python3 docs/development/python-projects/app.py triage examples/triage/input.json
python3 docs/development/python-projects/app.py fulfillment examples/fulfillment/input.json
```

Run the last command again to observe `duplicate-order` and the same receipt.
Change the JSON files to try other inputs. SQLite state lives under
`.scratch/python-projects/`; using a different `--workdir` starts a fresh
inventory. Each CLI launch prepares a temporary Core store; it does not pretend
to provide a persistent artifact cache. Within the exercise, each store is
prepared once and reused for all calls to that application.

To inspect a post-commit failure, use a fresh work directory:

```sh
python3 docs/development/python-projects/app.py fulfillment examples/fulfillment/input.json \
  --workdir .scratch/dispatch-failure --fault publish-failed
```

That command exits 1 with E1613 **and a durable booking**. An explicit later run
without the fault returns the existing receipt; it does not repair dispatch.
There is no automatic retry. A business rejection such as insufficient stock
is a typed successful invocation, so its CLI exit status is 0; inspect the
returned tag to distinguish it from an accepted order.

Run the reproducible exercise, including the independent oracles, boundary
probes, and timing samples:

```sh
python3 docs/development/python-projects/run.py --report .scratch/python-projects/results.json
```

The runner refuses an accidental `PYTHONPATH=src` import. It creates temporary
stores beside its report and removes them on exit. The report records library
and runtime identity, application source digests, every named case's observed
result, and measurements. `results.json` alongside this README is one recorded
run, not a performance promise. It records 66 named cases, including expected
failures, 27 planner problems checked against an independent exhaustive oracle,
and a 63-row triage matrix checked against an independent Python policy oracle.
All three CLI entry points were also exercised, including inventory reuse
across two separate processes.

## What feels right

**Business policy reads well in Jacquard.** The planner's constraint checks and
triage's typed reasons stayed small. Named fields helped review the six-field
incident record. Business rejections are ordinary constructors, clearly
separate from carrier errors. Equal-score planning repeats returned identical
values and Core evidence.

**Calling Python is straightforward once the identities are known.** Exact
operation identities bind directly to normal functions. The successful
multi-line order produced the expected five requests in source order: booking
status, two stock queries, reservation, and dispatch. Wrong-typed host results,
refused authority, and unknown completion produced the expected distinct codes.
The host did not repeat an ambiguous reservation.

**The low-level evidence is useful.** When dispatch failed after reservation,
the booking receipt remained in the earlier `effect_ok` host frame even though
the final result had no value. The SQLite record and ordered operation frames
made reconciliation possible. This is stronger than returning only a generic
exception or an opaque failed job identifier.

**A whole decision is a useful invocation unit.** The bounded planner solved
actual constrained choices without any callback machinery. Passing the entire
small problem into one checked term felt natural. The triage workload also
benefited substantially from batching; the recorded timings are in
[results.json](results.json).

## What feels wrong

**The binding/setup burden is too high for a polished Python experience.**
`support.py` contains roughly 100 lines of application glue: populate a store,
run three hash commands and one signature command, distinguish type and
constructor identities, construct booleans/options/lists, and decode results.
Cold preparation took roughly 2.5–2.8 seconds per project on this machine. The
same-named type/constructor case requires using the complete hash output rather
than only `hash_file`. Python lists and booleans are refused until explicitly
translated into prelude constructors. `Values` is a convenience for these known
declarations, not a general schema-aware binding generator.

**The practical collection limit is surprisingly small.** A `List Int` with 30
items succeeded and 31 failed with E1602. For the incident shapes used here,
28 items succeeded and 29 produced an error outcome while encoding the result.
Linked `Cons` values expand into nested JSON objects and arrays, so the depth
ceiling arrives well before the advertised 1024 collection-item ceiling.
An accepted input therefore does not imply its derived output will fit.
Eight-item batching works for these fixed record shapes; it is not a universal
safe batch size. A larger collection representation needs a reviewed Core
protocol change, not a host-only relaxation of the frozen limits.

**Every tiny call pays for a fresh process.** The empty-call median was about
18 ms. Processing 24 incidents individually took roughly 0.55 seconds versus
roughly 0.09 seconds in three batches of eight in the recorded runs. These are
local observations with filesystem caches already warm. They explain why
coarse calls feel better, while the depth ceiling limits how coarse they can
be. They do not establish concurrent throughput or a scheduler requirement.

**Failure presentation requires too much branching.** A Core failure has
`diagnostics` and `primary_code`; a local refusal instead puts its code and
detail in `host_evidence`, with no primary diagnostic. A missing worker raises
`FileNotFoundError` rather than returning `InvocationResult`. The README's
simple `diagnostics[0]` failure-rendering expression raises `IndexError` for
local refusals and carrier loss. A common presentation helper could unify
these paths while keeping Core and host evidence distinct. An unresolved
constructor also reports E1603 with a target/interface-oriented headline; its
cause is more helpful than its headline.

**An application-wide registry needs manual scoping.** Reusing the inventory
registry for a pure triage term is a local E1601 refusal. More seriously,
leaving dispatch unbound did not reject the entire workflow before action:
reservation committed first, then Core returned E1606 when dispatch was
reached. The authority documentation has been corrected to describe refusal
before dispatch of the missing operation, not transactional preflight of a
whole invocation. A future completeness check must come from checked operation
requirements; display signatures alone list effects, not all operations used.

**Multiple effects do not form a transaction.** A failed dispatch left the
reservation committed. An identical repeat returned a duplicate receipt and
left dispatch undone. The next application design should put reservation and
an outbox entry in one named transaction, then give delivery its own explicit
idempotency/reconciliation contract. An invocation-wide rollback would be a
false promise for arbitrary external actions. This is an application-design
finding, not a failed capability check.

**The deadline does not bound callback duration.** A 300 ms Python handler with
a 150 ms host deadline returned carrier loss only after about 329 ms. It did
not reach reservation, but a handler with its own writes could continue past
the deadline. This confirms the documented synchronous-handler limit. Handler
timeouts need an explicit completion policy; they cannot be added by treating
a timed-out thread as proof that an action stopped.

## What I would improve next

1. A versioned preparation/binding path that exposes member kinds and generates
   ordinary Python conversions from checked schemas. Keep the exact identities
   and raw frames available underneath it. This follows the existing embedding
   backlog rather than inventing a competing schema convention here.
2. A common failure renderer/code accessor covering Core diagnostics, host
   evidence, and spawn failures, with examples that handle every path.
3. Host-side preflight for encodable frame depth/size, plus shape-aware batching
   guidance. Keep the distinction between input validation and output-size
   guarantees. A flat bulk value belongs in a later Core contract.
4. Registry construction from checked term requirements, with explicit
   validation of completeness before effectful execution. Avoid silently giving
   every term an application's entire registry.
5. A documented multi-step persistence example using an outbox and explicit
   reconciliation, alongside the existing one-operation study transaction.

The current library is useful for bounded decision kernels and trusted local
workflows. The protocol behavior held up in these exercises; most of the pain
was the embedding API around it and the practical limits of the serial v0
carrier. "Implemented" is a fair description of the Python adapter;
"comfortable general-purpose Python SDK" would be premature.
