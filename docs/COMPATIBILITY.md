# Compatibility

## Pinned Core artifacts

Python and TypeScript consume the same artifacts. The Node package copies the
kit verbatim into its build assets and verifies the four digests independently
in `typescript/src/kit.ts`; its pin constants are in `typescript/src/compat.ts`.
Both adapters currently replay all 21 recordings against the pinned worker,
including Core's recorded `noncanonical-target-hash` divergence. This adds
independent v0 evidence; it does not change the protocol version or declare v1.

The adapter is pinned to these Jacquard Core artifacts. The same values live
in `src/jacquard_host/compat.py`, and the test suite fails if the vendored kit
under `src/jacquard_host/data/conformance/host-protocol-v0/`, this document, and that module disagree.

- Jacquard release baseline: `jacquard-core-0.2.0`
  (release commit `aceca65e364db6dcf91cbdd54586c136646dfa37`)
- host protocol: `jacquard-host-v0` (frozen HB.1 contract,
  `spec/host-protocol-v0.md` in jacquard-lang)
- carrier: `stdio-u32-json-v0`
- Core version recorded by the kit: `0.2.0`
- kit source commit: `61575c7bcd98c8922e739de647556d1368729ad8`
  (jacquard-lang `main` after the squash merge of pull request 115; the kit
  files are byte-identical to the reviewed branch head `c9947c2` they were
  first pinned from)
- kit evidence pack: `docs/release/host-boundary/` in jacquard-lang at
  `49ed2d0590682d5f0be2eb2693b3781035039187` (pull request 116), which
  records the kit's evidence, its still-active limits, and two pending
  decisions (`noncanonical-target-hash`, diagnostic prose)

Kit file digests (SHA-256, paths relative to `spec/host-protocol-v0/` in
Core and to `src/jacquard_host/data/conformance/host-protocol-v0/` here):

| File | SHA-256 |
|---|---|
| `kit/kit.json` | `a286328c457696a898aaa6731f0044fb90c78d8657d57270384a76976b070b53` |
| `kit/transcripts.json` | `ebaf590e3d1b7e8399f56de5ad85043ac3658416dd7f0af7b55b9f9f3ce93240` |
| `kit/fixtures.jac` | `037bb8209ca0344ba0a2cd0fcc16e69f13c8f9b91e3404dc3f3161e351831d80` |
| `vectors.json` | `9ba1bff8eee0b6dbea6475dc0c7e9cc6313d847ad80bd8d832457115eb1c738a` |

The kit records one pending Core decision, `noncanonical-target-hash`: the
HB.1 vector expects E1603 while the shipped preflight classifies an uppercase
target hash as a malformed scalar (E1601). The transcript is recorded as
`diverges`, and the adapter reproduces that recording rather than hiding it.
Six diagnostic prose fields also drift from the HB.1 vector text; prose is not
normative and is reported, never normalised.

## Status of the pin

See [SETUP.md](SETUP.md) for a reproducible build of the pinned worker and its
matching prelude. The tagged 0.2.0 binary release predates host-worker support;
use the commit pin above rather than relying on the version string alone.

The kit and its evidence pack are both on Core `main`, so all five items of
the adoption rule below are met. The file digests are the durable part of
the pin; the commit hashes record where the kit and the pack live. The
adapter's claim is that it replays the pinned kit in full and agrees with
every recording; the protocol itself remains the experimental `v0` carrier
that Core's pack describes, with the limits and pending decisions recorded
there. Core owns a "stable v1" decision and its acceptance of the independent
adapter and serial-application evidence; these implementations still select v0.

## Adoption rule

Implementation of an adapter begins only after `jacquard-lang` publishes all
of the following together:

1. a versioned, transport-neutral host contract;
2. a concrete carrier version;
3. canonical positive and hostile conformance fixtures;
4. explicit bounds and unknown-input behavior; and
5. an evidence and limitations statement.

All five items are satisfied by the pinned specification, worker, kit, and
evidence pack on Core `main`. Adapter-specific normalization
may not weaken, repair, or silently reinterpret a fixture. Protocol divergence
is reported upstream or fixed in the adapter; it is never hidden behind a
language-specific compatibility rule.

The process carrier is expected to be provisional. A later native or
WebAssembly carrier may replace it while preserving the transport-neutral
invocation, effect, diagnostic, cancellation, and evidence contract.

## Refreshing the pin

1. Copy the four kit files from the Core commit into
   `src/jacquard_host/data/conformance/host-protocol-v0/`.
2. Update the digests and commit in `src/jacquard_host/compat.py`,
   `typescript/src/compat.ts`, and this document.
3. Run the offline suite, then replay the kit against the matching Core build:

```sh
PYTHONPATH=src python3 -m unittest discover -s tests -v
PYTHONPATH=src JACQUARD_BIN=/path/to/jacquard JACQUARD_PRELUDE=/path/to/prelude \
  python3 -m jacquard_host.kit --report .scratch/kit-replay.json
# With the same environment configured, from typescript/:
npm test
npm run test:live
npm run check:package
```

A transcript that stops matching its recording is a protocol or
implementation finding to report upstream, not something to normalise here.
