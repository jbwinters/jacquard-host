"""Replay the pinned kit against a real ``jacquard host worker``.

Skipped unless ``JACQUARD_BIN`` names the Core executable and
``JACQUARD_PRELUDE`` names the prelude directory of the same Core build. The
store is built with the published recipe in a temporary directory.
"""

from __future__ import annotations

import os
import shutil
import tempfile
import unittest

from jacquard_host.kit import build_store, load_kit, replay, verify_pins

JACQUARD_BIN = os.environ.get("JACQUARD_BIN")
JACQUARD_PRELUDE = os.environ.get("JACQUARD_PRELUDE")


@unittest.skipUnless(
    JACQUARD_BIN and JACQUARD_PRELUDE and os.path.isdir(JACQUARD_PRELUDE),
    "set JACQUARD_BIN and JACQUARD_PRELUDE to replay the kit against a real worker",
)
class LiveKitTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.kit = load_kit()
        verify_pins(cls.kit)
        cls.scratch = tempfile.mkdtemp(prefix="jacquard-host-kit-live-")
        cls.store = os.path.join(cls.scratch, "store")
        build_store([JACQUARD_BIN], fixtures=cls.kit.fixtures, prelude=JACQUARD_PRELUDE, store=cls.store)

    @classmethod
    def tearDownClass(cls) -> None:
        shutil.rmtree(cls.scratch, ignore_errors=True)

    def test_every_transcript_matches_its_recording(self) -> None:
        results = replay([JACQUARD_BIN], self.store, self.kit)
        self.assertEqual(len(results), 21)
        for result in results:
            with self.subTest(transcript=result.name):
                self.assertEqual(result.differences, [], result.differences)
                self.assertLessEqual(len(result.observed.stderr), 65536)

    def test_the_recorded_divergence_is_still_the_only_one(self) -> None:
        results = replay([JACQUARD_BIN], self.store, self.kit, names=["noncanonical-target-hash", "pure-success"])
        by_name = {result.name: result for result in results}
        self.assertEqual(by_name["noncanonical-target-hash"].status, "diverges")
        self.assertEqual(by_name["noncanonical-target-hash"].summary["primary_code"], "E1601")
        self.assertEqual(by_name["pure-success"].status, "conforms")


if __name__ == "__main__":
    unittest.main()
