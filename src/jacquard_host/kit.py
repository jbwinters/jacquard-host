"""Replay the pinned ``jacquard-host-v0`` conformance kit against a worker.

This is a Python port of the deterministic fake host that Core uses to
produce and check the kit (``test/host_kit.ml`` in jacquard-lang). It plays
each recorded transcript in lockstep against one freshly spawned worker,
writes host frames only when the transcript says it is the host's turn, never
writes after it has observed a terminal frame (recording the local E1608 the
protocol requires instead), and compares the Core frames, exit status, and
observed summary with the recording. Diagnostic prose is compared separately
and reported as drift, never as a failure, because only the schema, domain,
code, severity, and span of a diagnostic are normative.

It is evidence tooling. The embedding adapter is a separate module; nothing
here normalises a frame to make a transcript pass.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Sequence

from jacquard_host import compat
from jacquard_host.protocol.errors import ProtocolError
from jacquard_host.protocol.framing import frame_bytes
from jacquard_host.protocol.json_codec import canonical_text, encode_object
from jacquard_host.protocol.limits import HARD_LIMITS
from jacquard_host.worker import WorkerProcess

DEFAULT_KIT_DIR = Path(__file__).resolve().parent / "data" / "conformance" / "host-protocol-v0"
KIT_SCHEMA = "jacquard-host-kit-v0"
PROSE_FIELDS = ("summary", "cause", "next_step", "contrast")
TERMINAL_KINDS = frozenset({"outcome", "fatal", "shutdown_ack"})
FAKE_HOST_TIMEOUT = {"kind": "fake-host-timeout"}


# --- semantic comparison -------------------------------------------------


def is_diagnostic(value: Any) -> bool:
    return isinstance(value, dict) and value.get("schema") == "jacquard-diagnostic-v1"


def strip_prose(value: Any) -> Any:
    """Drop the non-normative prose fields of every diagnostic-v1 object."""
    if isinstance(value, dict):
        if is_diagnostic(value):
            return {key: strip_prose(item) for key, item in value.items() if key not in PROSE_FIELDS}
        return {key: strip_prose(item) for key, item in value.items()}
    if isinstance(value, list):
        return [strip_prose(item) for item in value]
    return value


def equal_semantic(expected: Any, observed: Any) -> bool:
    return canonical_text(strip_prose(expected)) == canonical_text(strip_prose(observed))


def prose_drift(expected: Any, observed: Any, path: str = "") -> list[str]:
    """Paths of diagnostic prose fields whose text differs between two frames."""
    drift: list[str] = []
    if isinstance(expected, dict) and isinstance(observed, dict):
        if is_diagnostic(expected):
            for key in PROSE_FIELDS:
                if key in expected and key in observed and canonical_text(expected[key]) != canonical_text(observed[key]):
                    drift.append(f"{path}/{key}")
            return drift
        for key, item in expected.items():
            if key in observed:
                drift.extend(prose_drift(item, observed[key], f"{path}/{key}"))
    elif isinstance(expected, list) and isinstance(observed, list) and len(expected) == len(observed):
        for index, (a, b) in enumerate(zip(expected, observed)):
            drift.extend(prose_drift(a, b, f"{path}/{index}"))
    return drift


def is_terminal(frame: Any) -> bool:
    return isinstance(frame, dict) and frame.get("kind") in TERMINAL_KINDS


# --- the kit on disk -----------------------------------------------------


class PinMismatch(ValueError):
    """The kit on disk is not the kit this adapter is pinned to."""


@dataclass(frozen=True)
class Kit:
    directory: Path
    manifest: dict[str, Any]
    transcripts: list[dict[str, Any]]
    digests: dict[str, str]

    def transcript(self, name: str) -> dict[str, Any]:
        for entry in self.transcripts:
            if entry["name"] == name:
                return entry
        raise KeyError(name)

    @property
    def fixtures(self) -> Path:
        return self.directory / "kit" / self.manifest["recipe"]["fixtures"]


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_kit(directory: Path | str = DEFAULT_KIT_DIR) -> Kit:
    """Load ``kit.json`` and its transcripts, computing the file digests."""
    directory = Path(directory)
    manifest = json.loads((directory / "kit" / "kit.json").read_text(encoding="utf-8"))
    if manifest.get("schema") != KIT_SCHEMA:
        raise PinMismatch(f"kit schema is {manifest.get('schema')!r}, expected {KIT_SCHEMA!r}")
    transcripts = json.loads((directory / "kit" / manifest["transcripts"]).read_text(encoding="utf-8"))
    if not isinstance(transcripts, list) or len(transcripts) != manifest["transcript_count"]:
        raise PinMismatch("transcript count disagrees with the manifest")
    digests = {
        relative: sha256_file(directory / relative)
        for relative in ("kit/kit.json", "kit/transcripts.json", "kit/fixtures.jac", "vectors.json")
    }
    return Kit(directory=directory, manifest=manifest, transcripts=transcripts, digests=digests)


def verify_pins(kit: Kit) -> None:
    """Fail closed unless the kit matches :mod:`jacquard_host.compat` exactly."""
    problems: list[str] = []
    for relative, expected in compat.KIT_DIGESTS.items():
        observed = kit.digests.get(relative)
        if observed != expected:
            problems.append(f"{relative}: sha256 {observed} is not the pinned {expected}")
    for field, expected in (
        ("protocol", compat.PROTOCOL),
        ("carrier", compat.CARRIER),
        ("core_version", compat.CORE_VERSION),
    ):
        if kit.manifest.get(field) != expected:
            problems.append(f"{field}: {kit.manifest.get(field)!r} is not the pinned {expected!r}")
    if kit.manifest.get("hard_limits") != HARD_LIMITS:
        problems.append("hard_limits disagree with the frozen section 6 table")
    pending = tuple(entry["name"] for entry in kit.manifest.get("pending_decisions", []))
    if pending != compat.PENDING_DECISIONS:
        problems.append(f"pending decisions {pending!r} are not the pinned {compat.PENDING_DECISIONS!r}")
    if problems:
        raise PinMismatch("; ".join(problems))


# --- observed replay -----------------------------------------------------


@dataclass
class Observed:
    core_frames: list[dict[str, Any]]
    exit_code: int | None
    signal: int | None
    stderr: bytes
    host_refusal: str | None
    skipped_after_terminal: int
    write_failure: bool


def first_code(diagnostics: Any) -> str | None:
    if isinstance(diagnostics, list) and diagnostics and isinstance(diagnostics[0], dict):
        code = diagnostics[0].get("code")
        return code if isinstance(code, str) else None
    return None


def summary(observed: Observed) -> dict[str, Any]:
    """The ``observed`` object the kit records for one replay."""
    terminal = next((frame for frame in observed.core_frames if is_terminal(frame)), None)
    kind = terminal.get("kind") if terminal else None
    primary_code = None
    terminal_class = None
    if kind == "fatal":
        primary_code = first_code(terminal.get("diagnostics"))
        terminal_class = "fatal"
    elif kind == "outcome":
        result = terminal.get("result", {})
        if isinstance(result, dict) and result.get("kind") == "error":
            primary_code = first_code(result.get("diagnostics"))
        try:
            terminal_class = terminal["evidence"]["core"]["terminal"]
        except (KeyError, TypeError):
            terminal_class = None
    elif kind == "shutdown_ack":
        terminal_class = "shutdown"
    return {
        "terminal_kind": kind,
        "primary_code": primary_code,
        "terminal": terminal_class,
        "effect_requests": sum(1 for frame in observed.core_frames if frame.get("kind") == "effect_request"),
        "terminal_frames": sum(1 for frame in observed.core_frames if is_terminal(frame)),
        "host_local_refusal": observed.host_refusal,
        "host_inputs_skipped_after_terminal": observed.skipped_after_terminal,
    }


def host_message_suffix_honoured(transcript: dict[str, Any], observed: Observed) -> bool:
    """Spec section 8: the exact decoded host message must end the diagnostic cause."""
    messages = [
        step["frame"]["message"]
        for step in transcript["host_input"]
        if step["direction"] == "host_to_core"
        and not step.get("after_terminal")
        and step.get("frame", {}).get("kind") == "effect_failure"
    ]
    terminal = next((frame for frame in observed.core_frames if is_terminal(frame)), None)
    if not messages or terminal is None:
        return True
    if terminal.get("kind") != "outcome":
        return False
    diagnostics = terminal.get("result", {}).get("diagnostics")
    if not isinstance(diagnostics, list) or not diagnostics:
        return False
    cause = diagnostics[0].get("cause")
    return isinstance(cause, str) and cause.endswith(messages[0])


def conforms(transcript: dict[str, Any], observed: Observed) -> bool:
    """The kit's conformance rule for one transcript (positive or hostile)."""
    seen = summary(observed)
    expect = transcript["expect"]
    if transcript["kind"] == "positive":
        recorded = [frame for frame in transcript["core_output"]]
        actual = [frame for frame in observed.core_frames if frame != FAKE_HOST_TIMEOUT]
        return (
            seen["terminal"] == expect["terminal"]
            and observed.exit_code == 0
            and len(actual) == len(recorded)
            and all(equal_semantic(a, b) for a, b in zip(recorded, actual))
            and host_message_suffix_honoured(transcript, observed)
        )
    code = expect["code"]
    expected_exit = 74 if code == "E1611" else 0
    basic = (
        seen["effect_requests"] == expect["effect_requests_before_failure"]
        and seen["terminal_frames"] <= expect["terminal_frames_max"]
        and (not expect["forbid_outside_action"] or seen["effect_requests"] == 0)
        and observed.exit_code == expected_exit
    )
    if transcript.get("phase") == "terminal":
        return basic and observed.host_refusal == code and seen["terminal"] == "ok"
    return basic and seen["primary_code"] == code


