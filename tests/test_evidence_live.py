"""docs/EVIDENCE.md's first-party identities must match `jacquard hash` (skipped without Core)."""

from __future__ import annotations

import os
import re
import subprocess
import tempfile
import unittest
from pathlib import Path

JACQUARD_BIN = os.environ.get("JACQUARD_BIN")
JACQUARD_PRELUDE = os.environ.get("JACQUARD_PRELUDE")
REPO = Path(__file__).resolve().parents[1]
ROWS = {
    "`SaveOutcome` (type)": "save-outcome",
    "`Saved`": "saved",
    "`Duplicate`": "duplicate",
    "`Conflict`": "conflict",
    "`Study` (effect)": "study",
    "`save-response` (operation)": "save-response",
    "`record-response`": "record-response",
    "`http.parse`": "http.parse",
    "`http.route`": "http.route",
    "`http.render`": "http.render",
    "`trial.serve`": "trial.serve",
}


@unittest.skipUnless(JACQUARD_BIN and JACQUARD_PRELUDE and os.path.isdir(JACQUARD_PRELUDE), "set JACQUARD_BIN and JACQUARD_PRELUDE to check the published identities")
class EvidenceIdentitiesTest(unittest.TestCase):
    def test_published_identities_match_jacquard_hash(self) -> None:
        text = (REPO / "docs" / "EVIDENCE.md").read_text(encoding="utf-8")
        published = dict(re.findall(r"^\| (`[^|]+?`(?: \([a-z]+\))?) \| `([0-9a-f]{64})` \|$", text, re.MULTILINE))
        self.assertEqual(set(published), set(ROWS), "the identity table rows changed; update the test's row map")
        with tempfile.TemporaryDirectory(prefix="jacquard-host-evidence-") as scratch:
            combined = Path(scratch) / "all.jac"
            combined.write_text("\n".join((REPO / "src" / "jacquard_host" / "data" / "jacquard" / name).read_text(encoding="utf-8") for name in ("http.jac", "study.jac", "trial.jac")) + "\n", encoding="utf-8")
            env = dict(os.environ, JACQUARD_PRELUDE=os.path.abspath(JACQUARD_PRELUDE))
            output = subprocess.run([JACQUARD_BIN, "hash", str(combined)], capture_output=True, text=True, env=env, check=True).stdout
        first: dict[str, str] = {}
        for line in output.splitlines():
            match = re.match(r"^\d+:(\S+) ([0-9a-f]{64})$", line.strip())
            if match:
                first.setdefault(match.group(1), match.group(2))
        for label, name in ROWS.items():
            with self.subTest(label=label):
                self.assertEqual(published[label], first[name], f"{label} in docs/EVIDENCE.md is stale")


if __name__ == "__main__":
    unittest.main()
