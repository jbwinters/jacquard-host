"""The README embedding example, run for real (skipped without Core)."""

import os
import shutil
import subprocess
import tempfile
import unittest

from jacquard_host.adapter import Host
from jacquard_host.identities import hash_file, parse_signature, signatures
from jacquard_host.registry import HostFailure, OperationRegistry

JACQUARD_BIN = os.environ.get("JACQUARD_BIN")
JACQUARD_PRELUDE = os.environ.get("JACQUARD_PRELUDE")

DECLS = """\
once effect Documents where {
  read-pdf : (Text) -> Text
}

summarize : (Text) ->{Documents} Text
summarize(path) = text.slice(read-pdf(path), 0, 80)
"""


@unittest.skipUnless(JACQUARD_BIN and JACQUARD_PRELUDE and os.path.isdir(JACQUARD_PRELUDE), "set JACQUARD_BIN and JACQUARD_PRELUDE to run the README example")
class ReadmeExampleTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.scratch = tempfile.mkdtemp(prefix="jacquard-host-readme-")
        cls.decls = os.path.join(cls.scratch, "decls.jac")
        with open(cls.decls, "w", encoding="utf-8") as stream:
            stream.write(DECLS)
        cls.store = os.path.join(cls.scratch, "store")
        env = dict(os.environ, JACQUARD_PRELUDE=os.path.abspath(JACQUARD_PRELUDE))
        subprocess.run([JACQUARD_BIN, "run", cls.decls, "--store", cls.store], check=True, stdout=subprocess.DEVNULL, env=env)
        command, prelude = [JACQUARD_BIN], JACQUARD_PRELUDE
        cls.names = hash_file(cls.decls, command=command, prelude=prelude) | hash_file(os.path.join(prelude, "01-prim.jqd"), command=command, prelude=prelude)
        cls.interface = parse_signature(signatures(cls.decls, command=command, prelude=prelude)["summarize"], cls.names)

    @classmethod
    def tearDownClass(cls) -> None:
        shutil.rmtree(cls.scratch, ignore_errors=True)

    def registry(self, root: str) -> OperationRegistry:
        registry = OperationRegistry()

        @registry.operation(self.names["documents"], self.names["read-pdf"])
        def read_pdf(path: str) -> str:
            if not path.startswith(root):
                raise HostFailure("refused_authority", "not_started", "outside the document root")
            return "PDF TEXT " * 20

        return registry

    def test_python_serves_read_pdf_to_the_jacquard_term(self) -> None:
        host = Host(self.store, command=[JACQUARD_BIN])
        result = host.invoke(self.names["summarize"], self.interface, ["/srv/docs/report.pdf"], self.registry("/srv/docs/"))
        self.assertTrue(result.ok, result)
        self.assertEqual(result.value, ("PDF TEXT " * 20)[:80])
        self.assertEqual([r.operation for r in result.requests], [self.names["read-pdf"]])

    def test_refusal_reaches_the_caller_as_a_host_failure(self) -> None:
        host = Host(self.store, command=[JACQUARD_BIN])
        result = host.invoke(self.names["summarize"], self.interface, ["/etc/passwd"], self.registry("/srv/docs/"))
        self.assertEqual((result.kind, result.terminal, result.primary_code), ("error", "host_failure", "E1607"))
        self.assertTrue(result.diagnostics[0].cause.endswith("outside the document root"))


if __name__ == "__main__":
    unittest.main()