def status(transcript: dict[str, Any], observed: Observed) -> str:
    return "conforms" if conforms(transcript, observed) else "diverges"


def play(
    command: Sequence[str],
    store: str | os.PathLike[str],
    transcript: dict[str, Any],
    *,
    deadline_seconds: float = 30.0,
    env: dict[str, str] | None = None,
) -> Observed:
    """Play one transcript against a freshly spawned worker.

    ``command`` is the argv prefix of the worker executable, normally
    ``["jacquard"]``; the ``host worker --store STORE`` arguments are appended.
    """
    frames: list[dict[str, Any]] = []
    state = {"terminal": False, "refusal": None, "skipped": 0, "write_failure": False}
    with WorkerProcess(store, command=command, deadline_seconds=deadline_seconds, env=env) as worker:
        def read_frame() -> bool:
            try:
                frame = worker.read_frame(HARD_LIMITS)
            except ProtocolError:
                return False
            frames.append(frame)
            if is_terminal(frame):
                state["terminal"] = True
            return True

        def write(data: bytes) -> None:
            try:
                worker.write_bytes(data)
            except ProtocolError:
                state["write_failure"] = True

        def encode(frame: dict[str, Any]) -> bytes:
            return frame_bytes(encode_object(frame), max_frame_bytes=HARD_LIMITS["max_frame_bytes"])

        try:
            for step in transcript["host_input"]:
                if step["direction"] == "core_to_host":
                    read_frame()
                elif "raw_hex" in step:
                    write(bytes.fromhex(step["raw_hex"]))
                    worker.close_input()
                elif step.get("after_terminal"):
                    if state["terminal"]:
                        state["refusal"] = "E1608"
                    else:
                        write(encode(step["frame"]))
                elif state["terminal"]:
                    state["skipped"] += 1
                else:
                    write(encode(step["frame"]))
            worker.close_input()
            while read_frame():
                pass
        except TimeoutError:
            worker.kill()
            frames.append(dict(FAKE_HOST_TIMEOUT))
        returncode = worker.wait(timeout=1.0)
    return Observed(
        core_frames=frames,
        exit_code=returncode if returncode >= 0 else None,
        signal=-returncode if returncode < 0 else None,
        stderr=worker.stderr,
        host_refusal=state["refusal"],
        skipped_after_terminal=state["skipped"],
        write_failure=state["write_failure"],
    )


