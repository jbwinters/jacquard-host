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
`rust/src/compat.rs`, `java/src/main/java/org/jacquardlang/host/Compat.java`,
`dotnet/src/JacquardHost/Compat.cs`, the Go, Rust, Java, and .NET kit copies
under `go/kit/data/`, `rust/kit/`,
`java/src/main/resources/org/jacquardlang/host/kit/`, and
`dotnet/src/JacquardHost/kit/`, or the generated block
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

## Java

The independent Java adapter and SDK live in `java/` as Maven artifact
`org.jacquardlang:jacquard-host`. It has no third-party runtime dependencies
and does not call Python, Node.js, Go, or Rust; on Unix it runs the system
`mkfifo`. JUnit 5 is its only test dependency. Java 21+ and Maven 3.9+ are
required.

```sh
cd java
mvn -B verify
# With the pinned Core configured (eval "$(python3 ../scripts/core_pin.py install)"):
JACQUARD_REQUIRE_LIVE=1 mvn -B verify
java -cp target/classes:target/test-classes org.jacquardlang.host.examples.JacquardExamples quickstart
```

CI runs the offline tests on Java 21 and 25, and the live tests and the four
example projects on Java 21 in the shared pinned-Core job. The compiler runs
with `-Xlint:all -Werror`.
`java/src/main/java/org/jacquardlang/host/Compat.java` is generated and the
kit under `java/src/main/resources/` is a byte copy of the pinned kit.
`scripts/core_pin.py` maintains both; do not edit them by hand. The `StandIn`
main class and the `FakeCore` script in the test sources are fixtures for the
offline tests. The `JacquardExamples` command, also a test source, reads the
rules and inputs from `examples/` directly, so there is no copy to keep in
sync. The README's Java examples are compiled test sources under
`java/src/test/java/org/jacquardlang/host/readme/` (a test fails if they
differ). Copy the repository `NOTICE` to `java/NOTICE` when it changes (a test
fails if they differ).

## .NET

The independent .NET adapter and SDK live in `dotnet/` as package
`JacquardLang.Host` (`dotnet/src/JacquardHost`, the SDK in its `Sdk/`
directory). It targets .NET 8, has no runtime dependencies beyond the .NET base
class library, and does not call Python, Node.js, Go, Rust, or Java; it calls
the C library (`poll(2)`, and for the SDK `kill(2)`, `geteuid(2)`, `mkdir(2)`,
`statx(2)` or `stat(2)`, `getpwuid_r(3)`, `getgrgid_r(3)`, `getpwent_r(3)`, and `lgetxattr(2)`) and runs on Linux and macOS only. On Linux the
SDK runs Core's commands through util-linux `setsid` when one is installed.
NUnit, its adapter, and the test SDK are its only (test) dependencies. The
.NET 8 or 10 SDK is required.

```sh
cd dotnet
dotnet test test/JacquardHost.Tests -f net8.0
dotnet test test/JacquardHost.Tests -f net10.0   # with a .NET 10 SDK
# With the pinned Core configured (eval "$(python3 ../scripts/core_pin.py install)"):
JACQUARD_REQUIRE_LIVE=1 dotnet test test/JacquardHost.Tests -f net8.0
dotnet build examples/JacquardExamples
dotnet examples/JacquardExamples/bin/Debug/net8.0/JacquardExamples.dll quickstart
```

The example command's program cache is under `dotnet/.scratch/`, and the SDK
refuses it when another user could write a directory above it: one writable
by others, or by its group unless that group is your private group (named
after you, listing no other member, and no other account in the password
database having it as its primary group; a clone made with a `umask` of 002
under user-private groups passes). The refusal names every such directory;
run `chmod go-w` on each, or pass `--workdir` under directories only you or
root may write.

The SDK tests make their scratch directories under `TMPDIR` when neither a
group nor another user may write it or any directory above it (unless
sticky), and otherwise under `/tmp` when that holds, so the refusals they
expect name only the levels they make; when neither does, they fail asking
for such a `TMPDIR`.

CI runs the offline tests on .NET 8 and 10, and the live tests on both and the
four example projects in the shared pinned-Core job. The build treats warnings as errors, with nullable
reference types and the `8.0-recommended` analyzers.
`dotnet/src/JacquardHost/Compat.cs` is generated and
`dotnet/src/JacquardHost/kit/` is a byte copy of the pinned kit.
`scripts/core_pin.py` maintains both; do not edit them by hand. The
`JacquardHost.StandIn` program under `dotnet/test/` is a fixture for the
offline tests. The `JacquardExamples` command under `dotnet/examples/` reads the
rules and inputs from `examples/` directly, so there is no copy to keep in
sync. The README's C# examples are compiled test sources under
`dotnet/test/JacquardHost.Tests/Readme/` (a test fails if they differ). Copy
the repository `NOTICE` to `dotnet/NOTICE` when it changes (a test fails if
they differ).

## Releasing

A release is one commit on `main` with green CI. To cut one:

1. Open a pull request that dates the CHANGELOG entry and updates any version
   references in the docs. Keep the Python (`src/jacquard_host/__init__.py`),
   npm (`typescript/package.json`, `typescript/src/version.ts`), Rust
   (`rust/Cargo.toml`), Java (`java/pom.xml`), .NET
   (`dotnet/Directory.Build.props`), and release versions equal.
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

Nothing is published to PyPI, npm, crates.io, Maven Central, or NuGet. The
Rust crate is used from the repository tag, and the Java artifact and the .NET
package are built from it.

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
