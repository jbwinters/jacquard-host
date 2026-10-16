"""Application-facing failures; Core and host evidence remain separate."""

from __future__ import annotations

from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from jacquard_host.adapter import InvocationResult


class JacquardError(Exception):
    """Base class for errors exposed by the convenience API."""


class PreparationError(JacquardError):
    """A source, runtime, or prepared artifact could not be used."""


class BindingError(JacquardError, ValueError):
    """A function, data mapping, or handler configuration is incomplete."""


class InvalidArgument(JacquardError, ValueError):
    """A Python argument cannot cross the selected boundary; no call began."""


class InvocationError(JacquardError):
    """A failed invocation, with the complete original result available."""

    def __init__(self, result: InvocationResult) -> None:
        self.result = result
        self.code = result.code
        self.origin = "core" if result.diagnostics else "host"
        super().__init__(result.message)


class DeadlineExceeded(InvocationError):
    """The carrier budget expired; synchronous handlers are not preempted."""


class WorkerLost(InvocationError):
    """No trustworthy Core terminal result was available."""


class OperationFailed(InvocationError):
    """A handler reported failure or unknown completion. Nothing is retried."""


def invocation_error(result: InvocationResult) -> InvocationError:
    if result.host_evidence.get("reason") == "deadline":
        return DeadlineExceeded(result)
    if result.kind == "carrier_loss" or result.host_evidence.get("reason") == "worker_spawn_failed":
        return WorkerLost(result)
    if result.terminal in ("host_failure", "completion_unknown"):
        return OperationFailed(result)
    return InvocationError(result)
