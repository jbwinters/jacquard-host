"""A stand-in worker that replays one recorded transcript's Core frames.

Used only to test the replayer's process handling offline. It is not Core:
it validates nothing and emits the recorded frames in the recorded
interleaving, then exits with the recorded status.

Invocation mirrors the real worker: ``replay_worker.py host worker --store DIR``.
The transcript is selected by the ``REPLAY_TRANSCRIPTS`` (path) and
``REPLAY_CASE`` (name) environment variables.
"""

from __future__ import annotations

import json
import os
import sys


def read_host_frame(stream) -> bool:
    prefix = stream.read(4)
    if len(prefix) < 4:
        return False
    length = int.from_bytes(prefix, "big")
    if length < 1 or length > 1_048_576:
        return False
    payload = stream.read(length)
    return len(payload) == length


def main() -> int:
    if sys.argv[1:3] != ["host", "worker"] or sys.argv[3] != "--store":
        sys.stderr.write("replay worker: unexpected arguments\n")
        return 64
    with open(os.environ["REPLAY_TRANSCRIPTS"], encoding="utf-8") as stream:
        transcripts = json.load(stream)
    case = next(entry for entry in transcripts if entry["name"] == os.environ["REPLAY_CASE"])
    frames = case["core_output"]
    steps = case["host_input"]
    # host frames Core consumed before each Core frame after the first
    consumed: list[int] = []
    count = 0
    seen_core = 0
    for step in steps:
        if step["direction"] == "core_to_host":
            if seen_core > 0:
                consumed.append(count)
            count = 0
            seen_core += 1
        elif not step.get("after_terminal"):
            count += 1
    stdin = sys.stdin.buffer
    stdout = sys.stdout.buffer
    for index, frame in enumerate(frames):
        if index > 0:
            wanted = consumed[index - 1] if index - 1 < len(consumed) else 0
            for _ in range(wanted):
                if not read_host_frame(stdin):
                    break
        payload = json.dumps(frame, separators=(",", ":"), sort_keys=True).encode("utf-8")
        stdout.write(len(payload).to_bytes(4, "big") + payload)
        stdout.flush()
    sys.stderr.write(f"replay worker: {case['name']}\n")
    return int(case["exit_status"])


if __name__ == "__main__":
    sys.exit(main())
