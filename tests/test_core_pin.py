"""The single Core pin and the files generated from it agree."""

from __future__ import annotations

import importlib.util
import io
import json
from pathlib import Path
import shlex
import shutil
import subprocess
import tarfile
import tempfile
import unittest
from unittest.mock import patch

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
        for directory in core_pin.KIT_DIRS:
            shutil.copytree(directory, root / directory.relative_to(REPO))
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
        go_readme = root / core_pin.KIT_DIRS[1].relative_to(REPO) / "kit/README.md"
        go_readme.write_text("drifted")
        self.assertTrue(any("go/kit/data" in item and "README.md" in item for item in core_pin.problems(self.pin, root)))

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


def archive(members: list[tuple[str, bytes | None, str | None]]) -> bytes:
    """Build a gzip tar of (name, file bytes or None for a directory, symlink target)."""
    buffer = io.BytesIO()
    with tarfile.open(fileobj=buffer, mode="w:gz") as tar:
        for name, data, link in members:
            info = tarfile.TarInfo(name)
            if link is not None:
                info.type, info.linkname = tarfile.SYMTYPE, link
                tar.addfile(info)
            elif data is None:
                info.type, info.mode = tarfile.DIRTYPE, 0o755
                tar.addfile(info)
            else:
                info.size, info.mode = len(data), 0o755
                tar.addfile(info, io.BytesIO(data))
    return buffer.getvalue()


def release(version: str, extra: list | None = None) -> bytes:
    script = f"#!/bin/sh\necho {version}\n".encode()
    return archive([("jacquard-x/", None, None), ("jacquard-x/libexec/", None, None),
                    ("jacquard-x/libexec/jacquard/", None, None),
                    ("jacquard-x/libexec/jacquard/jacquard", script, None),
                    ("jacquard-x/share/jacquard/prelude/01-prim.jqd", b"prelude", None), *(extra or [])])


class InstallTest(unittest.TestCase):
    def setUp(self) -> None:
        self.dest = Path(tempfile.mkdtemp(prefix="core install "))
        self.addCleanup(shutil.rmtree, self.dest)

    def pin(self, data: bytes) -> dict:
        pin = json.loads(json.dumps(core_pin.load()))
        pin["assets"] = {"test-x86_64": {"name": "jacquard-test-x86_64.tar.gz", "sha256": core_pin.sha256(data)}}
        return pin

    def install(self, data: bytes, pin: dict | None = None) -> dict:
        with patch.object(core_pin, "fetch", return_value=data):
            return core_pin.install(pin or self.pin(data), self.dest, "test-x86_64")

    def test_verified_release_installs_and_exports_quoted_paths(self) -> None:
        data = release(core_pin.load()["release"]["version"])
        env = self.install(data)
        self.assertTrue(env["JACQUARD_BIN"].endswith("libexec/jacquard/jacquard"))
        self.assertTrue(Path(env["JACQUARD_PRELUDE"], "01-prim.jqd").is_file())
        script = core_pin.exports(env) + 'printf %s "$JACQUARD_BIN"'
        self.assertEqual(subprocess.run(["sh", "-c", script], capture_output=True, text=True).stdout, env["JACQUARD_BIN"])
        with patch.object(core_pin, "fetch", side_effect=AssertionError("reuse must not download")):
            self.assertEqual(core_pin.install(self.pin(data), self.dest, "test-x86_64"), env)
        self.assertEqual(shlex.split(core_pin.exports({"A": "x y"}))[1], "A=x y")

    def test_checksum_and_version_failures_leave_nothing_installed(self) -> None:
        data = release("0.0.0")
        tampered = self.pin(data)
        tampered["assets"]["test-x86_64"]["sha256"] = "0" * 64
        with self.assertRaisesRegex(core_pin.PinError, "is not the pinned"):
            self.install(data, tampered)
        with self.assertRaisesRegex(core_pin.PinError, "not " + core_pin.load()["release"]["version"]):
            self.install(data)
        self.assertEqual(list(self.dest.iterdir()), [])

    def test_escaping_members_and_links_are_refused_before_extraction(self) -> None:
        version = core_pin.load()["release"]["version"]
        for extra in ([("jacquard-x/link", None, "../../outside")], [("jacquard-x/abs", None, "/etc")],
                      [("../escape", b"x", None)], [("/absolute", b"x", None)],
                      [("jacquard-x/link", None, "../.."), ("jacquard-x/link/escaped", b"x", None)],
                      [("jacquard-x/s", None, "."), ("jacquard-x/t", None, "s/s/s/../../../..")]):
            with self.subTest(extra=extra), self.assertRaisesRegex(core_pin.PinError, "refusing"):
                self.install(release(version, extra))
        self.assertEqual(list(self.dest.iterdir()), [])

    def test_sibling_links_are_kept_and_hardlinks_refused(self) -> None:
        version = core_pin.load()["release"]["version"]
        env = self.install(release(version, [("jacquard-x/libexec/jacquard/jac", None, "jacquard")]))
        self.assertTrue(Path(env["JACQUARD_BIN"]).with_name("jac").is_symlink())
        buffer = io.BytesIO()
        with tarfile.open(fileobj=io.BytesIO(release(version))) as source, tarfile.open(fileobj=buffer, mode="w:gz") as tar:
            for member in source.getmembers():
                tar.addfile(member, source.extractfile(member) if member.isfile() else None)
            hard = tarfile.TarInfo("jacquard-x/hard")
            hard.type, hard.linkname = tarfile.LNKTYPE, "jacquard-x/libexec/jacquard/jacquard"
            tar.addfile(hard)
        shutil.rmtree(self.dest)
        self.dest.mkdir()
        with self.assertRaisesRegex(core_pin.PinError, "refusing"):
            self.install(buffer.getvalue())

    def test_missing_platform_asset_points_to_a_source_build(self) -> None:
        with self.assertRaisesRegex(core_pin.PinError, "from source"):
            core_pin.install(core_pin.load(), self.dest, "plan9-mips")


