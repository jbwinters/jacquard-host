"""``record-response`` from jacquard/study.jac through a real worker (skipped without Core)."""

from __future__ import annotations

import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

from jacquard_host.adapter import Host
from jacquard_host.identities import hash_file, parse_signature, signatures
from jacquard_host.registry import OperationRegistry
from jacquard_host.study import StudyIdentities, StudyStore, bind_study, receipt_of

JACQUARD_BIN = os.environ.get("JACQUARD_BIN")
JACQUARD_PRELUDE = os.environ.get("JACQUARD_PRELUDE")
DECLS = Path(__file__).resolve().parents[1] / "jacquard" / "study.jac"


@unittest.skipUnless(JACQUARD_BIN and JACQUARD_PRELUDE and os.path.isdir(JACQUARD_PRELUDE), "set JACQUARD_BIN and JACQUARD_PRELUDE to run the study operation against Core")
class LiveStudyTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.scratch = tempfile.mkdtemp(prefix="jacquard-host-study-live-")
        cls.store_dir = os.path.join(cls.scratch, "store")
        env = dict(os.environ, JACQUARD_PRELUDE=os.path.abspath(JACQUARD_PRELUDE))
        subprocess.run([JACQUARD_BIN, "run", str(DECLS), "--store", cls.store_dir], check=True, stdout=subprocess.DEVNULL, env=env)
        command, prelude = [JACQUARD_BIN], JACQUARD_PRELUDE
        cls.names = hash_file(DECLS, command=command, prelude=prelude) | hash_file(os.path.join(prelude, "01-prim.jqd"), command=command, prelude=prelude)
        cls.interface = parse_signature(signatures(DECLS, command=command, prelude=prelude)["record-response"], cls.names)
        cls.identities = StudyIdentities.from_names(cls.names)
        cls.host = Host(cls.store_dir, command=[JACQUARD_BIN])

    @classmethod
    def tearDownClass(cls) -> None:
        shutil.rmtree(cls.scratch, ignore_errors=True)

    def record(self, study: StudyStore, *arguments: str):
        registry = OperationRegistry()
        bind_study(registry, self.identities, study)
        return self.host.invoke(self.names["record-response"], self.interface, arguments, registry)

    def test_jacquard_sees_saved_duplicate_and_conflict(self) -> None:
        with StudyStore(os.path.join(self.scratch, "study.sqlite")) as study:
            receipt = receipt_of("trial-7", "sess-1", "B")
            first = self.record(study, "trial-7", "sess-1", "B")
            self.assertEqual((first.kind, first.value), ("ok", f"saved:{receipt}"))
            self.assertEqual([o["category"] for o in first.evidence["host_observations"]["responses"]], ["ok"])
            again = self.record(study, "trial-7", "sess-1", "B")
            self.assertEqual(again.value, f"duplicate:{receipt}")
            conflict = self.record(study, "trial-7", "sess-1", "C")
            self.assertEqual(conflict.value, "conflict")
            self.assertEqual(study.count(), 1)

    def test_invalid_input_is_refused_before_any_write(self) -> None:
        with StudyStore(os.path.join(self.scratch, "refused.sqlite")) as study:
            result = self.record(study, "bad trial", "sess-1", "B")
            self.assertEqual((result.kind, result.terminal, result.primary_code), ("error", "host_failure", "E1607"))
            self.assertEqual(study.count(), 0)


if __name__ == "__main__":
    unittest.main()
