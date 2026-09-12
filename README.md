# Jacquard Host

Jacquard Host is the private integration workspace for invoking Jacquard from
other languages and connecting Jacquard programs to operating-system services.
It is deliberately separate from
[`jacquard-lang`](https://github.com/jbwinters/jacquard-lang), which remains the
language implementation and semantic source of truth.

Status: a Python adapter for Core's pinned `jacquard-host-v0` protocol. It
spawns one `jacquard host worker` per invocation, runs one checked Jacquard
term with typed arguments, and serves the term's declared `once` effect
operations from Python functions bound by exact identity. The strict
`stdio-u32-json-v0` codecs and a replayer for Core's conformance kit back it.
A bounded serial loopback socket host relays one raw request at a time to
such a term and writes back the bytes it returns; no HTTP semantics live in
the host, and no deployment surface exists yet.

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

## Calling Jacquard from Python, and Python from Jacquard

The adapter binds everything by content hash, never by display name (Core
decision D80). A host learns the hashes of its own declarations with the
public `jacquard hash` command; `jacquard_host.identities` wraps it and turns
a checked signature into the positional interface the protocol requires.

Suppose a first-party Jacquard file declares an effect whose operation the
host will implement, and a term that uses it:

```jacquard
once effect Documents where {
  read-pdf : (Text) -> Text
}

summarize : (Text) ->{Documents} Text
summarize(path) = text.slice(read-pdf(path), 0, 80)
```

After `jacquard run decls.jac --store STORE` has populated a store, Python
exposes `read_pdf` and runs `summarize`:

```python
from jacquard_host.adapter import Host
from jacquard_host.identities import hash_file, parse_signature, signatures
from jacquard_host.registry import HostFailure, OperationRegistry

prelude = "/path/to/jacquard-lang/prelude"      # the prelude of the pinned Core build
names = hash_file("decls.jac", prelude=prelude) | hash_file(f"{prelude}/01-prim.jqd")
registry = OperationRegistry()

@registry.operation(names["documents"], names["read-pdf"])
def read_pdf(path: str) -> str:
    if not path.startswith("/srv/docs/"):
        raise HostFailure("refused_authority", "not_started", "outside the document root")
    return extract_text(path)          # the host's own PDF code

host = Host("STORE")
interface = parse_signature(signatures("decls.jac")["summarize"], names)
result = host.invoke(names["summarize"], interface, ["/srv/docs/report.pdf"], registry)
if result.ok:
    print(result.value)                # the first 80 codepoints of the text
else:
    print(result.terminal, result.primary_code, result.diagnostics[0].summary)
```

`01-prim.jqd` declares `Int`, `Real`, and `Text`; a signature that mentions
another prelude type needs that type's file hashed too (`Hash` lives in
`19-audit.jqd`, `Option` and `Bool` in `02-data.jqd`).

When the running term reaches `read-pdf(path)`, the worker pauses and sends
an `effect_request`; the adapter dispatches it to `read_pdf` by the exact
`(Documents, read-pdf)` identity, and the returned text resumes the term. A
handler that raises `HostFailure` reports one of the four frozen failure
pairs; any other exception is reported as an unknown completion, which Core
never retries. Values crossing the boundary are `Int`, `Real`, `Text`,
`Hash`, tuples, and saturated constructors; operations are `once` and serial.
The result carries every Core frame, every host frame, the exit status, the
bounded operator text, and any host-owned evidence such as a deadline kill,
so a run can be audited without trusting the adapter's summary.

## The study persistence operation

`jacquard/study.jac` declares `once effect Study where { save-response :
(Text, Text, Text) -> SaveOutcome }` and `jacquard_host.study` implements it
behind that one identity: host-owned SQLite, one `BEGIN IMMEDIATE`
transaction per save, a deterministic SHA-256 receipt, idempotent repeats
(`Duplicate`), and `Conflict` for a different answer under the same
`(trial, session)`. Inputs are validated before any write and refused as
`refused_authority`; a failed write, or a failed commit whose transaction is
still open, is `outside_failure` and rolled back; a commit failure that
leaves no transaction behind is reported as unknown and never retried. Jacquard decides what each outcome
means. No SQL, handle, path, or participant identity crosses the boundary;
`session` is a synthetic application token.

```python
from jacquard_host.study import StudyIdentities, StudyStore, bind_study

with StudyStore("study.sqlite") as study:
    registry = OperationRegistry()
    bind_study(registry, StudyIdentities.from_names(names), study)
    result = host.invoke(names["record-response"], interface, ["trial-7", "sess-1", "B"], registry)
```

## The serial socket host

`jacquard_host.socket_host.SerialSocketHost` owns one loopback listener and
serves one connection at a time, whichever thread calls it. For each
connection it reads one bounded request (the head up to the first CRLFCRLF,
then exactly `Content-Length` more bytes when that header is present), hands
the raw bytes to one worker as
Latin-1 `Text` through `Host.invoke`, writes back the bytes the term returned,
and closes. It enforces byte and time ceilings for its own resource safety and
does nothing else: on a ceiling violation, an early disconnect, or a failed
invocation it closes the connection without writing a response and records
the reason as host-owned evidence. Every HTTP meaning, including error
responses, is produced by Jacquard code (roadmap H1.1).

```python
from jacquard_host.socket_host import Ceilings, SerialSocketHost

server = SerialSocketHost.for_term(host, names["serve"], interface, ceilings=Ceilings(max_request_bytes=65536))
print(server.address)          # ("127.0.0.1", <port>)
server.serve_forever()         # strictly one request at a time; server.stop() closes the listener and ends it
```

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
