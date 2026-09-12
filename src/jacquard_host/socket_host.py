"""A bounded, serial, loopback socket host (roadmap H1.0).

The host owns one listening socket and serves exactly one connection at a
time. For each connection it reads one bounded request, hands the raw bytes
to one Jacquard invocation through :class:`~jacquard_host.adapter.Host`,
writes back whatever bytes the term returned, and closes. It imposes byte,
count, and time ceilings for its own resource safety and nothing else: it
does not route, does not validate HTTP, and never produces an HTTP response
of its own. When a ceiling is exceeded, the client disconnects early, or the
invocation fails, the host simply closes the connection and records the
reason as host-owned evidence. HTTP meaning stays in Jacquard code.

Bytes cross the v0 boundary as ``Text`` under the Latin-1 mapping: request
byte ``b`` becomes codepoint ``b`` and a response codepoint ``c`` (which must
be below 256) becomes byte ``c``. This is lossless, keeps every byte
inspectable from Jacquard, and needs no new boundary value.

Request framing is the one piece of HTTP the host must look at to know when
a request ends: it reads the head up to the first CRLFCRLF, then exactly
``Content-Length`` more bytes when that header is present. The host frames
leniently and the Jacquard parser judges: the first ``Content-Length`` wins
when the header repeats, leading zeros are read as their value, and a head
that carries ``Transfer-Encoding`` is not framed at all (the head alone is
passed through). ``jacquard/http.jac`` rejects every one of those shapes, so
a request the host framed leniently never reaches an application handler.
There is no keep-alive: bytes left unread when the connection closes are
dropped and never parsed as a second request.
"""

from __future__ import annotations

import re
import selectors
import socket
import threading
import time
from dataclasses import dataclass
from typing import Any, Callable

from jacquard_host.adapter import Host, InvocationResult
from jacquard_host.protocol.envelopes import Interface
from jacquard_host.protocol.values import Hash
from jacquard_host.registry import OperationRegistry

HEAD_END = b"\r\n\r\n"
# at most 18 digits: anything longer cannot fit any ceiling and is refused before conversion
_CONTENT_LENGTH = re.compile(rb"^content-length:[ \t]*([0-9]{1,18})[ \t]*\r?$", re.IGNORECASE | re.MULTILINE)
_CONTENT_LENGTH_ANY = re.compile(rb"^content-length:", re.IGNORECASE | re.MULTILINE)
_TRANSFER_ENCODING = re.compile(rb"^transfer-encoding:", re.IGNORECASE | re.MULTILINE)
# Core's Text boundary: every request byte may become two UTF-8 bytes
MAX_REQUEST_BYTES_CEILING = 131072


@dataclass(frozen=True)
class Ceilings:
    """Resource ceilings the host enforces without interpreting the request.

    The worker's own deadline belongs to :class:`~jacquard_host.adapter.Host`
    (``deadline_seconds``), not here.
    """

    max_request_bytes: int = 65536
    max_head_bytes: int = 16384
    head_timeout: float = 5.0
    request_timeout: float = 10.0
    response_timeout: float = 10.0
    max_served_records: int = 256

    def __post_init__(self) -> None:
        if not 1 <= self.max_request_bytes <= MAX_REQUEST_BYTES_CEILING:
            raise ValueError(f"max_request_bytes must be 1..{MAX_REQUEST_BYTES_CEILING} so the request fits Core's Text boundary")
        if self.max_head_bytes < 1 or self.max_served_records < 1:
            raise ValueError("ceilings must be positive")
        for name in ("head_timeout", "request_timeout", "response_timeout"):
            if getattr(self, name) <= 0:
                raise ValueError(f"{name} must be positive")


@dataclass
class Served:
    """Host-owned evidence for one accepted connection."""

    peer: tuple[str, int]
    outcome: str  # served | truncated | oversized | timeout | invocation_failed | disconnected | response_unencodable | host_error
    request_bytes: int = 0
    response_bytes: int = 0
    invocation: InvocationResult | None = None
    detail: str = ""
    elapsed_seconds: float = 0.0


class RequestError(Exception):
    def __init__(self, outcome: str, detail: str = "") -> None:
        super().__init__(detail or outcome)
        self.outcome = outcome
        self.detail = detail


def text_of_bytes(data: bytes) -> str:
    return data.decode("latin-1")


def bytes_of_text(text: str) -> bytes:
    try:
        return text.encode("latin-1")
    except UnicodeEncodeError:
        raise RequestError("response_unencodable", "the term returned a codepoint above 255") from None


def read_request(conn: socket.socket, ceilings: Ceilings) -> bytes:
    """Read one bounded request: the head, then an explicit Content-Length body."""
    started = time.monotonic()
    buffer = bytearray()

    def read_more(limit: int, deadline: float, phase: str) -> None:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise RequestError("timeout", phase)
        conn.settimeout(remaining)
        try:
            chunk = conn.recv(min(65536, limit - len(buffer)))
        except socket.timeout:
            raise RequestError("timeout", phase) from None
        except OSError as error:
            raise RequestError("disconnected", f"{phase}: {error.__class__.__name__}") from None
        if not chunk:
            raise RequestError("truncated", phase)
        buffer.extend(chunk)

    head_deadline = started + ceilings.head_timeout
    while HEAD_END not in buffer:
        if len(buffer) >= min(ceilings.max_head_bytes, ceilings.max_request_bytes):
            raise RequestError("oversized", "head")
        read_more(min(ceilings.max_head_bytes, ceilings.max_request_bytes), head_deadline, "head")
    head_end = buffer.index(HEAD_END) + len(HEAD_END)
    head = bytes(buffer[:head_end])
    body_length = 0
    if not _TRANSFER_ENCODING.search(head):
        match = _CONTENT_LENGTH.search(head)
        if match:
            body_length = int(match.group(1))
        elif _CONTENT_LENGTH_ANY.search(head):
            # a Content-Length the host cannot frame (too many digits, not digits) cannot be read safely
            raise RequestError("oversized", "unframeable Content-Length")
    total = head_end + body_length
    if total > ceilings.max_request_bytes:
        raise RequestError("oversized", "body")
    request_deadline = started + ceilings.request_timeout
    while len(buffer) < total:
        read_more(total, request_deadline, "body")
    return bytes(buffer[:total])


