"""Invoke one checked Jacquard term from Python and serve its host operations.

This is the embedding entry point. :class:`Host` starts one worker per
invocation against a local Core store, negotiates limits, sends the exact
invoke envelope, answers every ``effect_request`` through the closed
:class:`~jacquard_host.registry.OperationRegistry`, and returns the single
terminal result together with everything a caller needs to audit the run:
the Core frames, the host frames, the exit status, the bounded operator
text, and any host-owned evidence (deadline expiry, carrier loss).

Nothing here retries, and nothing here turns a missing terminal frame into a
Core verdict.
"""

from __future__ import annotations

import os
import time
from dataclasses import dataclass, field
from typing import Any, Mapping, Sequence

from jacquard_host.protocol.envelopes import (
    CoreHello,
    Diagnostic,
    EffectRequest,
    Fatal,
    Interface,
    Outcome,
    ShutdownAck,
)
from jacquard_host.protocol.errors import CarrierLoss, ProtocolError
from jacquard_host.protocol.framing import validate_deadline_seconds
from jacquard_host.protocol.limits import HARD_LIMITS, validate_limits
from jacquard_host.protocol.values import Hash
from jacquard_host.registry import OperationRegistry
from jacquard_host.session import HostSession, State
from jacquard_host.worker import DEFAULT_COMMAND, WorkerProcess


class _HostRefused(Exception):
    """The host refused its own first action; carries the underlying error."""

    def __init__(self, error: ProtocolError) -> None:
        super().__init__(str(error))
        self.error = error


@dataclass
class InvocationResult:
    """The terminal result of one worker lifetime.

    ``kind`` is one of:

    - ``ok``: an ``outcome`` with a successful value in ``value``;
    - ``error``: an ``outcome`` whose result is diagnostics (``terminal`` says
      whether the cause was a Core diagnostic, a host failure, a cancellation,
      or an unknown completion);
    - ``fatal``: Core refused the negotiation or the invoke before evaluation;
    - ``shutdown``: a pre-invocation shutdown was acknowledged;
    - ``carrier_loss``: no trustworthy terminal frame arrived (host-owned
      evidence in ``host_evidence``; never a Core verdict);
    - ``protocol_error``: Core sent something the host refused locally;
    - ``host_error``: the host could not build its own invoke (an argument
      with no v0 encoding, a registry entry outside the interface row); the
      worker was shut down cleanly before any invocation began.

    A worker that cannot be spawned at all raises ``OSError`` from the
    calling method; no result is fabricated for it.
    """

    kind: str
    value: Any = None
    diagnostics: tuple[Diagnostic, ...] = ()
    terminal: str | None = None
    evidence: dict[str, Any] | None = None
    core_frames: list[dict[str, Any]] = field(default_factory=list)
    host_frames: list[dict[str, Any]] = field(default_factory=list)
    requests: list[EffectRequest] = field(default_factory=list)
    exit_status: int | None = None
    signal: int | None = None
    stderr: bytes = b""
    host_evidence: dict[str, Any] = field(default_factory=dict)

    @property
    def ok(self) -> bool:
        return self.kind == "ok"

    @property
    def primary_code(self) -> str | None:
        return self.diagnostics[0].code if self.diagnostics else None

    @property
    def code(self) -> str | None:
        """One accessor for Core diagnostics and separately labeled host codes."""
        return self.primary_code or self.host_evidence.get("code")

    @property
    def message(self) -> str:
        """A useful message even when a failure has no Core diagnostic."""
        if self.ok:
            return "Jacquard invocation succeeded"
        if self.diagnostics:
            diagnostic = self.diagnostics[0]
            detail = f"{diagnostic.summary} {diagnostic.cause}"
        else:
            detail = self.host_evidence.get("detail") or self.host_evidence.get("reason") or self.kind
            if self.kind == "carrier_loss" and self.stderr:
                detail += ": " + self.stderr[:4096].decode("utf-8", "replace").strip()
        return f"{self.code or self.kind}: {detail}"

    def unwrap(self) -> Any:
        """Return the successful value or raise an auditable InvocationError."""
        if not self.ok:
            from jacquard_host.errors import invocation_error
            raise invocation_error(self)
        return self.value


def _bounded_message(message: str, max_bytes: int) -> str:
    """Cut a host-authored message at a scalar boundary so it fits the selected limit.

    The message is the host's own redacted detail, so truncating it is the
    host's business; a report that Core would refuse for length is worse than
    a shorter one.
    """
    encoded = message.encode("utf-8", "replace")
    if len(encoded) <= max_bytes:
        return encoded.decode("utf-8")
    return encoded[:max_bytes].decode("utf-8", "ignore")


