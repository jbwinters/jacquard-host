# Jacquard Host

Jacquard Host is the private integration workspace for invoking Jacquard from
other languages and connecting Jacquard programs to operating-system services.
It is deliberately separate from
[`jacquard-lang`](https://github.com/jbwinters/jacquard-lang), which remains the
language implementation and semantic source of truth.

Status: the strict `stdio-u32-json-v0` carrier codecs and a replayer for
Core's pinned `jacquard-host-v0` conformance kit are implemented in Python.
The embedding adapter (spawn a worker, invoke one checked term, serve its
configured effect operations) is the next slice. No HTTP server or deployment
surface exists yet.

## Repository boundary

This repository will own:

- host adapters that consume a released, versioned Jacquard host contract;
- the minimal serial HTTP integration used to test that contract;
- external persistence and socket adapters;
- cross-language conformance runs and integration evidence; and
- concrete requirements reported back to Jacquard Core.

It will not own:

- Jacquard language semantics, kernel forms, effect checking, or runtime grants;
- a competing copy of the host protocol;
- the real readability-study application or participant data;
- nginx, Cloudflare, TLS, authentication, or deployment configuration; or
- a claim that Jacquard capability checks are an operating-system sandbox.

The first integration is intentionally serial and local. It will use
first-party Jacquard programs, a trusted host and operating system, bounded
messages, a closed set of configured host operations, loopback sockets, and
synthetic data. See [the limits](docs/LIMITS.md) for the complete claim
boundary and the evidence required to widen it.

## Current compatibility

The adapter is pinned to the exact conformance-kit files of Core's
`jacquard-host-v0` protocol at a reviewed but not yet merged Core commit
(the worker and kit postdate the `jacquard-core-0.2.0` tag), vendored under
`conformance/host-protocol-v0/` and digest-checked by the test suite. The pin,
its provisional status, and the refresh procedure are in
[COMPATIBILITY.md](docs/COMPATIBILITY.md).

## Roadmap

[ROADMAP.md](docs/ROADMAP.md) separates the reusable host foundation from the
future readability product. Task Master mirrors this roadmap as local planning
state; the Markdown document is the reviewable source of intent.

## Development

There are no third-party runtime dependencies. The offline suite needs only
Python 3.11 or later:

```sh
PYTHONPATH=src python3 -m unittest discover -s tests -v
python3 -m compileall -q src tests
```

To replay the pinned kit against a real worker, point the suite (or the
replayer's command line) at a Core build of the pinned commit:

```sh
PYTHONPATH=src JACQUARD_BIN=/path/to/jacquard JACQUARD_PRELUDE=/path/to/prelude \
  python3 -m unittest tests.test_kit_live -v
PYTHONPATH=src python3 -m jacquard_host.kit --jacquard /path/to/jacquard \
  --prelude /path/to/prelude --report .scratch/kit-replay.json
```

The replayer builds the kit store with the published recipe, plays every
recorded transcript in lockstep against a fresh worker, and reports any
transcript whose Core frames, exit status, or observed summary differ from the
recording. Diagnostic prose is reported as drift, never normalised.

Subsequent adapters must pin the exact Jacquard protocol fixtures they consume
and fail closed on unknown versions, values, operations, or fields.
