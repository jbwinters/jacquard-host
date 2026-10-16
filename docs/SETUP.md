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

## Build the matching Core worker

The `jacquard-core-0.2.0` binary release predates the host worker. Its version
string alone is insufficient. Build the exact public commit below, which
contains the worker and pinned kit. You need Git, opam 2.x, and the native build
tools required by OCaml. Follow [opam's installation instructions](https://opam.ocaml.org/doc/Install.html)
for your platform if opam is not installed.

From the host repository root, with opam initialized:

```sh
mkdir -p .scratch
git clone https://github.com/jbwinters/jacquard-lang.git .scratch/jacquard-core
git -C .scratch/jacquard-core checkout --detach 61575c7bcd98c8922e739de647556d1368729ad8
cd .scratch/jacquard-core
opam switch create . ocaml-base-compiler.5.1.1 -y
opam install . --deps-only -y
opam exec -- dune build bin/main.exe
cd ../..

export JACQUARD_BIN="$PWD/.scratch/jacquard-core/_build/default/bin/main.exe"
export JACQUARD_PRELUDE="$PWD/.scratch/jacquard-core/prelude"
"$JACQUARD_BIN" --version
"$JACQUARD_BIN" host worker --help
python examples/quickstart/run.py
```

Initialize a first-time opam installation with `opam init --bare --no-setup`
before creating the switch. The local switch and all build artifacts stay under
`.scratch/`. If using an existing build instead, set both variables to its
absolute paths and verify its commit against [COMPATIBILITY.md](COMPATIBILITY.md).
Keep the executable and prelude from the same checkout. Export the variables
again when opening a new shell.

The quickstart prints shipping costs of 5 and 0. Continue with the
[example projects](../examples/README.md), or run every example:

```sh
python scripts/check_examples.py
```

Preparation checks the Core version and caches checked source, identities,
and store files. A warm run verifies and reuses the cache. A missing handler,
invalid input, mismatched version, or changed cached file fails explicitly;
see [the SDK guide](PYTHON-SDK.md) for error handling and limits.

For checkout access or build failures, report the Core commit, platform, Python
version, and failing command. No unpinned fallback binary is selected.
