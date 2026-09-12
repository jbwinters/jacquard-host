"""Offline checks of the conformance-kit replayer against the recorded kit.

These tests never run Core. They prove that the vendored kit is the pinned
kit, that the Python port of the kit's summary and conformance rules
reproduces every recorded ``observed`` object and status (including the one
recorded divergence), and that the process-level replayer handles pipes,
terminal detection, local refusal, skipped input, exit statuses, and stderr
using a stand-in worker that replays recorded frames.
"""

from __future__ import annotations

import os
import sys
import unittest
from pathlib import Path

from jacquard_host import compat, kit as kit_module
from jacquard_host.kit import (
    Observed,
    PinMismatch,
    compare,
    conforms,
    equal_semantic,
    load_kit,
    play,
    prose_drift,
    strip_prose,
    summary,
    verify_pins,
)

REPO = Path(__file__).resolve().parents[1]
REPLAY_WORKER = Path(__file__).resolve().parent / "replay_worker.py"


def recorded_observed(transcript: dict) -> Observed:
    return Observed(
        core_frames=list(transcript["core_output"]),
        exit_code=transcript["exit_status"],
        signal=None,
        stderr=b"",
        host_refusal=transcript["observed"]["host_local_refusal"],
        skipped_after_terminal=transcript["observed"]["host_inputs_skipped_after_terminal"],
        write_failure=False,
    )


class PinTest(unittest.TestCase):
    def test_vendored_kit_is_the_pinned_kit(self) -> None:
        kit = load_kit()
        verify_pins(kit)
        self.assertEqual(kit.manifest["transcript_count"], 21)
        self.assertEqual(len(kit.transcripts), 21)

    def test_compatibility_document_records_the_same_pin(self) -> None:
        text = (REPO / "docs" / "COMPATIBILITY.md").read_text(encoding="utf-8")
        for value in (compat.PROTOCOL, compat.CARRIER, compat.CORE_VERSION, compat.CORE_COMMIT, *compat.KIT_DIGESTS.values()):
            self.assertIn(value, text, f"COMPATIBILITY.md does not record {value}")
        for name in compat.PENDING_DECISIONS:
            self.assertIn(name, text)

    def test_a_modified_kit_fails_the_pin(self) -> None:
        kit = load_kit()
        tampered = kit_module.Kit(
            directory=kit.directory,
            manifest=dict(kit.manifest, core_version="0.3.0"),
            transcripts=kit.transcripts,
            digests=dict(kit.digests, **{"kit/kit.json": "0" * 64}),
        )
        with self.assertRaises(PinMismatch) as caught:
            verify_pins(tampered)
        self.assertIn("kit/kit.json", str(caught.exception))
        self.assertIn("core_version", str(caught.exception))


class SemanticComparisonTest(unittest.TestCase):
    def test_prose_is_stripped_only_inside_diagnostics(self) -> None:
        frame = {
            "kind": "fatal",
            "diagnostics": [{"schema": "jacquard-diagnostic-v1", "code": "E1600", "summary": "a", "cause": "b", "span": None}],
            "summary": "kept",
        }
        self.assertEqual(
            strip_prose(frame),
            {"kind": "fatal", "diagnostics": [{"schema": "jacquard-diagnostic-v1", "code": "E1600", "span": None}], "summary": "kept"},
        )

    def test_equal_semantic_ignores_prose_and_key_order_but_not_codes(self) -> None:
        a = {"kind": "fatal", "diagnostics": [{"schema": "jacquard-diagnostic-v1", "code": "E1600", "cause": "x"}]}
        b = {"diagnostics": [{"cause": "y", "code": "E1600", "schema": "jacquard-diagnostic-v1"}], "kind": "fatal"}
        c = {"kind": "fatal", "diagnostics": [{"schema": "jacquard-diagnostic-v1", "code": "E1601", "cause": "x"}]}
        self.assertTrue(equal_semantic(a, b))
        self.assertFalse(equal_semantic(a, c))
        self.assertEqual(prose_drift(a, b), ["/diagnostics/0/cause"])
        self.assertEqual(prose_drift(a, a), [])


