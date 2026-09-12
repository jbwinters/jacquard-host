"""The embedding adapter against the stand-in worker (no Core needed)."""

import os
import sys
import unittest
from pathlib import Path

from jacquard_host.adapter import Host
from jacquard_host.kit import load_kit
from jacquard_host.protocol.envelopes import Interface
from jacquard_host.protocol.values import Hash, Nominal, TupleType
from jacquard_host.registry import HostFailure, OperationRegistry

KIT = load_kit()
IDS = {name: Hash(value) for name, value in KIT.manifest["identities"].items()}
TEXT = Nominal(IDS["text"])
ECHO = Interface((TEXT,), TEXT, (IDS["kit-world"],))
REPLAY_WORKER = Path(__file__).resolve().parent / "replay_worker.py"


def replay_host(case: str, **overrides) -> Host:
    env = dict(os.environ, REPLAY_TRANSCRIPTS=str(KIT.directory / "kit" / "transcripts.json"), REPLAY_CASE=case)
    return Host("unused-store", command=[sys.executable, str(REPLAY_WORKER)], env=env, deadline_seconds=overrides.pop("deadline_seconds", 20), **overrides)


class ReplayedAdapterTest(unittest.TestCase):
    def test_pure_invocation(self) -> None:
        result = replay_host("pure-success").invoke(IDS["kit-ready"], Interface((), TupleType()))
        self.assertEqual((result.kind, result.value, result.terminal, result.exit_status), ("ok", (), "ok", 0))
        self.assertEqual([f["kind"] for f in result.host_frames], ["host_select", "invoke"])
        self.assertEqual([f["kind"] for f in result.core_frames], ["core_hello", "outcome"])
        self.assertEqual(result.evidence["core"]["terminal"], "ok")

    def test_python_function_serves_the_effect(self) -> None:
        registry = OperationRegistry()
        calls = []

        @registry.operation(IDS["kit-world"], IDS["send"])
        def send(body: str) -> str:
            calls.append(body)
            return "pong"

        result = replay_host("one-once-effect-success").invoke(IDS["kit-echo"], ECHO, ("ping",), registry)
        self.assertEqual((result.kind, result.value, calls), ("ok", "pong", ["ping"]))
        self.assertEqual([f["kind"] for f in result.host_frames], ["host_select", "invoke", "effect_ok"])
        self.assertEqual(result.host_frames[2], KIT.transcript("one-once-effect-success")["host_input"][4]["frame"])
        self.assertEqual(len(result.requests), 1)
        self.assertEqual(result.evidence["host_observations"]["responses"], [{"category": "ok", "completion": "completed", "ordinal": 1}])

    def test_refused_authority_from_the_handler(self) -> None:
        registry = OperationRegistry()
        registry.bind(IDS["kit-world"], IDS["send"], lambda body: (_ for _ in ()).throw(HostFailure("refused_authority", "not_started", "policy denied")))
        result = replay_host("refused-authority").invoke(IDS["kit-echo"], ECHO, ("ping",), registry)
        self.assertEqual((result.kind, result.terminal, result.primary_code), ("error", "host_failure", "E1607"))
        self.assertEqual(result.host_frames[2], KIT.transcript("refused-authority")["host_input"][4]["frame"])

    def test_shutdown_probe(self) -> None:
        result = replay_host("selected-shutdown").shutdown_probe()
        self.assertEqual((result.kind, result.terminal, result.exit_status), ("shutdown", "shutdown", 0))

    def test_fatal_from_core(self) -> None:
        result = replay_host("interface-mismatch").invoke(IDS["kit-echo"], ECHO, ("ping",))
        self.assertEqual((result.kind, result.primary_code, result.exit_status), ("fatal", "E1603", 0))
        self.assertEqual(result.host_evidence, {})

    def test_malformed_identity_from_core_is_a_local_protocol_error(self) -> None:
        program = (
            "import json,sys\n"
            "t=json.load(open(sys.argv[1]))\n"
            "c=[e for e in t if e['name']=='one-once-effect-success'][0]['core_output']\n"
            "def w(f):\n"
            "  p=json.dumps(f).encode(); sys.stdout.buffer.write(len(p).to_bytes(4,'big')+p); sys.stdout.buffer.flush()\n"
            "def r():\n"
            "  n=int.from_bytes(sys.stdin.buffer.read(4),'big'); sys.stdin.buffer.read(n)\n"
            "w(c[0]); r(); r(); req=dict(c[1]); req['effect']=req['effect'].upper(); w(req); sys.stdin.buffer.read()\n"
        )
        host = Host("unused-store", command=[sys.executable, "-c", program, str(KIT.directory / "kit" / "transcripts.json")], deadline_seconds=10)
        result = host.invoke(IDS["kit-echo"], ECHO, ("ping",), OperationRegistry())
        self.assertEqual((result.kind, result.host_evidence["code"], result.host_evidence["reason"]), ("protocol_error", "E1601", "core_frame_refused"))

    def test_host_side_invoke_refusal_shuts_the_worker_down_cleanly(self) -> None:
        from jacquard_host.protocol.values import Nominal as _N  # noqa: F401 - keep import local
        registry = OperationRegistry()
        registry.bind(IDS["kit-world"], IDS["send"], lambda body: body)
        # a registry effect outside the interface row: the host refuses its own invoke
        result = replay_host("selected-shutdown").invoke(IDS["kit-ready"], Interface((), TupleType()), (), registry)
        self.assertEqual((result.kind, result.host_evidence["reason"], result.host_evidence["code"]), ("host_error", "invoke_refused_locally", "E1601"))
        self.assertEqual([f["kind"] for f in result.host_frames], ["host_select", "shutdown"])
        self.assertEqual([f["kind"] for f in result.core_frames], ["core_hello", "shutdown_ack"])
        self.assertEqual((result.exit_status, result.signal), (0, None))
        result = replay_host("selected-shutdown").invoke(IDS["kit-double"], Interface((Nominal(IDS["int"]),), Nominal(IDS["int"])), (True,))
        self.assertEqual((result.kind, result.host_evidence["code"]), ("host_error", "E1604"))

    def test_a_host_frame_over_the_selected_limits_is_a_host_error(self) -> None:
        host = replay_host("pure-success", limits={**__import__("jacquard_host.protocol.limits", fromlist=["HARD_LIMITS"]).HARD_LIMITS, "max_frame_bytes": 64})
        result = host.invoke(IDS["kit-ready"], Interface((), TupleType()))
        self.assertEqual((result.kind, result.host_evidence["reason"], result.host_evidence["code"]), ("host_error", "host_frame_refused", "E1602"))

    def test_long_failure_messages_are_cut_to_the_selected_limit(self) -> None:
        registry = OperationRegistry()
        registry.bind(IDS["kit-world"], IDS["send"], lambda body: (_ for _ in ()).throw(HostFailure("refused_authority", "not_started", "é" * 3000)))
        result = replay_host("refused-authority").invoke(IDS["kit-echo"], ECHO, ("ping",), registry)
        message = result.host_frames[2]["message"]
        self.assertEqual(len(message.encode("utf-8")), 4096)
        self.assertEqual(message, "é" * 2048)

    def test_descriptors_are_released_across_invocations(self) -> None:
        if not os.path.isdir("/proc/self/fd"):
            self.skipTest("no /proc")
        before = len(os.listdir("/proc/self/fd"))
        for _ in range(3):
            replay_host("pure-success").invoke(IDS["kit-ready"], Interface((), TupleType()))
            Host("unused-store", command=[sys.executable, "-c", "pass"], deadline_seconds=5).invoke(IDS["kit-ready"], Interface((), TupleType()))
            Host("unused-store", command=[sys.executable, "-c", "import sys; sys.stdout.buffer.write(b'\\x00\\x00\\x00\\x02[]'); sys.stdout.flush(); sys.stdin.read()"], deadline_seconds=5).invoke(IDS["kit-ready"], Interface((), TupleType()))
            Host("unused-store", command=[sys.executable, "-c", "import time; time.sleep(30)"], deadline_seconds=0.2).invoke(IDS["kit-ready"], Interface((), TupleType()))
        self.assertEqual(len(os.listdir("/proc/self/fd")), before)
        import glob
        import tempfile
        self.assertEqual(glob.glob(os.path.join(tempfile.gettempdir(), "jacquard-host-worker-stderr-*")), [])

    def test_worker_that_exits_immediately_is_carrier_loss(self) -> None:
        host = Host("unused-store", command=[sys.executable, "-c", "pass"], deadline_seconds=5)
        result = host.invoke(IDS["kit-ready"], Interface((), TupleType()))
        self.assertEqual(result.kind, "carrier_loss")
        self.assertEqual((result.host_evidence["code"], result.host_evidence["reason"], result.host_evidence["state"]), ("E1611", "carrier_loss", "hello"))
        self.assertEqual(result.exit_status, 0)

    def test_silent_worker_hits_the_host_deadline(self) -> None:
        host = Host("unused-store", command=[sys.executable, "-c", "import time; time.sleep(30)"], deadline_seconds=0.3)
        result = host.invoke(IDS["kit-ready"], Interface((), TupleType()))
        self.assertEqual((result.kind, result.host_evidence["reason"]), ("carrier_loss", "deadline"))
        self.assertIsNone(result.exit_status)
        self.assertIsNotNone(result.signal)

    def test_garbage_from_the_worker_is_a_local_protocol_error(self) -> None:
        program = "import sys; sys.stdout.buffer.write(b'\\x00\\x00\\x00\\x02[]'); sys.stdout.flush(); sys.stdin.read()"
        host = Host("unused-store", command=[sys.executable, "-c", program], deadline_seconds=5)
        result = host.invoke(IDS["kit-ready"], Interface((), TupleType()))
        self.assertEqual((result.kind, result.host_evidence["code"]), ("protocol_error", "E1601"))


if __name__ == "__main__":
    unittest.main()
