"""Run the Jacquard HTTP subset's Warp tests through Core (skipped without Core)."""

from __future__ import annotations

import os
import re
import subprocess
import tempfile
import unittest
from pathlib import Path

JACQUARD_BIN = os.environ.get("JACQUARD_BIN")
JACQUARD_PRELUDE = os.environ.get("JACQUARD_PRELUDE")
JACQUARD_DIR = Path(__file__).resolve().parents[1] / "jacquard"


def core(*args: str) -> subprocess.CompletedProcess:
    env = dict(os.environ, JACQUARD_PRELUDE=os.path.abspath(JACQUARD_PRELUDE))
    return subprocess.run([JACQUARD_BIN, *args], capture_output=True, text=True, env=env, check=False)


@unittest.skipUnless(JACQUARD_BIN and JACQUARD_PRELUDE and os.path.isdir(JACQUARD_PRELUDE), "set JACQUARD_BIN and JACQUARD_PRELUDE to run the Jacquard HTTP tests")
class HttpJacquardTest(unittest.TestCase):
    def test_library_checks_with_the_expected_signatures(self) -> None:
        checked = core("check", str(JACQUARD_DIR / "http.jac"), "--print-sigs")
        self.assertEqual(checked.returncode, 0, checked.stderr)
        self.assertIn("http.parse : (Text) ->{} HttpParsed", checked.stdout)
        self.assertIn("http.render : (HttpResponse) ->{} Text", checked.stdout)
        self.assertIn("http.route : forall a. (HttpRequest, List (HttpRoute, a)) ->{} Option a", checked.stdout)

    def test_a_maximal_head_parses_within_the_worker_deadline(self) -> None:
        import time
        head = "GET /" + "a" * 511 + " HTTP/1.1\r\nHost: h\r\n" + "".join(f"X{i}: " + "y" * 1015 + "\r\n" for i in range(30)) + "\r\n"
        body = "b" * 65536
        raw = head.replace("Host: h\r\n", "Host: h\r\nContent-Length: 65536\r\n", 1).replace("GET", "POST", 1) + body
        with tempfile.TemporaryDirectory(prefix="jacquard-host-http-max-") as scratch:
            probe = Path(scratch) / "max.jac"
            escaped = raw.replace("\\", "\\\\").replace('"', '\\"').replace("\r", "\\r").replace("\n", "\\n")
            probe.write_text((JACQUARD_DIR / "http.jac").read_text(encoding="utf-8") + f'\nprobe = http.parse("{escaped}")\nmatch probe {{ | HttpOk(HttpRequest(headers: hs, body: b)) -> (list.length(hs), text.length(b)) | HttpBad(HttpReject(status: s)) -> (s, 0) }}\n', encoding="utf-8")
            started = time.monotonic()
            result = core("run", str(probe))
            elapsed = time.monotonic() - started
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("(32, 65536)", result.stdout)
        self.assertLess(elapsed, 20.0, f"maximal head took {elapsed:.1f}s")

    def test_warp_suite_passes_with_a_fixed_seed(self) -> None:
        with tempfile.TemporaryDirectory(prefix="jacquard-host-http-") as scratch:
            combined = Path(scratch) / "http-all.jac"
            combined.write_text(
                (JACQUARD_DIR / "http.jac").read_text(encoding="utf-8") + "\n" + (JACQUARD_DIR / "http-tests.jac").read_text(encoding="utf-8"),
                encoding="utf-8",
            )
            result = core("test", str(combined), "--seed", "42", "--cache-dir", os.path.join(scratch, "cache"))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        summary = re.search(r"(\d+) passed, (\d+) failed, (\d+) skipped, (\d+) refused", result.stdout)
        self.assertIsNotNone(summary, result.stdout)
        passed, failed, skipped, refused = (int(n) for n in summary.groups())
        self.assertEqual((failed, skipped, refused), (0, 0, 0), result.stdout)
        self.assertEqual(passed, 47)


if __name__ == "__main__":
    unittest.main()