class RecordedSummaryTest(unittest.TestCase):
    def test_every_recorded_observed_object_is_reproduced(self) -> None:
        kit = load_kit()
        for transcript in kit.transcripts:
            with self.subTest(transcript=transcript["name"]):
                self.assertEqual(summary(recorded_observed(transcript)), transcript["observed"])

    def test_every_recorded_status_is_reproduced_including_the_divergence(self) -> None:
        kit = load_kit()
        statuses = {}
        for transcript in kit.transcripts:
            with self.subTest(transcript=transcript["name"]):
                observed = recorded_observed(transcript)
                self.assertEqual(conforms(transcript, observed), transcript["status"] == "conforms")
                statuses[transcript["name"]] = transcript["status"]
                result = compare(transcript, observed)
                self.assertEqual(result.differences, [])
                self.assertEqual(result.prose_drift, [])
        self.assertEqual(statuses["noncanonical-target-hash"], "diverges")
        self.assertEqual(sum(1 for value in statuses.values() if value == "diverges"), 1)

    def test_conformance_rule_rejects_the_wrong_evidence(self) -> None:
        kit = load_kit()
        positive = kit.transcript("one-once-effect-success")
        observed = recorded_observed(positive)
        observed.exit_code = 74
        self.assertFalse(conforms(positive, observed))
        hostile = kit.transcript("unknown-version")
        observed = recorded_observed(hostile)
        observed.core_frames = [dict(observed.core_frames[0]), {"kind": "effect_request"}, observed.core_frames[1]]
        self.assertFalse(conforms(hostile, observed), "an effect request before a preflight failure must not conform")
        terminal = kit.transcript("message-after-outcome")
        observed = recorded_observed(terminal)
        observed.host_refusal = None
        self.assertFalse(conforms(terminal, observed), "the terminal-phase case needs the local E1608 refusal")
        refused = kit.transcript("refused-authority")
        observed = recorded_observed(refused)
        observed.core_frames[-1]["result"]["diagnostics"][0]["cause"] = "detail dropped"
        self.assertFalse(conforms(refused, observed), "the host message must end the diagnostic cause")


class ReplayWorkerTest(unittest.TestCase):
    """Drive the replayer's process handling with the stand-in worker."""

    def replay_env(self, name: str) -> dict[str, str]:
        env = dict(os.environ)
        env["REPLAY_TRANSCRIPTS"] = str(load_kit().directory / "kit" / "transcripts.json")
        env["REPLAY_CASE"] = name
        return env

    def test_every_transcript_replays_to_its_recording(self) -> None:
        kit = load_kit()
        for transcript in kit.transcripts:
            with self.subTest(transcript=transcript["name"]):
                observed = play([sys.executable, str(REPLAY_WORKER)], "unused-store", transcript, deadline_seconds=20, env=self.replay_env(transcript["name"]))
                result = compare(transcript, observed)
                self.assertEqual(result.differences, [], result.differences)
                self.assertEqual(result.status, transcript["status"])
                self.assertEqual(observed.signal, None)
                self.assertIn(transcript["name"].encode(), observed.stderr)

    def test_terminal_phase_case_refuses_locally_and_writes_nothing_after_the_outcome(self) -> None:
        kit = load_kit()
        transcript = kit.transcript("message-after-outcome")
        observed = play([sys.executable, str(REPLAY_WORKER)], "unused-store", transcript, deadline_seconds=20, env=self.replay_env(transcript["name"]))
        self.assertEqual(observed.host_refusal, "E1608")
        self.assertEqual(observed.skipped_after_terminal, 0)
        self.assertEqual([frame["kind"] for frame in observed.core_frames], ["core_hello", "effect_request", "outcome"])

    def test_a_silent_worker_is_killed_at_the_deadline(self) -> None:
        kit = load_kit()
        transcript = kit.transcript("pure-success")
        observed = play([sys.executable, "-c", "import time; time.sleep(30)"], "unused-store", transcript, deadline_seconds=0.5)
        self.assertEqual(observed.core_frames, [{"kind": "fake-host-timeout"}])
        self.assertIsNone(observed.exit_code)
        self.assertIsNotNone(observed.signal)
        self.assertFalse(conforms(transcript, observed))


if __name__ == "__main__":
    unittest.main()
