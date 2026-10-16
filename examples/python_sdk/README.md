# Python SDK walkthrough

These use the same checked Jacquard rules as the
[original adapter experiments](../../docs/development/python-projects/README.md). Python code now
uses cached preparation, native data, typed result records, complete service
binding, and one failure family. The inventory service also keeps a durable
outbox so an explicit recovery command can repair a failed dispatch.

Follow [setup](../../docs/SETUP.md), then run from the repository root:

```sh
python examples/python_sdk/app.py planner examples/planner/input.json
python examples/python_sdk/app.py triage examples/triage/input.json
python examples/python_sdk/app.py fulfillment examples/fulfillment/input.json
```

See each project for its inputs, rules, and expected output:
[planner](../planner/README.md), [triage](../triage/README.md), and
[fulfillment](../fulfillment/README.md). Core and its prelude must match
[the compatibility pin](../../docs/COMPATIBILITY.md).

The planner returns `Planned(Plan(score=16, ...))`, choosing two small jobs over
the tempting large job, plus the documentation job in the second slot. Triage
returns native `Routed` records whose urgency is an enum. The first fulfillment
returns `Fulfilled(receipt=...)`; repeating it returns `DuplicateOrder` with the
same receipt. SQLite state and verified prepared artifacts live under
`.scratch/python-sdk/` by default; `--workdir` chooses another isolated state.

The application path is ordinary Python:

```python
program = runtime.prepare("examples/fulfillment/rules.jac")
for record in (Line, Order, Fulfilled, DuplicateOrder, OrderRejected):
    program.data.record(record)

place = program.function("fulfillment.place", effects={
    "Inventory": warehouse,
    "Dispatch": warehouse,
})
decision = place(Order("synthetic-order-1", [Line("sensor", 2), Line("cable", 3)]))
```

The explicit dataclasses in [app.py](app.py) define the application's Python
mapping, in Jacquard constructor field order. There are no application hash
lookups, prelude-file discovery, hand-built `Cons`/`Some` values, or custom
protocol result dictionaries. Automatic generation of these dataclasses still
needs Core's structured host interface exporter.

For pure independent incident rows:

```python
reports = program.function("triage.batch").map_batches(
    TriagePolicy(page=4, stale=60),
    [Incident("inc-1", "api", 5, 90, True, owner=None)],
)
```

This uses explicit eight-row chunks under the existing v0 depth ceiling.
Batching is not suitable for global optimization, sorting, or cross-row policy;
the SDK never claims chunking preserves those functions' meaning.

## Failure and recovery

Try a dispatch failure with a fresh work directory:

```sh
python3 examples/python_sdk/app.py fulfillment examples/fulfillment/input.json \
  --workdir .scratch/sdk-recovery --fault publish-failed --inspect .scratch/failed-run.json
```

The invocation exits 1 with E1613. Its state shows one booking, one pending
delivery, and zero completed deliveries. The booking and pending delivery were
written in one SQLite transaction. A normal repeat recognizes the booking;
it does not silently retry delivery.

Choose explicit reconciliation:

```sh
python3 examples/python_sdk/app.py fulfillment --workdir .scratch/sdk-recovery --deliver-pending
python3 examples/python_sdk/app.py fulfillment --workdir .scratch/sdk-recovery --deliver-pending
```

The first command delivers one receipt; the second delivers zero. `after-commit`
and `after-delivery` faults exercise unknown completion before and after the
simulated sink accepted the receipt. The sink deduplicates receipts durably.
This is an offline SQLite simulation, not a claim about any real message queue.

The program binder refuses a missing dispatch method before the invocation, so
the missing-binding failure from the original experiment cannot reserve stock
through this API. Dataclass errors such as `Line("sensor", True)` name the
`.lines[0].units` field before any handler runs. A real callback can still fail
after another callback commits; the outbox handles that application problem.

`--inspect PATH` saves raw frames for fulfillment/planning. Triage writes one
numbered inspection file per chunk. `.invoke()`, `.raw`, `.render()`, and
`.save()` also work directly from Python; regular calls simply return the
native value or raise a `JacquardError` subclass.

## Evidence

Run the installed-wheel exercise:

```sh
python3 examples/python_sdk/exercise.py --report .scratch/sdk-exercise/results.json
```

It refuses a source-tree library import and exercises all three applications,
native results, 64 incident rows, missing/invalid bindings, all three outbox
faults, explicit reconciliation, cached preparation, and a 24-row batching
comparison. [Development measurements](../../docs/development/sdk-results.json) record one local run and the installed
SDK source digest. Warm preparation was about 23 ms; the first cold preparation
was about 3 seconds and subsequent projects sharing prelude discovery about
1.5 seconds. Processing 24 incidents took about 495 ms individually and 82 ms
in batches. These are observations on one machine, not latency guarantees.

The live regression suite also checks 25 planning problems against an
independent exhaustive oracle, validates feasibility of returned assignments,
checks a 60-row triage matrix against a Python oracle, and exercises cache
corruption, effect scoping, output limits without retry, and worker loss. The
original low-level adapter continues to replay the same pinned conformance kit.

The carrier remains serial and provisional. Synchronous callbacks cannot be
preempted by its deadline, and the depth ceiling remains. See
[the SDK guide](../../docs/PYTHON-SDK.md) and [the limits](../../docs/LIMITS.md).
