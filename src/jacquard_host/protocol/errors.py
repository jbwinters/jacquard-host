"""Fail-closed protocol errors, labelled with the stable E16xx codes.

Every error here is a host-side classification. A code carried by a
:class:`ProtocolError` says which spec condition the host observed locally;
it is never presented as a Core diagnostic, which only Core frames carry.
"""

from __future__ import annotations


class ProtocolError(Exception):
    """A host-local, fail-closed protocol condition."""

    code: str = "E1601"

    def __init__(self, message: str, *, code: str | None = None) -> None:
        super().__init__(message)
        if code is not None:
            self.code = code
        self.message = message

    def __str__(self) -> str:
        return f"{self.code}: {self.message}"


class MalformedFrame(ProtocolError):
    """Framing, UTF-8, JSON, or envelope shape is invalid (E1601)."""

    code = "E1601"


class LimitExceeded(ProtocolError):
    """A hard or selected limit was exceeded (E1602)."""

    code = "E1602"


class CarrierLoss(ProtocolError):
    """EOF or an I/O failure prevented a complete frame (E1611).

    This is host-owned carrier evidence. It must not be reported as if Core
    had emitted it.
    """

    code = "E1611"


class OrderViolation(ProtocolError):
    """A message would violate the protocol state machine (E1608)."""

    code = "E1608"
