"""The closed, configured operation registry (spec section 7.3 and 8).

Jacquard code declares a ``once`` effect whose operations have first-order
signatures; the host binds each exact ``(effect, operation)`` identity to a
native callable. When the running term reaches such an operation, Core sends
an ``effect_request``; the registry dispatches by identity only, never by
any name or string inside the arguments, calls the bound function with the
decoded positional arguments, and returns the typed response.

A handler reports a definite outside failure by raising :class:`HostFailure`
with one of the four allowed ``(category, completion)`` pairs. Any other
exception is reported as ``completion_unknown``/``unknown``: the host cannot
prove the action never started, and the protocol never retries an unknown
completion, so this is the conservative report. The message carried upstream
names only the exception class; handler authors must redact detail
themselves.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Callable, Iterable

from jacquard_host.protocol.envelopes import FAILURE_PAIRS, EffectRequest, OperationEntry
from jacquard_host.protocol.values import Hash

Handler = Callable[..., Any]


class HostFailure(Exception):
    """A deliberate, already-redacted failure report from a handler."""

    def __init__(self, category: str, completion: str, message: str = "") -> None:
        if (category, completion) not in FAILURE_PAIRS:
            raise ValueError(f"({category}, {completion}) is not an allowed effect_failure pair")
        if not isinstance(message, str):
            raise ValueError("a host failure message must be a str (already redacted)")
        super().__init__(f"{category}/{completion}: {message}")
        self.category = category
        self.completion = completion
        self.message = message


@dataclass(frozen=True)
class Response:
    """What the host will send for one request: a value, or a failure report."""

    value: Any = None
    failure: HostFailure | None = None

    @property
    def ok(self) -> bool:
        return self.failure is None


@dataclass(frozen=True)
class Binding:
    effect: Hash
    operation: Hash
    handler: Handler
    name: str


class OperationRegistry:
    """Exact identities bound to native functions; nothing else is reachable."""

    def __init__(self) -> None:
        self._bindings: dict[tuple[Hash, Hash], Binding] = {}

    def bind(self, effect: Hash | str, operation: Hash | str, handler: Handler, *, name: str | None = None) -> Binding:
        effect = effect if isinstance(effect, Hash) else Hash(effect)
        operation = operation if isinstance(operation, Hash) else Hash(operation)
        key = (effect, operation)
        if key in self._bindings:
            raise ValueError("that operation identity is already bound")
        binding = Binding(effect, operation, handler, name or getattr(handler, "__name__", "<handler>"))
        self._bindings[key] = binding
        return binding

    def operation(self, effect: Hash | str, operation: Hash | str, *, name: str | None = None) -> Callable[[Handler], Handler]:
        """Decorator form of :meth:`bind`."""

        def decorate(handler: Handler) -> Handler:
            self.bind(effect, operation, handler, name=name)
            return handler

        return decorate

    def __len__(self) -> int:
        return len(self._bindings)

    def __iter__(self) -> Iterable[Binding]:
        return iter(sorted(self._bindings.values(), key=lambda b: (b.effect, b.operation)))

    def entries(self) -> list[OperationEntry]:
        return [OperationEntry(binding.effect, binding.operation) for binding in self]

    def effects(self) -> tuple[Hash, ...]:
        return tuple(sorted({binding.effect for binding in self._bindings.values()}))

    def dispatch(self, request: EffectRequest) -> Response:
        """Run the bound handler for ``request`` and classify what happened."""
        binding = self._bindings.get((request.effect, request.operation))
        if binding is None:
            return Response(failure=HostFailure("unsupported_operation", "not_started", "operation is not configured"))
        try:
            return Response(value=binding.handler(*request.arguments))
        except HostFailure as failure:
            return Response(failure=failure)
        except Exception as error:  # noqa: BLE001 - every handler failure must be reported, never raised
            return Response(
                failure=HostFailure("completion_unknown", "unknown", f"handler raised {type(error).__name__}")
            )
