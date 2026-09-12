"""The H2.2 measurement script stays runnable (skipped without Core)."""

from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

JACQUARD_BIN = os.environ.get("JACQUARD_BIN")
JACQUARD_PRELUDE = os.environ.get("JACQUARD_PRELUDE")
REPO = Path(__file__).resolve().parents[1]


@unittest.skipUnless(JACQUARD_BIN and JACQUARD_PRELUDE and os.path.isdir(JACQUARD_PRELUDE), "set JACQUARD_BIN and JACQUARD_PRELUDE to run the measurement")
class MeasurementTest(unittest.TestCase):
    def test_measurement_script_produces_a_report(self) -> None:
        with tempfile.TemporaryDirectory(prefix="jacquard-host-measure-") as scratch:
            report = os.path.join(scratch, "report.json")
            env = dict(os.environ, PYTHONPATH=str(REPO / "src"))
            completed = subprocess.run(
                [sys.executable, str(REPO / "scripts" / "measure_serial.py"), "--workdir", os.path.join(scratch, "work"), "--requests", "3", "--report", report],
                capture_output=True, text=True, env=env, check=False,
            )
            self.assertEqual(completed.returncode, 0, completed.stderr)
            data = json.loads(Path(report).read_text(encoding="utf-8"))
        self.assertEqual(data["schema"], "jacquard-host-serial-measurement-v0")
        self.assertEqual([s["scenario"] for s in data["scenarios"]][:2], ["serial GET, one client", "serial POST, one client"])
        for scenario in data["scenarios"]:
            self.assertEqual(scenario["served_outcomes"], ["served"], scenario)
            self.assertIn(scenario["statuses"], (["HTTP/1.1 200 OK"], ["HTTP/1.1 201 Created"]))
            self.assertIn("connect_wait", scenario)
            self.assertIn("accept_backlog", scenario)
        with_backlog = [s for s in data["scenarios"] if s["accept_backlog"] > 1]
        self.assertTrue(with_backlog, "a matching-backlog control scenario must exist")
        for scenario in with_backlog:
            self.assertLess(scenario["connect_wait"]["max"], 0.5, "with a matching backlog no client waits on the kernel")
        self.assertLess(data["worker_spawn_negotiate_shutdown"]["median"], 5.0)

    def test_recorded_measurement_matches_the_document(self) -> None:
        data = json.loads((REPO / "docs" / "measurements-serial-2026-09-12.json").read_text(encoding="utf-8"))
        self.assertEqual(data["schema"], "jacquard-host-serial-measurement-v0")
        text = (REPO / "docs" / "C4-REQUIREMENTS.md").read_text(encoding="utf-8")
        self.assertIn("measurements-serial-2026-09-12.json", text)
        self.assertEqual(len(data["scenarios"]), 11)
        names = [s["scenario"] for s in data["scenarios"]]
        self.assertIn("burst GET x40, backlog 40", names)
        self.assertIn("burst GET x40, backlog 1, clients give up after 10 s", names)
        starved = next(s for s in data["scenarios"] if s["scenario"].startswith("burst GET x40, backlog 1"))
        self.assertGreater(starved["clients_gave_up"], 0, "the recorded run shows starvation at backlog 1")


if __name__ == "__main__":
    unittest.main()
