"""The host side of one ``jacquard-host-v0`` worker lifetime (spec sections 7 to 10).

The session is a pure state machine: it never reads or writes a carrier. The
worker wrapper feeds it every Core frame through :meth:`HostSession.receive`
and asks it to build every host frame; each builder refuses, with the E1608
the protocol requires, any frame that the current state does not permit,
including anything after a terminal frame has been observed. A refused frame
is never written.
"""

from __future__ import annotations

from enum import Enum
from typing import Any, Mapping, Sequence

from jacquard_host.protocol import envelopes
from jacquard_host.protocol.envelopes import (
    CoreFrame,
    CoreHello,
    EffectRequest,
    Fatal,
    Interface,
    OperationEntry,
    Outcome,
    ShutdownAck,
)
from jacquard_host.protocol.errors import LimitExceeded, OrderViolation
from jacquard_host.protocol.limits import HARD_LIMITS, validate_limits
from jacquard_host.protocol.values import Hash


class State(Enum):
    HELLO = "hello"  # waiting for core_hello
    NEGOTIATING = "negotiating"  # core_hello received, host_select not yet sent
    SELECTED = "selected"  # host may invoke or shut down
    SHUTTING_DOWN = "shutting_down"  # shutdown sent, expecting shutdown_ack
    RUNNING = "running"  # invoke sent, Core computing
    WAITING = "waiting"  # effect_request received, host owes one response
    FINISHING = "finishing"  # failure or cancel sent; only a terminal frame may follow
    CLOSED = "closed"  # a terminal frame was observed


class HostSession:
    def __init__(self) -> None:
        self.state = State.HELLO
        self.limits: dict[str, int] = dict(HARD_LIMITS)
        self.hello: CoreHello | None = None
        self.requests: list[EffectRequest] = []
        self.responses: list[dict[str, Any]] = []
        self.terminal: Outcome | Fatal | ShutdownAck | None = None
        self._pending: EffectRequest | None = None

    # --- Core -> host ---------------------------------------------------

    @property
    def pending(self) -> EffectRequest | None:
        return self._pending

    def receive(self, frame: dict[str, Any]) -> CoreFrame:
        """Validate one Core frame against the state machine and record it."""
        if self.state is State.CLOSED:
            raise OrderViolation("Core sent a frame after its terminal frame")
        if self.state is State.HELLO:
            hello = envelopes.parse_core_hello(frame)
            if envelopes.PROTOCOL not in hello.versions:
                raise OrderViolation("Core does not offer jacquard-host-v0", code="E1600")
            self.hello = hello
            self.state = State.NEGOTIATING
            return hello
        parsed = envelopes.parse_core_frame(frame, limits=self.limits)
        if isinstance(parsed, ShutdownAck):
            if self.state is not State.SHUTTING_DOWN:
                raise OrderViolation("shutdown_ack arrived without a shutdown")
            self._close(parsed)
            return parsed
        if isinstance(parsed, Fatal):
            if self.state not in (State.NEGOTIATING, State.SELECTED, State.SHUTTING_DOWN, State.RUNNING, State.FINISHING):
                raise OrderViolation("fatal arrived while a response was outstanding")
            self._close(parsed)
            return parsed
        if isinstance(parsed, Outcome):
            if self.state not in (State.RUNNING, State.WAITING, State.FINISHING):
                raise OrderViolation("outcome arrived without a live invocation")
            self._close(parsed)
            return parsed
        if isinstance(parsed, EffectRequest):
            if self.state is State.FINISHING:
                raise OrderViolation("effect_request arrived after the host closed the invocation with a failure or cancel")
            if self.state is not State.RUNNING:
                raise OrderViolation("effect_request arrived while another request was outstanding or no invocation was live")
            expected = len(self.requests) + 1
            if parsed.ordinal != expected:
                raise OrderViolation(f"request ordinal {parsed.ordinal} is not the expected {expected}")
            if expected > self.limits["max_effect_requests"]:
                raise LimitExceeded("Core exceeded the selected max_effect_requests")
            self.requests.append(parsed)
            self._pending = parsed
            self.state = State.WAITING
            return parsed
        raise OrderViolation("unexpected Core frame")

    def _close(self, terminal: Outcome | Fatal | ShutdownAck) -> None:
        self.terminal = terminal
        self._pending = None
        self.state = State.CLOSED

    # --- host -> Core ---------------------------------------------------

    def _require(self, *states: State, action: str) -> None:
        if self.state is State.CLOSED:
            raise OrderViolation(f"{action} refused locally: a terminal frame was already observed")
        if self.state not in states:
            raise OrderViolation(f"{action} is not permitted in state {self.state.value}")

    def select(self, limits: Mapping[str, int] | None = None) -> dict[str, Any]:
        self._require(State.NEGOTIATING, action="host_select")
        selected = validate_limits(dict(limits) if limits is not None else dict(HARD_LIMITS))
        frame = envelopes.host_select(selected)
        self.limits = selected
        self.state = State.SELECTED
        return frame

    def shutdown(self) -> dict[str, Any]:
        self._require(State.SELECTED, action="shutdown")
        self.state = State.SHUTTING_DOWN
        return envelopes.shutdown()

    def invoke(
        self,
        target: Hash,
        interface: Interface,
        arguments: Sequence[Any],
        operations: Sequence[OperationEntry] = (),
    ) -> dict[str, Any]:
        self._require(State.SELECTED, action="invoke")
        frame = envelopes.invoke(target, interface, arguments, operations, limits=self.limits)
        self.state = State.RUNNING
        return frame

    def _respond(self, frame: dict[str, Any], *, terminal: bool) -> dict[str, Any]:
        self.responses.append(frame)
        self._pending = None
        # a failure or cancel closes the slot for good: Core answers with one outcome
        self.state = State.FINISHING if terminal else State.RUNNING
        return frame

    def respond_ok(self, value: Any) -> dict[str, Any]:
        self._require(State.WAITING, action="effect_ok")
        assert self._pending is not None
        return self._respond(envelopes.effect_ok(self._pending.ordinal, value, limits=self.limits), terminal=False)

    def respond_failure(self, category: str, completion: str, message: str) -> dict[str, Any]:
        self._require(State.WAITING, action="effect_failure")
        assert self._pending is not None
        return self._respond(
            envelopes.effect_failure(self._pending.ordinal, category, completion, message, limits=self.limits),
            terminal=True,
        )

    def cancel(self, reason: str, completion: str) -> dict[str, Any]:
        self._require(State.WAITING, action="cancel")
        assert self._pending is not None
        return self._respond(envelopes.cancel(self._pending.ordinal, reason, completion), terminal=True)
