"""The single Core pin and the files generated from it agree."""

from __future__ import annotations

import importlib.util
import json
from pathlib import Path
import shutil
import tempfile
import unittest

from jacquard_host import compat

REPO = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("core_pin", REPO / "scripts" / "core_pin.py")
core_pin = importlib.util.module_from_spec(spec)
spec.loader.exec_module(core_pin)


class CorePinTest(unittest.TestCase):
    def setUp(self) -> None:
        self.pin = core_pin.load()

    def test_repository_is_consistent_with_the_pin(self) -> None:
        self.assertEqual(core_pin.problems(self.pin), [])

    def test_python_constants_come_from_the_pin(self) -> None:
        self.assertEqual(compat.CORE_TAG, self.pin["release"]["tag"])
        self.assertEqual(compat.CORE_VERSION, self.pin["release"]["version"])
        self.assertEqual(compat.CORE_COMMIT, self.pin["release"]["commit"])
        self.assertEqual(compat.KIT_DIGESTS, self.pin["kit"]["digests"])
        self.assertEqual(list(compat.PENDING_DECISIONS), self.pin["kit"]["pending_decisions"])

    def copy(self) -> Path:
        root = Path(tempfile.mkdtemp(prefix="core-pin-", dir=REPO / ".scratch" if (REPO / ".scratch").is_dir() else None))
        self.addCleanup(shutil.rmtree, root)
        for name in [*core_pin.GENERATED, *core_pin.BLOCKS]:
            (root / name).parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(REPO / name, root / name)
        shutil.copytree(core_pin.KIT_DIR, root / core_pin.KIT_DIR.relative_to(REPO))
        return root

    def test_hand_edits_and_kit_tampering_are_reported(self) -> None:
        root = self.copy()
        compat_ts = root / "typescript/src/compat.ts"
        compat_ts.write_text(compat_ts.read_text().replace(self.pin["release"]["version"], "9.9.9"))
        kit = root / core_pin.KIT_DIR.relative_to(REPO) / "kit/fixtures.jac"
        kit.write_bytes(kit.read_bytes() + b"\n")
        found = core_pin.problems(self.pin, root)
        self.assertTrue(any("compat.ts" in item for item in found), found)
        self.assertTrue(any("fixtures.jac" in item for item in found), found)

    def test_sync_rewrites_only_generated_regions(self) -> None:
        root = self.copy()
        document = root / "docs/COMPATIBILITY.md"
        original = document.read_text()
        newer = json.loads(json.dumps(self.pin))
        newer["release"] = {"tag": "jacquard-core-9.9.9", "version": "9.9.9", "commit": "f" * 40}
        self.assertEqual(sorted(core_pin.sync(newer, root)), sorted([*core_pin.GENERATED, *core_pin.BLOCKS]))
        rewritten = document.read_text()
        self.assertIn("jacquard-core-9.9.9", rewritten)
        outside = lambda text: text[:text.index(core_pin.BEGIN)] + text[text.index(core_pin.END):]
        self.assertEqual(outside(rewritten), outside(original))
        self.assertIn('CORE_VERSION = "9.9.9"', (root / "src/jacquard_host/compat.py").read_text())
        self.assertEqual(core_pin.sync(newer, root), [])

    def test_kit_metadata_must_match_the_release(self) -> None:
        manifest = {"protocol": self.pin["protocol"], "carrier": self.pin["carrier"], "core_version": "0.0.1",
                    "pending_decisions": []}
        found = core_pin.manifest_problems(manifest, self.pin)
        self.assertTrue(any("core_version" in item for item in found), found)
        self.assertTrue(any("pending decisions" in item for item in found), found)

    def test_checksum_files_are_parsed_strictly(self) -> None:
        digest = "a" * 64
        self.assertEqual(core_pin.parse_sha256(f"{digest}  jacquard-linux-x86_64.tar.gz\n", "jacquard-linux-x86_64.tar.gz"), digest)
        self.assertEqual(core_pin.parse_sha256(f"{digest} *jacquard-linux-x86_64.tar.gz", "jacquard-linux-x86_64.tar.gz"), digest)
        for text in (f"{digest}  other.tar.gz", "XYZ  jacquard-linux-x86_64.tar.gz", ""):
            with self.assertRaises(core_pin.PinError):
                core_pin.parse_sha256(text, "jacquard-linux-x86_64.tar.gz")

    def test_pin_file_validation(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "core-pin.json"
            broken = json.loads(json.dumps(self.pin))
            broken["release"]["tag"] = "jacquard-core-0.0.0"
            path.write_text(json.dumps(broken))
            with self.assertRaisesRegex(core_pin.PinError, "does not name version"):
                core_pin.load(path)


if __name__ == "__main__":
    unittest.main()
