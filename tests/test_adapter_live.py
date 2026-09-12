"""The embedding adapter against a real worker and the kit fixtures.

Skipped unless JACQUARD_BIN and JACQUARD_PRELUDE are set.
"""

import os
import shutil
import tempfile
import unittest

from jacquard_host.adapter import Host
from jacquard_host.kit import build_store, load_kit
from jacquard_host.protocol.envelopes import Interface
from jacquard_host.protocol.values import Hash, Nominal, TupleType
from jacquard_host.registry import HostFailure, OperationRegistry

JACQUARD_BIN = os.environ.get("JACQUARD_BIN")
JACQUARD_PRELUDE = os.environ.get("JACQUARD_PRELUDE")
KIT = load_kit()
IDS = {name: Hash(value) for name, value in KIT.manifest["identities"].items()}
TEXT, INT = Nominal(IDS["text"]), Nominal(IDS["int"])
ECHO = Interface((TEXT,), TEXT, (IDS["kit-world"],))


@unittest.skipUnless(JACQUARD_BIN and JACQUARD_PRELUDE and os.path.isdir(JACQUARD_PRELUDE), "set JACQUARD_BIN and JACQUARD_PRELUDE to run the adapter against Core")
class LiveAdapterTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.scratch = tempfile.mkdtemp(prefix="jacquard-host-adapter-live-")
        cls.store = os.path.join(cls.scratch, "store")
        build_store([JACQUARD_BIN], fixtures=KIT.fixtures, prelude=JACQUARD_PRELUDE, store=cls.store)
        cls.host = Host(cls.store, command=[JACQUARD_BIN], deadline_seconds=30)

    @classmethod
    def tearDownClass(cls) -> None:
        shutil.rmtree(cls.scratch, ignore_errors=True)

    def test_pure_term_with_an_argument(self) -> None:
        result = self.host.invoke(IDS["kit-double"], Interface((INT,), INT), (21,))
        self.assertEqual((result.kind, result.value, result.exit_status), ("ok", 42, 0))
        self.assertLessEqual(len(result.stderr), 65536)

    def test_python_function_called_from_jacquard(self) -> None:
        registry = OperationRegistry()
        calls = []

        @registry.operation(IDS["kit-world"], IDS["send"])
        def send(body: str) -> str:
            calls.append(body)
            return f"{body}!"

        result = self.host.invoke(IDS["kit-twice"], ECHO, ("ping",), registry)
        self.assertEqual((result.kind, result.value), ("ok", "ping!!"))
        self.assertEqual(calls, ["ping", "ping!"])
        self.assertEqual([r.ordinal for r in result.requests], [1, 2])
        self.assertEqual([o["ordinal"] for o in result.evidence["core"]["effect_requests"]], [1, 2])

    def test_runtime_failure_is_a_diagnostic_outcome(self) -> None:
        result = self.host.invoke(IDS["kit-fail"], Interface((INT,), INT), (1,))
        self.assertEqual((result.kind, result.terminal, result.exit_status), ("error", "diagnostic", 0))
        self.assertTrue(result.diagnostics)

    def test_handler_failures_map_to_the_frozen_terminals(self) -> None:
        cases = {
            ("refused_authority", "not_started"): ("host_failure", "E1607"),
            ("outside_failure", "failed"): ("host_failure", "E1613"),
            ("completion_unknown", "unknown"): ("completion_unknown", "E1612"),
            ("unsupported_operation", "not_started"): ("host_failure", "E1606"),
        }
        for (category, completion), (terminal, code) in cases.items():
            with self.subTest(category=category):
                registry = OperationRegistry()
                registry.bind(IDS["kit-world"], IDS["send"], lambda body, c=category, k=completion: (_ for _ in ()).throw(HostFailure(c, k, "redacted")))
                result = self.host.invoke(IDS["kit-echo"], ECHO, ("ping",), registry)
                self.assertEqual((result.kind, result.terminal, result.primary_code), ("error", terminal, code))
                self.assertTrue(result.diagnostics[0].cause.endswith("redacted"))

    def test_unbound_operation_is_refused_by_core_before_any_request(self) -> None:
        result = self.host.invoke(IDS["kit-echo"], ECHO, ("ping",), OperationRegistry())
        self.assertEqual((result.kind, result.primary_code, len(result.requests)), ("error", "E1606", 0))

    def test_handler_exception_is_reported_as_unknown_completion(self) -> None:
        registry = OperationRegistry()
        registry.bind(IDS["kit-world"], IDS["send"], lambda body: 1 / 0)
        result = self.host.invoke(IDS["kit-echo"], ECHO, ("ping",), registry)
        self.assertEqual((result.kind, result.terminal, result.primary_code), ("error", "completion_unknown", "E1612"))

    def test_handler_result_without_an_encoding_is_reported_not_crashed(self) -> None:
        registry = OperationRegistry()
        registry.bind(IDS["kit-world"], IDS["send"], lambda body: object())
        result = self.host.invoke(IDS["kit-echo"], ECHO, ("ping",), registry)
        self.assertEqual((result.kind, result.terminal, result.primary_code), ("error", "host_failure", "E1613"))

    def test_wrong_typed_handler_result_is_refused_by_core(self) -> None:
        registry = OperationRegistry()
        registry.bind(IDS["kit-world"], IDS["send"], lambda body: 7)
        result = self.host.invoke(IDS["kit-echo"], ECHO, ("ping",), registry)
        self.assertEqual((result.kind, result.primary_code), ("error", "E1608"))

    def test_interface_mismatch_is_fatal_before_evaluation(self) -> None:
        result = self.host.invoke(IDS["kit-double"], Interface((TEXT,), INT), ("x",))
        self.assertEqual((result.kind, result.primary_code, result.exit_status), ("fatal", "E1603", 0))

    def test_shutdown_probe(self) -> None:
        result = self.host.shutdown_probe()
        self.assertEqual((result.kind, result.exit_status), ("shutdown", 0))

    def test_missing_store_is_a_startup_failure_not_evidence(self) -> None:
        result = Host(os.path.join(self.scratch, "missing"), command=[JACQUARD_BIN], deadline_seconds=10).shutdown_probe()
        self.assertEqual(result.kind, "carrier_loss")
        self.assertNotEqual(result.exit_status, 0)


if __name__ == "__main__":
    unittest.main()
