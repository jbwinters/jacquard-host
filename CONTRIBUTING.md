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

## Offline checks

With Python 3.11+ and a virtual environment:

```sh
python -m pip install 'setuptools>=68' wheel
PYTHONPATH=src python -m unittest discover -s tests -v
python -m compileall -q src tests examples scripts
python scripts/check_distribution.py
```

The distribution check builds a source archive, rebuilds its wheel, installs
outside the checkout, checks bundled examples and resources, and replays the
recorded transcripts through a stand-in worker. Live tests skip when Core is
not configured.

## Live checks

Build and configure Core using [SETUP.md](docs/SETUP.md), then test the installed
library and runnable examples:

```sh
python -m pip install .
python -m unittest discover -s tests -v
python scripts/check_examples.py
python examples/python_sdk/exercise.py --report .scratch/sdk-exercise/results.json
```

CI runs the offline suite on Python 3.11, 3.12, and 3.13, and has a separate
Linux job that builds the pinned Core commit and runs the live suite and
examples against the installed wheel. Recorded evidence and known limitations
belong in [EVIDENCE.md](docs/EVIDENCE.md); timings are observations, not promises.

Keep generated caches, SQLite state, reports, secrets, and participant data out
of commits. Raw inspection reports contain application values. Use `.scratch/`
for local artifacts. Do not change or normalize the frozen conformance fixtures
to make a test pass; report a mismatch against the pinned contract.
