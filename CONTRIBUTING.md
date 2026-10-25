# Contributing

Jacquard Host consumes the versioned Core boundary. Language semantics,
identity, and protocol changes belong in
[Jacquard Core](https://github.com/jbwinters/jacquard-lang).
Read [compatibility](docs/COMPATIBILITY.md) and [limits](docs/LIMITS.md) before
changing boundary behavior. Use synthetic data in examples and tests.

Work on a branch and submit a pull request. Explain the behavior changed and
the checks run. Keep public examples runnable and update their expected output
when behavior changes. Runtime dependencies should remain standard-library-only
unless a concrete need is reviewed.

Contributions are licensed under [Apache 2.0](LICENSE), matching this repository.

## Core releases

`core-pin.json` names the one Core release all adapters target. Do not edit
`src/jacquard_host/compat.py`, `typescript/src/compat.ts`, or the generated block
in `docs/COMPATIBILITY.md` by hand: change the pin with
`python3 scripts/core_pin.py update --tag ...`, which also vendors the kit, and
`python3 scripts/core_pin.py check` (run in CI) reports any drift.
`python3 scripts/core_pin.py status` says whether Core has a newer release.
Adopt a release in its own pull request with the live evidence described in
[COMPATIBILITY.md](docs/COMPATIBILITY.md#following-core-releases).

## Offline checks

With Python 3.11+ and a virtual environment:

```sh
python -m pip install 'setuptools>=68' wheel
python scripts/core_pin.py check
PYTHONPATH=src python -m unittest discover -s tests -v
python -m compileall -q src tests examples scripts
python scripts/check_distribution.py
```

The distribution check builds a source archive, rebuilds its wheel, installs
outside the checkout, checks bundled examples and resources, and replays the
recorded transcripts through a stand-in worker. Live tests skip when Core is
not configured.

## Live checks

Install and configure the pinned Core with [SETUP.md](docs/SETUP.md)
(`eval "$(python3 scripts/core_pin.py install)"`), then test the installed
library and runnable examples:

```sh
python -m pip install .
python -m unittest discover -s tests -v
python scripts/check_examples.py
python examples/python_sdk/exercise.py --report .scratch/sdk-exercise/results.json
```

CI runs the offline suite on Python 3.11, 3.12, and 3.13, and has a separate
Linux job that installs the pinned Core release and runs the live suite and
examples against the installed wheel. Recorded evidence and known limitations
belong in [EVIDENCE.md](docs/EVIDENCE.md); timings are observations, not promises.

Keep generated caches, SQLite state, reports, secrets, and participant data out
of commits. Raw inspection reports contain application values. Use `.scratch/`
for local artifacts. Do not change or normalize the frozen conformance fixtures
to make a test pass; report a mismatch against the pinned contract.

## TypeScript

The independent Node.js adapter lives in `typescript/`; it does not call Python.
Node.js 22+ and npm are required. TypeScript and Node declarations are locked
development dependencies; the package has no third-party runtime dependency.

```sh
cd typescript
npm ci
npm test
npm run check:package
# With the pinned Core configured:
npm run test:live
npm run check:package
```

CI checks offline tests, consumer types, and installed tarballs on Node 22/24.
The shared Core build job runs the live Node suite and installed examples too.
Kit assets are copied verbatim from the Python package's pinned fixture directory
at build time and digest-checked independently. Do not edit generated `data/`
or `dist/` files. Update source, examples, or the shared pin instead.