# --- comparison with the recording ---------------------------------------


@dataclass
class Replay:
    name: str
    observed: Observed
    summary: dict[str, Any]
    status: str
    differences: list[str]
    prose_drift: list[str]

    @property
    def matches_recording(self) -> bool:
        return not self.differences


def compare(transcript: dict[str, Any], observed: Observed) -> Replay:
    """Compare one replay with its recording; prose drift is reported, not failed."""
    seen = summary(observed)
    differences: list[str] = []
    verdict = status(transcript, observed)
    if verdict != transcript["status"]:
        differences.append(f"status {verdict} is not the recorded {transcript['status']}")
    if observed.exit_code != transcript["exit_status"]:
        differences.append(f"exit status {observed.exit_code} is not the recorded {transcript['exit_status']}")
    for field, recorded in transcript["observed"].items():
        if seen.get(field) != recorded:
            differences.append(f"observed.{field} {seen.get(field)!r} is not the recorded {recorded!r}")
    recorded_frames = transcript["core_output"]
    actual = [frame for frame in observed.core_frames if frame != FAKE_HOST_TIMEOUT]
    drift: list[str] = []
    if len(actual) != len(recorded_frames):
        differences.append(f"{len(actual)} Core frames were observed, the recording has {len(recorded_frames)}")
    else:
        for index, (expected, frame) in enumerate(zip(recorded_frames, actual)):
            if not equal_semantic(expected, frame):
                differences.append(f"Core frame {index} differs semantically from the recording")
            drift.extend(f"{index}{path}" for path in prose_drift(expected, frame))
    return Replay(
        name=transcript["name"],
        observed=observed,
        summary=seen,
        status=verdict,
        differences=differences,
        prose_drift=drift,
    )