Invoker = Callable[[str], InvocationResult]


class SerialSocketHost:
    """One loopback listener; one connection at a time; one worker per request."""

    def __init__(
        self,
        invoker: Invoker,
        *,
        bind: tuple[str, int] = ("127.0.0.1", 0),
        ceilings: Ceilings = Ceilings(),
        backlog: int = 1,
    ) -> None:
        if bind[0] not in ("127.0.0.1", "::1", "localhost"):
            raise ValueError("the serial socket host binds loopback only; public binding is separate deployment work")
        self._invoker = invoker
        self._ceilings = ceilings
        self._listener = socket.socket(socket.AF_INET6 if bind[0] == "::1" else socket.AF_INET, socket.SOCK_STREAM)
        try:
            self._listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            self._listener.bind(bind)
            self._listener.listen(backlog)
            self._listener.setblocking(False)
        except OSError:
            self._listener.close()
            raise
        self._stop = threading.Event()
        self._serving = threading.Lock()  # one connection at a time, whoever calls
        self.served: list[Served] = []

    @classmethod
    def for_term(
        cls,
        host: Host,
        target: Hash | str,
        interface: Interface,
        registry: OperationRegistry | None = None,
        **options: Any,
    ) -> "SerialSocketHost":
        """Serve requests by invoking ``target : (Text) ->{...} Text`` once per request."""
        registry = registry if registry is not None else OperationRegistry()

        def invoke(request_text: str) -> InvocationResult:
            return host.invoke(target, interface, (request_text,), registry)

        return cls(invoke, **options)

    @property
    def address(self) -> tuple[str, int]:
        return self._listener.getsockname()[:2]

    def stop(self) -> None:
        """Stop accepting and close the listener; a queued connection is reset, not served."""
        self.close()

    def close(self) -> None:
        self._stop.set()
        try:
            self._listener.close()
        except OSError:
            pass

    def __enter__(self) -> "SerialSocketHost":
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()

    # --- serving -------------------------------------------------------

    def serve_one(self, *, accept_timeout: float | None = None) -> Served | None:
        """Accept and serve one connection; None if stopped or nothing arrived in time.

        Calls from several threads are serialised: the listener serves one
        connection at a time whoever is calling.
        """
        deadline = None if accept_timeout is None else time.monotonic() + accept_timeout
        with self._serving:
            if self._stop.is_set():
                return None
            try:
                with selectors.DefaultSelector() as selector:
                    selector.register(self._listener, selectors.EVENT_READ)
                    while not self._stop.is_set():
                        wait = 0.25 if deadline is None else max(0.0, min(0.25, deadline - time.monotonic()))
                        if selector.select(wait):
                            try:
                                conn, peer = self._listener.accept()
                            except OSError:
                                continue
                            return self._serve(conn, peer[:2])
                        if deadline is not None and time.monotonic() >= deadline:
                            return None
            except (OSError, ValueError):
                # the listener was closed underneath a blocked accept
                return None
        return None

    def serve_forever(self) -> None:
        while not self._stop.is_set():
            self.serve_one()

    def _serve(self, conn: socket.socket, peer: tuple[str, int]) -> Served:
        started = time.monotonic()
        served = Served(peer=peer, outcome="served")
        try:
            with conn:
                try:
                    request = read_request(conn, self._ceilings)
                    served.request_bytes = len(request)
                    result = self._invoker(text_of_bytes(request))
                    served.invocation = result
                    if not result.ok:
                        raise RequestError("invocation_failed", f"{result.kind}:{result.terminal or ''}:{result.primary_code or ''}")
                    if not isinstance(result.value, str):
                        raise RequestError("response_unencodable", "the term did not return Text")
                    response = bytes_of_text(result.value)
                    conn.settimeout(self._ceilings.response_timeout)
                    try:
                        conn.sendall(response)
                    except socket.timeout:
                        raise RequestError("timeout", "response") from None
                    except OSError as error:
                        raise RequestError("disconnected", f"response: {error.__class__.__name__}") from None
                    served.response_bytes = len(response)
                    try:
                        conn.shutdown(socket.SHUT_RDWR)
                    except OSError:
                        pass  # the peer already went away after the full response was written
                except RequestError as error:
                    served.outcome = error.outcome
                    served.detail = error.detail
                except Exception as error:  # noqa: BLE001 - a client must never take the host down
                    served.outcome = "host_error"
                    served.detail = f"{type(error).__name__}"
        finally:
            served.elapsed_seconds = time.monotonic() - started
            self.served.append(served)
            del self.served[: -self._ceilings.max_served_records]
        return served
