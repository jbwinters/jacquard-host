# Setup

The Python SDK requires Python 3.11 or later and has no third-party runtime
dependencies. Linux is the supported CI platform; see [EVIDENCE.md](EVIDENCE.md)
for other platform status. Core is a separate executable with a matching prelude.

## Install the Python SDK

From a clone of this repository:

```sh
python3 -m venv .venv
. .venv/bin/activate
python -m pip install .
```

These instructions install from source. A PyPI release is not required or
assumed. The source archive includes the examples; the wheel includes the
library, its typing marker, application resources, and the pinned conformance kit.

## Install the pinned Core release

The host adapters are pinned to one Jacquard Core release, recorded in
[`core-pin.json`](../core-pin.json) (see [COMPATIBILITY.md](COMPATIBILITY.md)).
Core publishes Linux x86_64 and macOS (arm64, x86_64) assets; this repository's
CI exercises only Linux x86_64. With Python 3.11.4 or later, download that
release, verify its SHA-256 against the pin, and configure the current shell
from the repository root:

```sh
eval "$(python3 scripts/core_pin.py install)"
"$JACQUARD_BIN" --version
python examples/quickstart/run.py
```

The release unpacks under `.scratch/jacquard-core/`. `install` prints the
`JACQUARD_BIN` (the release's real executable, not its `sh` launcher) and
`JACQUARD_PRELUDE` exports; run it again in a new shell, which reuses the
verified download. A checksum mismatch or a different `--version` fails.
The install's link safety does not rely on Python's tarfile `data` filter. It
refuses archive links that name anything other than a sibling entry, and it
refuses hardlinks. The filter still applies, so a Python with the 2025 tarfile
fixes (3.11.13, 3.12.11, 3.13.4, or later) is preferable.

### Build the same release from source

For other platforms, build the pinned tag. You need Git, opam 2.x, and the
native build tools required by OCaml; follow
[opam's installation instructions](https://opam.ocaml.org/doc/Install.html) if
opam is not installed (initialize a first installation with
`opam init --bare --no-setup`).

```sh
mkdir -p .scratch
git clone https://github.com/jbwinters/jacquard-lang.git .scratch/jacquard-core-src
git -C .scratch/jacquard-core-src checkout --detach "$(python3 scripts/core_pin.py get release.commit)"
cd .scratch/jacquard-core-src
opam switch create . ocaml-base-compiler.5.1.1 -y
opam install . --deps-only -y
opam exec -- dune build bin/main.exe
cd ../..

export JACQUARD_BIN="$PWD/.scratch/jacquard-core-src/_build/default/bin/main.exe"
export JACQUARD_PRELUDE="$PWD/.scratch/jacquard-core-src/prelude"
"$JACQUARD_BIN" host worker --help
```

The switch and build artifacts stay under `.scratch/`. If you use an existing
build instead, set both variables to its absolute paths and check its commit
against the pin. Keep the executable and prelude from the same release.

The quickstart prints shipping costs of 5 and 0. Continue with the
[example projects](../examples/README.md), or run every example:

```sh
python scripts/check_examples.py
```

Preparation checks the Core version and caches checked source, identities,
and store files. A warm run verifies and reuses the cache. A missing handler,
invalid input, mismatched version, or changed cached file fails explicitly;
see [the SDK guide](PYTHON-SDK.md) for error handling and limits.

For download, checkout, or build failures, report the pinned Core tag, platform,
Python version, and failing command. No unpinned fallback binary is selected.
