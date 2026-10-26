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
`src/jacquard_host/compat.py`, `typescript/src/compat.ts`, `go/protocol/compat.go`,
`rust/src/compat.rs`, the Go and Rust kit copies under `go/kit/data/` and
`rust/kit/`, or the generated block
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

## Go

The independent Go adapter and SDK live in `go/` as module
`github.com/jbwinters/jacquard-host/go`; they use only the standard library and
do not call Python or Node.js. Go 1.24+ is required.

```sh
cd go
gofmt -l .
go vet ./...
go test -race ./...
# With the pinned Core configured (eval "$(python3 scripts/core_pin.py install)"):
go test -count=1 ./...
```

CI runs the offline tests with the race detector on Go 1.24 and the current
stable release, and the live tests in the shared pinned-Core job.
`go/protocol/compat.go` is generated and `go/kit/data/` is a byte copy of the
pinned kit, both maintained by `scripts/core_pin.py`; do not edit them by hand.
`go/examples/data/` embeds copies of the example rules and inputs: after changing
a file under `examples/`, copy it there too (a Go test fails if they differ).

## Rust

The independent Rust adapter and SDK live in `rust/` as crate `jacquard-host`.
It uses only the standard library and does not call Python, Node.js, or Go.
Rust 1.88+ is required.

```sh
cd rust
cargo fmt --check
cargo clippy --all-targets -- -D warnings
cargo test
# With the pinned Core configured (eval "$(python3 ../scripts/core_pin.py install)"):
cargo test -- --nocapture
```

CI runs fmt, clippy, and the offline tests on Rust 1.88 and the current stable
release. It runs the live tests in the shared pinned-Core job.
`rust/src/compat.rs` is generated and `rust/kit/` is a byte copy of the pinned
kit. `scripts/core_pin.py` maintains both; do not edit them by hand. The
`jacquard-host-standin` binary is a test fixture for the offline tests. The
`jacquard-examples` Cargo example reads the rules and inputs from `examples/`
directly, so there is no copy to keep in sync.

## Releasing

A release is one commit on `main` with green CI. To cut one:

1. Open a pull request that dates the CHANGELOG entry and updates any version
   references in the docs. Keep the Python (`src/jacquard_host/__init__.py`),
   npm (`typescript/package.json`, `typescript/src/version.ts`), Rust
   (`rust/Cargo.toml`), and release versions equal.
2. After it merges, tag the merge commit twice, with annotated tags
   (`git tag -a`):
   `jacquard-host-X.Y.Z` for the repository and its assets, and `go/vX.Y.Z`
   for the Go module.
3. Build the assets from a clean checkout of the tag:
   - the sdist and wheel, through `setuptools.build_meta`, as
     `scripts/check_distribution.py` does;
   - the npm tarball (`npm pack` in `typescript/`);
   - a `SHA256SUMS` file.
4. Publish a GitHub release for `jacquard-host-X.Y.Z` with the CHANGELOG
   entry as its notes and the assets attached.

Nothing is published to PyPI, npm, or crates.io. The Rust crate is used from
the repository tag.

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