class Host:
    """A configured way to run terms from one local Core store."""

    def __init__(
        self,
        store: str | os.PathLike[str],
        *,
        command: Sequence[str] = DEFAULT_COMMAND,
        limits: Mapping[str, int] | None = None,
        deadline_seconds: float | None = 30.0,
        env: Mapping[str, str] | None = None,
    ) -> None:
        validate_deadline_seconds(deadline_seconds)
        self.store = os.fspath(store)
        self.command = tuple(command)
        self.limits = validate_limits(dict(limits) if limits is not None else dict(HARD_LIMITS))
        self.deadline_seconds = deadline_seconds
        self.env = None if env is None else dict(env)

    # --- public API -------------------------------------------------------

    def invoke(
        self,
        target: Hash | str,
        interface: Interface,
        arguments: Sequence[Any] = (),
        registry: OperationRegistry | None = None,
    ) -> InvocationResult:
        """Run ``target`` once with ``arguments``, serving its operations from ``registry``."""
        target = target if isinstance(target, Hash) else Hash(target)
        registry = registry if registry is not None else OperationRegistry()
        return self._run(lambda session: session.invoke(target, interface, arguments, registry.entries()), registry)

    def shutdown_probe(self) -> InvocationResult:
        """Negotiate and immediately shut down; proves the worker and store are reachable."""
        return self._run(lambda session: session.shutdown(), OperationRegistry())

    # --- the lifetime -----------------------------------------------------

    def _run(self, first_action, registry: OperationRegistry) -> InvocationResult:
        session = HostSession()
        result = InvocationResult(kind="carrier_loss")
        started = time.monotonic()
        with WorkerProcess(self.store, command=self.command, deadline_seconds=self.deadline_seconds, env=self.env) as worker:
            try:
                self._exchange(session, worker, registry, result, first_action)
            except TimeoutError:
                result.kind = "carrier_loss"
                result.host_evidence = {
                    "schema": "jacquard-host-python-evidence-v0",
                    "code": "E1611",
                    "reason": "deadline",
                    "state": session.state.value,
                    "outstanding_request": session.pending.ordinal if session.pending else None,
                    "elapsed_seconds": time.monotonic() - started,
                }
                worker.kill()
            except CarrierLoss as loss:
                result.kind = "carrier_loss"
                result.host_evidence = {
                    "schema": "jacquard-host-python-evidence-v0",
                    "code": "E1611",
                    "reason": "carrier_loss",
                    "detail": loss.message,
                    "state": session.state.value,
                    "outstanding_request": session.pending.ordinal if session.pending else None,
                }
            except _HostRefused as refused:
                result.kind = "host_error"
                result.host_evidence = {
                    "schema": "jacquard-host-python-evidence-v0",
                    "code": refused.error.code,
                    "reason": "invoke_refused_locally" if session.state is State.CLOSED else "host_frame_refused",
                    "detail": refused.error.message,
                    "state": session.state.value,
                }
                if session.state is not State.CLOSED:
                    worker.kill()
            except ProtocolError as error:
                result.kind = "protocol_error"
                result.host_evidence = {
                    "schema": "jacquard-host-python-evidence-v0",
                    "code": error.code,
                    "reason": "core_frame_refused",
                    "detail": error.message,
                    "state": session.state.value,
                }
                worker.kill()
            finally:
                worker.close_input()
                worker.wait(timeout=5.0)
        result.requests = list(session.requests)
        result.exit_status = worker.exit_status
        result.signal = worker.signal
        result.stderr = worker.stderr
        return result

    def _send(self, session_frame: dict[str, Any], worker: WorkerProcess, result: InvocationResult, limits: Mapping[str, int]) -> None:
        result.host_frames.append(session_frame)
        try:
            worker.write_frame(session_frame, limits)
        except CarrierLoss:
            raise
        except ProtocolError as error:
            # the host's own frame does not fit the selected limits; Core did nothing wrong
            result.host_frames.pop()
            raise _HostRefused(error) from None

    def _exchange(self, session: HostSession, worker: WorkerProcess, registry: OperationRegistry, result: InvocationResult, first_action) -> None:
        hello = worker.read_frame(HARD_LIMITS)
        result.core_frames.append(hello)
        parsed = session.receive(hello)
        assert isinstance(parsed, CoreHello)
        self._send(session.select(self.limits), worker, result, HARD_LIMITS)
        try:
            first = first_action(session)
        except ProtocolError as error:
            # the host's own frame was unbuildable; Core did nothing wrong, so
            # leave the worker with a clean pre-invocation shutdown
            self._send(session.shutdown(), worker, result, session.limits)
            frame = worker.read_frame(session.limits)
            result.core_frames.append(frame)
            session.receive(frame)
            worker.close_input()
            raise _HostRefused(error) from None
        self._send(first, worker, result, session.limits)
        while session.state is not State.CLOSED:
            frame = worker.read_frame(session.limits)
            result.core_frames.append(frame)
            parsed = session.receive(frame)
            if isinstance(parsed, EffectRequest):
                response = registry.dispatch(parsed)
                if response.ok:
                    try:
                        reply = session.respond_ok(response.value)
                    except ProtocolError as error:
                        # the handler returned something with no v0 encoding; report it, never crash
                        message = _bounded_message(f"host result has no v0 encoding ({error.code})", session.limits["max_host_message_bytes"])
                        reply = session.respond_failure("outside_failure", "failed", message)
                else:
                    failure = response.failure
                    assert failure is not None
                    message = _bounded_message(failure.message, session.limits["max_host_message_bytes"])
                    reply = session.respond_failure(failure.category, failure.completion, message)
                self._send(reply, worker, result, session.limits)
            elif isinstance(parsed, Outcome):
                result.kind = "ok" if parsed.ok else "error"
                result.value = parsed.value
                result.diagnostics = parsed.diagnostics
                result.terminal = parsed.terminal
                result.evidence = parsed.evidence
            elif isinstance(parsed, Fatal):
                result.kind = "fatal"
                result.diagnostics = parsed.diagnostics
                result.terminal = "fatal"
            elif isinstance(parsed, ShutdownAck):
                result.kind = "shutdown"
                result.terminal = "shutdown"
        worker.close_input()
