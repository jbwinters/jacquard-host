"""One ``jacquard host worker`` process behind pipes.

The wrapper owns the process, its three descriptors, a bounded stderr
capture, and a host-owned deadline. It reads and writes frames but decides
nothing about their meaning; that is the session's job. Deadline expiry and
carrier loss are host-owned evidence: the wrapper records them and kills the
worker, and never fabricates a Core frame.
"""

from __future__ import annotations

import os
import subprocess
import tempfile
import time
from pathlib import Path
from typing import Any, Mapping, Sequence

from jacquard_host.protocol.errors import CarrierLoss
from jacquard_host.protocol.framing import FrameReader, frame_bytes
from jacquard_host.protocol.json_codec import decode_object, encode_object

DEFAULT_COMMAND: tuple[str, ...] = ("jacquard",)


class WorkerProcess:
    def __init__(
        self,
        store: str | os.PathLike[str],
        *,
        command: Sequence[str] = DEFAULT_COMMAND,
        deadline_seconds: float | None = 30.0,
        env: Mapping[str, str] | None = None,
    ) -> None:
        self.argv = [*command, "host", "worker", "--store", os.fspath(store)]
        self.deadline = None if deadline_seconds is None else time.monotonic() + deadline_seconds
        stderr_fd, stderr_name = tempfile.mkstemp(prefix="jacquard-host-worker-stderr-")
        self._stderr_path = Path(stderr_name)
        try:
            self.process = subprocess.Popen(
                self.argv,
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=stderr_fd,
                env=None if env is None else dict(env),
            )
        except OSError:
            os.close(stderr_fd)
            self._stderr_path.unlink(missing_ok=True)
            raise
        # the child holds its own copy of the descriptor; release the parent's
        os.close(stderr_fd)
        assert self.process.stdin is not None and self.process.stdout is not None
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
        if not self._input_open:
            raise CarrierLoss("the worker's input is already closed")
        data = frame_bytes(encode_object(frame), max_frame_bytes=limits["max_frame_bytes"])
        try:
            self.process.stdin.write(data)
            self.process.stdin.flush()
        except (BrokenPipeError, OSError):
            self.close_input()
            raise CarrierLoss("the worker's input pipe was lost during a write") from None

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
        if self._stderr_path.exists():
            self.stderr = self._stderr_path.read_bytes()
            try:
                self._stderr_path.unlink()
            except OSError:
                pass

    def __enter__(self) -> "WorkerProcess":
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()
