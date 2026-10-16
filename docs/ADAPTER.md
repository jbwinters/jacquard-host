# Low-level adapter and local integrations

For the application-facing API, start with [the Python SDK](PYTHON-SDK.md)
and [the examples](../examples/README.md). This page documents exact protocol
values and the synthetic persistence/socket integration.

## Calling with exact protocol values

Install the Python 3.11+ library with `python3 -m pip install .`. It has no
third-party runtime dependencies. The wheel and source distribution include
the pinned conformance kit and the synthetic application's Jacquard sources;
installed code does not need a repository checkout. Core is installed
separately and must match [the compatibility pin](COMPATIBILITY.md).
The installed commands `jacquard-host-kit` and `jacquard-host-trial` expose the
same options as their `python3 -m jacquard_host.kit` and
`python3 -m jacquard_host.trial` counterparts.

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
    print(result.code, result.message)
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

`src/jacquard_host/data/jacquard/study.jac` declares `once effect Study where { save-response :
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

## The HTTP/1.1 subset in Jacquard

`src/jacquard_host/data/jacquard/http.jac` is the parser, validator, router, and response renderer
(roadmap H1.1), written entirely in Jacquard so that every HTTP meaning
stays in application code. It accepts GET and POST, an origin-form target,
HTTP/1.1, CRLF line endings, exactly one Host header, and an optional
explicit Content-Length body within fixed ceilings (32 header lines, 1024
bytes per line, 512-byte target, 65536-byte body). It rejects everything
else with a status the application renders: other methods (501), other
versions (505), bare LF or CR, obsolete line folding, repeated or missing
Host, repeated or malformed Content-Length, a body without a length or of
the wrong length, a GET with a body, Transfer-Encoding or Upgrade (501),
Expect (417), and oversized input (400 or 413). `http.route` selects a
handler by method and path and `http.path-known?` distinguishes 404 from
405; `http.render` always emits Content-Length and Connection: close.
Response bodies and header values must be codepoints 0..255, because the
host writes each codepoint as one byte (a `charset=utf-8` body must already
be UTF-8 bytes); the host refuses anything above. Response headers are
rendered verbatim, so first-party code must not put CR or LF in them.
`http.header` returns a value only when the header appears exactly once;
`http.header-values` lists every occurrence. `Host:` with an empty value and
obs-text bytes (0x80..0xff) in header values are accepted as RFC 9112 allows.
The host's request ceiling (default 65536 bytes, head included) bounds the
effective body size below the parser's own 65536-byte ceiling.

`src/jacquard_host/data/jacquard/http-tests.jac` is the Warp suite (47 cases and properties). Run
it against the concatenation of the two files, because Jacquard has no
import syntax:

```sh
mkdir -p .scratch
cat src/jacquard_host/data/jacquard/http.jac src/jacquard_host/data/jacquard/http-tests.jac > .scratch/http-all.jac
jacquard test .scratch/http-all.jac --seed 42
```

## The synthetic trial slice

`src/jacquard_host/data/jacquard/trial.jac` is the end-to-end proof (roadmap H1.3): `GET /trial`
returns one deterministic synthetic trial, `POST /response` validates a
`trial=…&session=…&answer=a|b` body (exactly those three keys, raw token
values with no URL encoding, each 1..128 characters starting alphanumeric,
the same rule the persistence operation enforces), calls `save-response`, and answers
`201 receipt=…`, `200` for an identical repeat, `409` for a different answer
under the same session, and Jacquard-authored `400`/`404`/`405`/`501`/`505`
for everything else. `jacquard_host.trial.TrialApp` assembles the three
Jacquard files, populates a store, binds the persistence operation, and
serves the term behind the socket host:

```sh
PYTHONPATH=src JACQUARD_BIN=/path/to/jacquard JACQUARD_PRELUDE=/path/to/prelude \
  python3 -m jacquard_host.trial --workdir .scratch/trial --port 8080
curl -s http://127.0.0.1:8080/trial
curl -s -d 'trial=trial-synthetic-1&session=sess-1&answer=b' http://127.0.0.1:8080/response
```

Every request runs in a fresh worker; every response, including every
error, is rendered by Jacquard (the host itself only ever closes a
connection, on its own ceilings or on an invocation failure); the only
outside action is the one named persistence operation. The work directory's
store is built once from the assembled source and its digest is recorded;
a later start with a different source is refused rather than repopulated in
place. This is synthetic data on loopback, not the readability study.