def replay(
    command: Sequence[str],
    store: str | os.PathLike[str],
    kit: Kit,
    *,
    names: Sequence[str] | None = None,
    deadline_seconds: float = 30.0,
    env: dict[str, str] | None = None,
) -> list[Replay]:
    """Play every transcript (or the named ones) and compare each with its recording."""
    if names is not None:
        unknown = sorted(set(names) - {transcript["name"] for transcript in kit.transcripts})
        if unknown:
            raise KeyError(f"the kit has no transcript named {', '.join(unknown)}")
    results = []
    for transcript in kit.transcripts:
        if names is not None and transcript["name"] not in names:
            continue
        observed = play(command, store, transcript, deadline_seconds=deadline_seconds, env=env)
        results.append(compare(transcript, observed))
    return results


# --- the kit store -------------------------------------------------------


def build_store(
    command: Sequence[str],
    *,
    fixtures: str | os.PathLike[str],
    prelude: str | os.PathLike[str],
    store: str | os.PathLike[str],
) -> None:
    """Follow the published recipe exactly: ``jacquard run fixtures.jac --store DIR``."""
    env = dict(os.environ)
    env["JACQUARD_PRELUDE"] = os.path.abspath(os.fspath(prelude))
    env["TMPDIR"] = tempfile.gettempdir()
    completed = subprocess.run(
        [*command, "run", os.fspath(fixtures), "--store", os.fspath(store)],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        env=env,
        check=False,
    )
    if completed.returncode != 0:
        raise RuntimeError(f"the kit recipe exited {completed.returncode}")


def report_json(results: Sequence[Replay]) -> dict[str, Any]:
    return {
        "schema": "jacquard-host-kit-replay-v0",
        "pin": {
            "protocol": compat.PROTOCOL,
            "carrier": compat.CARRIER,
            "core_version": compat.CORE_VERSION,
            "core_commit": compat.CORE_COMMIT,
            "digests": dict(compat.KIT_DIGESTS),
        },
        "replays": [
            {
                "name": result.name,
                "status": result.status,
                "matches_recording": result.matches_recording,
                "differences": result.differences,
                "prose_drift": result.prose_drift,
                "observed": result.summary,
                "exit_status": result.observed.exit_code,
                "signal": result.observed.signal,
                "stderr_bytes": len(result.observed.stderr),
            }
            for result in results
        ],
    }


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Replay the pinned jacquard-host-v0 conformance kit against a worker.")
    parser.add_argument("--jacquard", default=os.environ.get("JACQUARD_BIN", "jacquard"), help="worker executable")
    parser.add_argument("--prelude", default=os.environ.get("JACQUARD_PRELUDE"), help="Core prelude directory")
    parser.add_argument("--kit", default=str(DEFAULT_KIT_DIR), help="kit directory (contains kit/ and vectors.json)")
    parser.add_argument("--store", default=None, help="reuse an already built kit store instead of a temporary one")
    parser.add_argument("--only", action="append", default=None, help="transcript name (repeatable)")
    parser.add_argument("--deadline", type=float, default=30.0, help="seconds allowed per transcript")
    parser.add_argument("--report", default=None, help="write a JSON report to this path")
    args = parser.parse_args(argv)

    command = [args.jacquard]
    env = dict(os.environ)
    if args.prelude:
        env["JACQUARD_PRELUDE"] = os.path.abspath(args.prelude)
    try:
        kit = load_kit(args.kit)
        verify_pins(kit)
        with tempfile.TemporaryDirectory(prefix="jacquard-host-kit-store-") as scratch:
            store = args.store
            if store is None:
                if not args.prelude:
                    parser.error("--prelude (or JACQUARD_PRELUDE) is required to build the kit store")
                store = os.path.join(scratch, "store")
                build_store(command, fixtures=kit.fixtures, prelude=args.prelude, store=store)
            results = replay(command, store, kit, names=args.only, deadline_seconds=args.deadline, env=env)
    except (OSError, RuntimeError, KeyError, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    if not results:
        print("error: no transcript was replayed", file=sys.stderr)
        return 2
    failures = 0
    for result in results:
        mark = "ok" if result.matches_recording else "MISMATCH"
        print(f"{mark:8} {result.name:34} {result.status:9} exit={result.observed.exit_code}")
        for line in result.differences:
            print(f"         - {line}")
        if result.prose_drift:
            print(f"         prose drift: {', '.join(result.prose_drift)}")
        failures += 0 if result.matches_recording else 1
    if args.report:
        Path(args.report).write_text(json.dumps(report_json(results), indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(f"{len(results) - failures}/{len(results)} transcripts match the recording")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