class UpdateTest(unittest.TestCase):
    def setUp(self) -> None:
        self.pin = core_pin.load()
        self.kit = Path(tempfile.mkdtemp(prefix="core-kit-"))
        self.addCleanup(shutil.rmtree, self.kit)
        shutil.copytree(core_pin.KIT_DIR, self.kit, dirs_exist_ok=True)
        self.go_kit = Path(tempfile.mkdtemp(prefix="core-go-kit-"))
        self.addCleanup(shutil.rmtree, self.go_kit)
        shutil.copytree(core_pin.KIT_DIR, self.go_kit, dirs_exist_ok=True)
        self.files = {name: (core_pin.KIT_DIR / name).read_bytes() for name in (*core_pin.KIT_FILES, *core_pin.KIT_EXTRAS)}

    def run_update(self, manifest_changes: dict, assets: dict) -> dict:
        files = dict(self.files)
        manifest = json.loads(files["kit/kit.json"])
        manifest.update(manifest_changes)
        files["kit/kit.json"] = json.dumps(manifest).encode()
        with patch.object(core_pin, "tag_commit", return_value="e" * 40), \
             patch.object(core_pin, "kit_file", side_effect=lambda repo, commit, path, checkout: files[path.split("/", 2)[2]]), \
             patch.object(core_pin, "release_assets", return_value=assets) as self.fetched:
            return core_pin.update(self.pin, "jacquard-core-9.9.9", None, (self.kit, self.go_kit))

    def test_a_release_is_adopted_with_its_kit_and_assets(self) -> None:
        assets = {"linux-x86_64": {"name": "jacquard-linux-x86_64.tar.gz", "sha256": "c" * 64}}
        new = self.run_update({"core_version": "9.9.9"}, assets)
        self.assertEqual(new["release"], {"tag": "jacquard-core-9.9.9", "version": "9.9.9", "commit": "e" * 40})
        self.assertEqual(new["assets"], assets)
        written = (self.kit / "kit/kit.json").read_bytes()
        self.assertEqual(new["kit"]["digests"]["kit/kit.json"], core_pin.sha256(written))
        self.assertEqual((self.go_kit / "kit/kit.json").read_bytes(), written)
        self.assertEqual([path.name for path in self.kit.rglob(".*.core-pin")], [])

    def test_a_failed_replacement_restores_every_kit_file(self) -> None:
        before = {name: (self.kit / name).read_bytes() for name in core_pin.KIT_FILES}
        real_replace, calls = Path.replace, []
        def flaky(path, target):
            calls.append(path)
            if len(calls) == 2:
                raise OSError("synthetic rename failure")
            return real_replace(path, target)
        with patch.object(Path, "replace", flaky), self.assertRaisesRegex(OSError, "synthetic"):
            core_pin.write_kit({name: b"changed" for name in core_pin.KIT_FILES}, self.kit)
        self.assertEqual({name: (self.kit / name).read_bytes() for name in core_pin.KIT_FILES}, before)
        self.assertEqual([path.name for path in self.kit.rglob(".*.core-pin")], [])

    def test_restore_skips_unchanged_files_and_attempts_every_file(self) -> None:
        unchanged, changed, created = self.kit / "vectors.json", self.kit / "kit/kit.json", self.kit / "new.txt"
        originals = {unchanged: unchanged.read_bytes(), changed: changed.read_bytes(), created: None}
        changed.write_bytes(b"changed")
        created.write_bytes(b"created")
        real_write = Path.write_bytes
        def refuse_unchanged(path, data):
            if path == unchanged:
                raise PermissionError("read-only")
            return real_write(path, data)
        with patch.object(Path, "write_bytes", refuse_unchanged):
            core_pin.restore(originals)
        self.assertEqual(changed.read_bytes(), originals[changed])
        self.assertFalse(created.exists())
        changed.write_bytes(b"changed again")
        unchanged.write_bytes(b"also changed")
        with patch.object(Path, "write_bytes", refuse_unchanged), self.assertRaisesRegex(core_pin.PinError, "could not restore"):
            core_pin.restore(originals)
        self.assertEqual(changed.read_bytes(), originals[changed])

    def test_protocol_change_mismatched_version_or_missing_linux_asset_is_refused(self) -> None:
        linux = {"linux-x86_64": {"name": "jacquard-linux-x86_64.tar.gz", "sha256": "c" * 64}}
        cases = [({"core_version": "9.9.9", "pending_decisions": [{"name": 'x"); import os; ("'}]}, linux, "plain lowercase"),
                 ({"core_version": "9.9.9", "protocol": "jacquard-host-v1"}, linux, "protocol change"),
                 ({"core_version": "9.9.8"}, linux, "core_version"),
                 ({"core_version": "9.9.9"}, {}, "linux-x86_64")]
        before = {name: (self.kit / name).read_bytes() for name in core_pin.KIT_FILES}
        for changes, assets, message in cases:
            with self.subTest(message=message), self.assertRaisesRegex(core_pin.PinError, message):
                self.run_update(changes, assets)
            if message == "protocol change":
                self.fetched.assert_not_called()
        self.assertEqual({name: (self.kit / name).read_bytes() for name in core_pin.KIT_FILES}, before)


