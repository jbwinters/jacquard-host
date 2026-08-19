# Jacquard Host

Jacquard Host is the private integration workspace for invoking Jacquard from
other languages and connecting Jacquard programs to operating-system services.
It is deliberately separate from
[`jacquard-lang`](https://github.com/jbwinters/jacquard-lang), which remains the
language implementation and semantic source of truth.

Status: repository bootstrap. No host protocol, adapter, HTTP server, or
deployment surface is implemented yet.

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

The planning baseline is Jacquard Core 0.2.0. There is no supported host
protocol yet, so adapters must not invent one. Exact pins and the rule for
adopting the future protocol are in [COMPATIBILITY.md](docs/COMPATIBILITY.md).

## Roadmap

[ROADMAP.md](docs/ROADMAP.md) separates the reusable host foundation from the
future readability product. Task Master mirrors this roadmap as local planning
state; the Markdown document is the reviewable source of intent.

## Development

The bootstrap has no third-party runtime dependencies. Its smoke checks are:

```sh
PYTHONPATH=src python3 -m unittest discover -s tests -v
python3 -m compileall -q src tests
```

Subsequent adapters must pin the exact Jacquard protocol fixtures they consume
and fail closed on unknown versions, values, operations, or fields.
