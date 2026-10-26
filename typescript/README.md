# Jacquard Host for TypeScript

Call checked Jacquard functions from Node.js, bind named services with async
methods, and inspect the exact protocol evidence. This is an independent
TypeScript implementation of the pinned `jacquard-host-v0` boundary. It runs
Core directly and has no Python or third-party runtime dependency.

Node.js 22+, ESM. The SDK and process carrier are experimental. Core is a
separate executable; the host and operating system are trusted. Browser and
Windows support are not claimed.

## Install from the repository

Build and configure the pinned Core worker using [setup](https://github.com/jbwinters/jacquard-host/blob/main/docs/SETUP.md).
Then, from the repository root:

```sh
npm --prefix typescript ci
npm --prefix typescript run build
node typescript/dist/examples/run.js quickstart
```

Expected output:

```text
Order total 35: shipping 5
Order total 75: shipping 0
```

To use the package in another Node project, create and install a local tarball:

```sh
# From this repository's typescript/ directory
npm pack
# In your application: npm install /absolute/path/to/jacquard-host-0.2.0.tgz
```

Each release also attaches the tarball to its
[GitHub release](https://github.com/jbwinters/jacquard-host/releases/tag/jacquard-host-0.2.0).
These commands do not assume an npm registry release. TypeScript applications
need their usual development tools, `typescript` and `@types/node`, with Node's
declarations enabled (for example `"types": ["node"]` in `tsconfig.json`). Runtime
imports use the compiled JavaScript and included declarations.

## Call a checked function

```typescript
import { Runtime } from 'jacquard-host';

const program = await new Runtime().prepare('rules.jac');
const shippingCost = program.function('shipping-cost', {
  parameters: [program.data.int],
  result: program.data.int,
});

const cost: number = await shippingCost.call(35);
const run = await shippingCost.invoke(75);
console.log(run.unwrap(), run.render());
await run.save('.scratch/shipping.json');
```

For the shipping rule, use the [quickstart source](https://github.com/jbwinters/jacquard-host/blob/main/examples/quickstart/rules.jac).
`prepare` accepts one path or an ordered array of `.jac` paths (or `.jqd` paths).
It uses public Core check/hash/run commands and verifies a cached store keyed
by source bytes, executable bytes, all prelude contents, protocol, and preparation
format. Specify `{ cacheDir: '...' }` as the second argument to choose the cache
location. Reuse one `Runtime` across preparations to share prelude discovery.
One fresh worker runs each call.

Prepare first-party declaration-only sources. Preparation runs Core's public
`run` command to populate the store without application service bindings.

Parameter and result codecs preserve TypeScript types and validate the mapped
data at runtime. Without codecs, `.call()` accepts native values and returns
`unknown`. Core always checks the actual callable and wire types. Codecs are
explicit application mappings, not a generated proof of a Jacquard data layout.

## Records, unions, and enums

```typescript
const d = program.data;
const Policy = d.record('TriagePolicy', { page: d.int, stale: d.int });
const Incident = d.record('Incident', {
  id: d.text, service: d.text, severity: d.int, age: d.int,
  visible: d.bool, owner: d.option(d.text),
});
const urgency = d.enumeration({ PAGE: 'PageNow', TODAY: 'WorkToday', WATCH: 'WatchOnly' });
const Routed = d.record('Routed', {
  id: d.text, team: d.text, urgency, reasons: d.list(d.text),
});
const InvalidIncident = d.record('InvalidIncident', { id: d.text });
const route = program.function('triage.batch', {
  parameters: [Policy, d.list(Incident)],
  result: d.list(d.union(Routed, InvalidIncident)),
});
const results = await route.mapBatches([
  Policy.create({ page: 4, stale: 60 }),
  [Incident.create({ id: 'inc-1', service: 'api', severity: 5,
    age: 90, visible: true, owner: null })],
]);
for (const result of results) {
  if (result.$tag === 'Routed') console.log(result.team, result.urgency);
}
```

Record fields must follow the Jacquard constructor's positional order.
`.create()` produces ordinary objects with a discriminant such as
`$tag: 'Incident'`; field codecs reject invalid values before invocation.
`Infer<typeof Incident>` extracts the record type. Union results narrow on
`$tag`. Enum keys are application strings encoded as exact constructor identities.
For dynamic code, `d.make('ConstructorName', ...fields)` makes a `Variant`.
Unknown or ambiguous public names are refused.

Prelude discovery covers primitive types, Bool/List/Option, and Hash, plus the
prepared source definitions. Other prelude nominal types require explicit
identities through the low-level API.

| Data | Mapping |
|---|---|
| `d.int` | Safe integer `number`; rejects fractions, booleans, and unsafe integers |
| `d.bigint` | `bigint`, covering Core's complete signed 63-bit Int range |
| `d.real` | `number` encoded as binary64; includes integer-valued reals, NaN, infinities, and signed zero |
| `d.text`, `d.bool`, `d.hash` | Scalar text, boolean, and explicit `Hash` |
| `d.list(codec)`, `d.tuple(...codecs)` | Arrays and positional tuples |
| `d.option(codec)` | `null` or a value; explicit `Some` represents nested option states |
| `d.unit` | `undefined` for Jacquard unit |

Without codecs, checked primitive parameter types select the encoding. A safe
integer number becomes Int, a number at a Real parameter becomes Real, arrays
become lists (or checked tuples), and `null` becomes None. Decoded Int values are
numbers when safe and bigints otherwise. Handler results have no checked type
hint from the pinned CLI: use a result codec or `Real.from(1)` for an
integer-valued Real. The low-level `Real` class preserves exact NaN payload bits;
`.raw` retains the original wire descriptors. Native JavaScript arithmetic does
not promise bitwise NaN preservation.

## Services and failures

```typescript
import { HostFailure } from 'jacquard-host';

class Documents {
  async readDocument(key: string): Promise<string> {
    if (key !== 'example') {
      throw new HostFailure('refused_authority', 'not_started', 'unknown document');
    }
    return 'A synthetic document supplied by Node.js.';
  }
}
const summarize = program.function('summarize', {
  effects: { Documents: new Documents() },
  parameters: [program.data.text], result: program.data.text,
});
console.log(await summarize.call('example'));
```

Services are explicit objects. Operation names accept exact kebab names, camel
case (`readDocument`), or snake case (`read_document`). Methods keep `this`.
Every operation in each checked root effect is required before invocation,
including operations an execution path may not reach. `program.provide({...})`
shares configured services; each function receives only its own checked effects.

For callback codecs or cooperative cancellation, use a descriptor instead of
a method: `{ handler, parameters: [...], result: codec, context: true }`.
With `context: true`, the final handler argument is `{ signal: AbortSignal }`.
`invokeWith({ signal }, ...args)` also accepts an external abort signal.

`Runtime({ deadlineMs: 30000 })` bounds the worker lifetime, pipe operations,
awaited handlers, and process exit. A pending async handler can outlive the
invocation; a CPU-bound synchronous handler blocks Node's event loop and cannot
be preempted. An abort signal is cooperative. The SDK kills and reaps the worker
and never sends a late response or retries the action. This does not prove that
an outside operation stopped or rolled back.

Native calls and `.unwrap()` raise `JacquardError` subclasses. `InvocationError`
retains `.result`, `.code`, and `.origin`; `OperationFailed` includes unknown
completion, `DeadlineExceeded` marks the host deadline, and `WorkerLost` covers
carrier loss. Local input and binding errors use `InvalidArgument` and
`BindingError`. A result mapping error retains its successful Core `.result`.
Core and host evidence remain separate; inspection files include argument and
result values and are written only by an explicit `.save()`.

## Run the projects

All rules and synthetic inputs ship in the npm tarball:

```sh
node typescript/dist/examples/run.js planner
node typescript/dist/examples/run.js triage
node typescript/dist/examples/run.js fulfillment
node typescript/dist/examples/run.js fulfillment
```

Planner returns score 16, triage returns PAGE/TODAY/WATCH, and fulfillment
returns Fulfilled then DuplicateOrder with the same receipt. Use `--inspect PATH`
to save frames (triage adds numbered chunk suffixes). Custom input JSON may
follow the project name. State defaults to `.scratch/typescript-examples/`.

Try explicit recovery in a fresh work directory:

```sh
node typescript/dist/examples/run.js fulfillment --workdir .scratch/ts-recovery --fault publish-failed
node typescript/dist/examples/run.js fulfillment --workdir .scratch/ts-recovery --deliver-pending
node typescript/dist/examples/run.js fulfillment --workdir .scratch/ts-recovery --deliver-pending
```

The first command intentionally exits 1 with E1613 and one pending booking.
Reconciliation delivers one receipt, then zero. `after-commit` and
`after-delivery` exercise E1612 around the snapshot commits. The TypeScript
warehouse uses an atomic JSON snapshot with booking/outbox updates together and
a receipt-deduplicating simulated delivery sink. It is a single-process example
with no cross-process locking or power-loss durability claim. Python's example
uses SQLite instead. Real queues and stores need their own recovery contract.

`mapBatches(args, { batchSize: 8 })` is explicit opt-in for pure independent
List-to-List decisions. All chunks are validated before the first runs; a failed
chunk records `batchIndex` and `completedValues`, with no adaptive retry. The v0
nested-list depth ceiling still applies to inputs and derived outputs.

## Check the implementation

```sh
cd typescript
npm ci
npm test
npm run check:package
# With JACQUARD_BIN and JACQUARD_PRELUDE configured:
npm run test:live
npm run check:package
```

Offline tests cover strict JSON, lossless values, state ordering, hostile
workers, and all 21 recorded transcripts through a stand-in. Live tests replay
all 21 against Core and exercise services, schemas, caches, worker failures,
batching, correctness oracles, and recovery. The distribution check installs a
tarball outside the checkout, checks exports and consumer types, verifies the
packaged kit, and replays the recordings. With Core configured it also runs all
four installed examples. No Python process participates in these checks.

Low-level APIs are exported from `jacquard-host` and `jacquard-host/protocol`;
the pinned kit and replayer are in `jacquard-host/kit`. See
[compatibility](https://github.com/jbwinters/jacquard-host/blob/main/docs/COMPATIBILITY.md), [limits](https://github.com/jbwinters/jacquard-host/blob/main/docs/LIMITS.md), and
[evidence](https://github.com/jbwinters/jacquard-host/blob/main/docs/EVIDENCE.md). Apache 2.0; upstream attribution is included in
the package's LICENSE and NOTICE.
