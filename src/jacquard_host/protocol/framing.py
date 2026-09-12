"""``stdio-u32-json-v0`` frames (spec section 3).

A frame is a four-byte unsigned big-endian payload length followed by exactly
that many bytes. The length must lie in ``1..max_frame_bytes``; a length
outside that range is refused before any payload byte is read (E1602). EOF
inside a prefix or payload is carrier loss (E1611), which is host-owned
evidence rather than a Core result.
"""

from __future__ import annotations

import os
import select
import time
from typing import BinaryIO

from jacquard_host.protocol.errors import CarrierLoss, LimitExceeded

PREFIX_BYTES = 4


def frame_bytes(payload: bytes, *, max_frame_bytes: int) -> bytes:
    """Prefix ``payload`` with its length, refusing out-of-range sizes."""
    length = len(payload)
    if length < 1 or length > max_frame_bytes:
        raise LimitExceeded("frame payload length is outside 1..max_frame_bytes")
    return length.to_bytes(PREFIX_BYTES, "big") + payload


def write_frame(stream: BinaryIO, payload: bytes, *, max_frame_bytes: int) -> None:
    """Write and flush one complete frame."""
    data = frame_bytes(payload, max_frame_bytes=max_frame_bytes)
    try:
        stream.write(data)
        stream.flush()
    except (BrokenPipeError, OSError) as error:
        raise CarrierLoss(f"the carrier refused a write: {error.__class__.__name__}") from None


class FrameReader:
    """Reads frames from a file descriptor with an optional absolute deadline.

    The reader owns its own buffer so that ``select`` can be used for the
    deadline without fighting a buffered file object.
    """

    def __init__(self, fd: int, *, deadline: float | None = None) -> None:
        self._fd = fd
        self._deadline = deadline
        self._buffer = bytearray()
        self._eof = False

    def _fill(self, wanted: int) -> None:
        while len(self._buffer) < wanted and not self._eof:
            if self._deadline is not None:
                remaining = self._deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError("frame read deadline expired")
                ready, _, _ = select.select([self._fd], [], [], remaining)
                if not ready:
                    raise TimeoutError("frame read deadline expired")
            try:
                chunk = os.read(self._fd, 65536)
            except OSError as error:
                raise CarrierLoss(f"the carrier read failed: {error.__class__.__name__}") from None
            if not chunk:
                self._eof = True
            else:
                self._buffer.extend(chunk)

    def read_exactly(self, count: int) -> bytes:
        self._fill(count)
        if len(self._buffer) < count:
            raise CarrierLoss("the carrier ended before the declared frame completed")
        data = bytes(self._buffer[:count])
        del self._buffer[:count]
        return data

    def at_eof(self) -> bool:
        """True when no byte is buffered and the descriptor has reported EOF."""
        if self._buffer:
            return False
        try:
            self._fill(1)
        except TimeoutError:
            return False
        return self._eof and not self._buffer

    def read_frame(self, *, max_frame_bytes: int) -> bytes:
        """Read one payload, or raise :class:`CarrierLoss` / :class:`LimitExceeded`."""
        prefix = self.read_exactly(PREFIX_BYTES)
        length = int.from_bytes(prefix, "big")
        if length < 1 or length > max_frame_bytes:
            raise LimitExceeded("frame length prefix is outside 1..max_frame_bytes")
        return self.read_exactly(length)