class HardeningTest(unittest.TestCase):
    def setUp(self) -> None:
        self.pin = core_pin.load()

    def test_maintainer_fields_are_validated(self) -> None:
        for path, value in ((("repository",), "owner/../name"), (("repository",), "no-slash"),
                            (("kit", "path"), "../outside"), (("kit", "path"), "/absolute"),
                            (("kit", "evidence"), "docs/./release"), (("kit", "evidence"), "a b")):
            broken = json.loads(json.dumps(self.pin))
            target = broken
            for key in path[:-1]:
                target = target[key]
            target[path[-1]] = value
            with self.subTest(field=path, value=value), self.assertRaises(core_pin.PinError):
                core_pin.validate(broken)

    def test_malformed_decisions_and_tags_are_refused_before_any_fetch(self) -> None:
        for entries in ("x", ["name"], [{"name": 3}]):
            with self.subTest(entries=entries), self.assertRaisesRegex(core_pin.PinError, "pending_decisions"):
                core_pin.decision_names({"pending_decisions": entries})
        with patch.object(core_pin, "tag_commit", side_effect=AssertionError("fetched")) as fetched:
            for tag in ("jacquard-core-latest", "jacquard-core-1.2", "v1.2.3", "jacquard-core-1.2.3; rm"):
                with self.subTest(tag=tag), self.assertRaisesRegex(core_pin.PinError, "release tags"):
                    core_pin.update(self.pin, tag, None)
            fetched.assert_not_called()

    def copy(self) -> tuple[Path, Path, tuple[Path, ...]]:
        root = Path(tempfile.mkdtemp(prefix="core-adopt-"))
        self.addCleanup(shutil.rmtree, root)
        for name in [*core_pin.GENERATED, *core_pin.BLOCKS]:
            (root / name).parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(REPO / name, root / name)
        kit_dirs = tuple(root / directory.relative_to(REPO) for directory in core_pin.KIT_DIRS)
        for source, target in zip(core_pin.KIT_DIRS, kit_dirs):
            shutil.copytree(source, target)
        pin_path = root / "core-pin.json"
        shutil.copyfile(core_pin.PIN, pin_path)
        return root, pin_path, kit_dirs

    def snapshot(self, root: Path) -> dict[str, bytes]:
        return {str(path.relative_to(root)): path.read_bytes() for path in sorted(root.rglob("*")) if path.is_file()}

    def adopt(self, root: Path, pin_path: Path, kit_dirs: tuple[Path, ...]) -> None:
        files = {name: (core_pin.KIT_DIR / name).read_bytes() for name in (*core_pin.KIT_FILES, *core_pin.KIT_EXTRAS)}
        manifest = json.loads(files["kit/kit.json"])
        manifest["core_version"] = "9.9.9"
        files["kit/kit.json"] = json.dumps(manifest).encode()
        assets = {"linux-x86_64": {"name": "jacquard-linux-x86_64.tar.gz", "sha256": "c" * 64}}
        with patch.object(core_pin, "tag_commit", return_value="e" * 40), \
             patch.object(core_pin, "kit_file", side_effect=lambda repo, commit, path, checkout: files[path.split("/", 2)[2]]), \
             patch.object(core_pin, "release_assets", return_value=assets):
            core_pin.adopt(self.pin, "jacquard-core-9.9.9", None, root=root, pin_path=pin_path, kit_dirs=kit_dirs)

    def test_adoption_succeeds_end_to_end(self) -> None:
        root, pin_path, kit_dirs = self.copy()
        self.adopt(root, pin_path, kit_dirs)
        adopted = core_pin.load(pin_path)
        self.assertEqual(adopted["release"]["tag"], "jacquard-core-9.9.9")
        self.assertEqual(core_pin.problems(adopted, root), [])

    def test_a_failed_sync_restores_pin_kit_and_generated_files(self) -> None:
        root, pin_path, kit_dirs = self.copy()
        before = self.snapshot(root)
        with patch.object(core_pin, "sync", side_effect=OSError("synthetic sync failure")), \
             self.assertRaisesRegex(OSError, "synthetic sync failure"):
            self.adopt(root, pin_path, kit_dirs)
        self.assertEqual(self.snapshot(root), before)

    def test_a_failed_restore_reports_the_original_failure(self) -> None:
        root, pin_path, kit_dirs = self.copy()
        with patch.object(core_pin, "sync", side_effect=OSError("synthetic sync failure")), \
             patch.object(core_pin, "restore", side_effect=core_pin.PinError("could not restore")), \
             self.assertRaisesRegex(core_pin.PinError, "could not restore.*synthetic sync failure"):
            self.adopt(root, pin_path, kit_dirs)

    def test_a_partial_staging_write_leaves_originals_and_no_temporaries(self) -> None:
        root, _, kit_dirs = self.copy()
        targets = {kit_dirs[0] / name: b"changed" for name in core_pin.KIT_FILES}
        before = {path: path.read_bytes() for path in targets}
        real_write, calls = Path.write_bytes, []
        def flaky(path, data):
            if path.name.endswith(".core-pin"):
                calls.append(path)
                if len(calls) == 2:
                    path.write_text("partial")
                    raise OSError("disk full")
            return real_write(path, data)
        with patch.object(Path, "write_bytes", flaky), self.assertRaisesRegex(OSError, "disk full"):
            core_pin.write_files(targets)
        self.assertEqual({path: path.read_bytes() for path in targets}, before)
        self.assertEqual([path for path in root.rglob("*.core-pin")], [])


if __name__ == "__main__":
    unittest.main()
