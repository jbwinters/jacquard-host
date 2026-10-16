"""One ``jacquard host worker`` process behind pipes.

The wrapper owns the process, its three descriptors, a bounded stderr
capture, and a host-owned deadline. It reads and writes frames but decides
nothing about their meaning; that is the session's job. Deadline expiry and
carrier loss are host-owned evidence: the wrapper records them and kills the
worker, and never fabricates a Core frame.
"""

from __future__ import annotations

import os
import selectors
import subprocess
import threading
import time
from typing import Any, BinaryIO, Mapping, Sequence

from jacquard_host.protocol.errors import CarrierLoss
from jacquard_host.protocol.framing import FrameReader, frame_bytes, validate_deadline_seconds, write_bytes
from jacquard_host.protocol.json_codec import decode_object, encode_object, validate_object

DEFAULT_COMMAND: tuple[str, ...] = ("jacquard",)
MAX_STDERR_BYTES = 65536


class _StderrCapture:
    """Drain operator text without blocking Core or growing memory/disk use."""

    def __init__(self, stream: BinaryIO) -> None:
        self.stream = stream
        self.data = bytearray()
        self.stop = threading.Event()
        self.thread = threading.Thread(target=self._drain, name="jacquard-host-stderr", daemon=True)
        self.thread.start()

    def _drain(self) -> None:
        try:
            with selectors.DefaultSelector() as selector:
                selector.register(self.stream, selectors.EVENT_READ)
                while True:
                    stopping = self.stop.is_set()
                    ready = selector.select(0 if stopping else 0.05)
                    if ready:
                        chunk = os.read(self.stream.fileno(), 65536)
                        if not chunk:
                            break
                        self.data.extend(chunk[:max(0, MAX_STDERR_BYTES - len(self.data))])
                    if stopping:
                        break
        finally:
            self.stream.close()

    def close(self) -> bytes:
        self.stop.set()
        self.thread.join()
        return bytes(self.data)


class WorkerProcess:
    def __init__(
        self,
        store: str | os.PathLike[str],
        *,
        command: Sequence[str] = DEFAULT_COMMAND,
        deadline_seconds: float | None = 30.0,
        env: Mapping[str, str] | None = None,
    ) -> None:
        validate_deadline_seconds(deadline_seconds)
        self.argv = [*command, "host", "worker", "--store", os.fspath(store)]
        self.deadline = None if deadline_seconds is None else time.monotonic() + deadline_seconds
        self.process = subprocess.Popen(
            self.argv,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            bufsize=0,
            env=None if env is None else dict(env),
        )
        assert self.process.stdin is not None and self.process.stdout is not None
        assert self.process.stderr is not None
        try:
            self._capture = _StderrCapture(self.process.stderr)
        except BaseException:
            self.process.kill()
            self.process.wait()
            self.process.stdin.close()
            self.process.stdout.close()
            self.process.stderr.close()
            raise
        self._reader = FrameReader(self.process.stdout.fileno(), deadline=self.deadline)
        self._input_open = True
        self.returncode: int | None = None
        self.stderr: bytes = b""

    # --- frames -----------------------------------------------------------

    def read_frame(self, limits: Mapping[str, int]) -> dict[str, Any]:
        """Read one Core frame; raises CarrierLoss, LimitExceeded, MalformedFrame, or TimeoutError."""
        payload = self._reader.read_frame(max_frame_bytes=limits["max_frame_bytes"])
        return decode_object(payload, max_depth=limits["max_json_depth"])

    def write_frame(self, frame: dict[str, Any], limits: Mapping[str, int]) -> None:
        validate_object(frame, max_depth=limits["max_json_depth"])
        data = frame_bytes(encode_object(frame), max_frame_bytes=limits["max_frame_bytes"])
        self.write_bytes(data)

    def write_bytes(self, data: bytes) -> None:
        """Write carrier bytes within the lifetime budget (also used by the kit)."""
        if not self._input_open:
            raise CarrierLoss("the worker's input is already closed")
        try:
            write_bytes(self.process.stdin.fileno(), data, deadline=self.deadline)
        except CarrierLoss:
            self.close_input()
            raise

    def close_input(self) -> None:
        if self._input_open:
            self._input_open = False
            try:
                self.process.stdin.close()
            except OSError:
                pass

    # --- lifetime ---------------------------------------------------------

    def wait(self, timeout: float | None = None) -> int:
        """Reap the worker, killing it if it outlives ``timeout`` seconds."""
        self.close_input()
        if self.returncode is None:
            if self.deadline is not None:
                remaining = max(0.0, self.deadline - time.monotonic())
                timeout = remaining if timeout is None else min(timeout, remaining)
            try:
                self.returncode = self.process.wait(timeout=timeout)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.returncode = self.process.wait()
        return self.returncode

    def kill(self) -> int:
        self.close_input()
        if self.process.poll() is None:
            self.process.kill()
        return self.wait()

    @property
    def exit_status(self) -> int | None:
        """The exit code, or None when the worker died from a signal."""
        return None if self.returncode is None or self.returncode < 0 else self.returncode

    @property
    def signal(self) -> int | None:
        return None if self.returncode is None or self.returncode >= 0 else -self.returncode

    def close(self) -> None:
        """Reap, release descriptors, and collect the bounded operator text."""
        self.close_input()
        if self.process.poll() is None and self.returncode is None:
            self.kill()
        elif self.returncode is None:
            self.returncode = self.process.returncode
        try:
            self.process.stdout.close()
        except OSError:
            pass
        self.stderr = self._capture.close()

    def __enter__(self) -> "WorkerProcess":
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()
