import unittest

from jacquard_host.kit import load_kit
from jacquard_host.protocol import envelopes
from jacquard_host.protocol.envelopes import (
    EffectRequest,
    Fatal,
    Interface,
    OperationEntry,
    Outcome,
    ShutdownAck,
    parse_core_frame,
    parse_core_hello,
    request_id,
)
from jacquard_host.protocol.errors import LimitExceeded, MalformedFrame, OrderViolation
from jacquard_host.protocol.limits import HARD_LIMITS
from jacquard_host.protocol.values import Hash, Nominal, TupleType

KIT = load_kit()
IDS = {name: Hash(value) for name, value in KIT.manifest["identities"].items()}
TEXT = Nominal(IDS["text"])


class RequestIdTest(unittest.TestCase):
    def test_sixteen_hex_digits_from_one(self) -> None:
        self.assertEqual(request_id(1), "0000000000000001")
        self.assertEqual(request_id(255), "00000000000000ff")
        with self.assertRaises(ValueError):
            request_id(0)


class HostFrameTest(unittest.TestCase):
    def test_host_select_and_shutdown_are_exact(self) -> None:
        self.assertEqual(envelopes.host_select(HARD_LIMITS), KIT.transcript("pure-success")["host_input"][1]["frame"])
        self.assertEqual(envelopes.shutdown(), {"kind": "shutdown", "protocol": "jacquard-host-v0"})
        with self.assertRaises(LimitExceeded):
            envelopes.host_select({**HARD_LIMITS, "max_frame_bytes": 0})

    def test_invoke_reproduces_the_recorded_pure_and_effect_frames(self) -> None:
        pure = envelopes.invoke(IDS["kit-ready"], Interface((), TupleType()), (), (), limits=HARD_LIMITS)
        self.assertEqual(pure, KIT.transcript("pure-success")["host_input"][2]["frame"])
        interface = Interface((TEXT,), TEXT, (IDS["kit-world"],))
        effect = envelopes.invoke(
            IDS["kit-echo"], interface, ("ping",), (OperationEntry(IDS["kit-world"], IDS["send"]),), limits=HARD_LIMITS
        )
        self.assertEqual(effect, KIT.transcript("one-once-effect-success")["host_input"][2]["frame"])

    def test_invoke_refuses_inconsistent_inputs(self) -> None:
        with self.assertRaises(MalformedFrame):
            envelopes.invoke(IDS["kit-double"], Interface((TEXT,), TEXT), (), (), limits=HARD_LIMITS)
        with self.assertRaises(MalformedFrame):
            envelopes.invoke(IDS["kit-ready"], Interface((), TupleType()), (), (OperationEntry(IDS["kit-world"], IDS["send"]),), limits=HARD_LIMITS)
        with self.assertRaises(ValueError):
            Interface((), TupleType(), (IDS["send"], IDS["kit-world"]) if IDS["send"] > IDS["kit-world"] else (IDS["kit-world"], IDS["send"], IDS["send"]))

    def test_responses_match_the_recorded_frames(self) -> None:
        self.assertEqual(envelopes.effect_ok(1, "pong", limits=HARD_LIMITS), KIT.transcript("one-once-effect-success")["host_input"][4]["frame"])
        self.assertEqual(
            envelopes.effect_failure(1, "refused_authority", "not_started", "policy denied", limits=HARD_LIMITS),
            KIT.transcript("refused-authority")["host_input"][4]["frame"],
        )
        self.assertEqual(envelopes.cancel(1, "timeout", "unknown"), KIT.transcript("timeout-with-unknown-completion")["host_input"][4]["frame"])

    def test_failure_pairs_reasons_and_message_bounds_are_exact(self) -> None:
        with self.assertRaises(MalformedFrame):
            envelopes.effect_failure(1, "refused_authority", "failed", "", limits=HARD_LIMITS)
        with self.assertRaises(LimitExceeded):
            envelopes.effect_failure(1, "outside_failure", "failed", "x" * 4097, limits=HARD_LIMITS)
        with self.assertRaises(MalformedFrame):
            envelopes.cancel(1, "bored", "unknown")
        with self.assertRaises(MalformedFrame):
            envelopes.cancel(1, "timeout", "completed")


class CoreFrameTest(unittest.TestCase):
    def test_core_hello_must_advertise_the_hard_limits(self) -> None:
        hello = KIT.transcript("pure-success")["core_output"][0]
        parsed = parse_core_hello(hello)
        self.assertEqual(parsed.limits, HARD_LIMITS)
        self.assertEqual(parsed.versions, ("jacquard-host-v0",))
        with self.assertRaises(MalformedFrame):
            parse_core_hello({**hello, "carrier": "tcp"})
        with self.assertRaises(MalformedFrame):
            parse_core_hello({**hello, "limits": {**hello["limits"], "max_frame_bytes": 1}})
        with self.assertRaises(MalformedFrame):
            parse_core_hello({**hello, "extra": 1})

    def test_recorded_core_frames_parse_into_typed_envelopes(self) -> None:
        for transcript in KIT.transcripts:
            for frame in transcript["core_output"][1:]:
                with self.subTest(transcript=transcript["name"], kind=frame["kind"]):
                    parsed = parse_core_frame(frame, limits=HARD_LIMITS)
                    self.assertIsInstance(parsed, (EffectRequest, Outcome, Fatal, ShutdownAck))
        request = parse_core_frame(KIT.transcript("one-once-effect-success")["core_output"][1], limits=HARD_LIMITS)
        assert isinstance(request, EffectRequest)
        self.assertEqual((request.ordinal, request.effect, request.operation, request.arguments), (1, IDS["kit-world"], IDS["send"], ("ping",)))
        outcome = parse_core_frame(KIT.transcript("refused-authority")["core_output"][2], limits=HARD_LIMITS)
        assert isinstance(outcome, Outcome)
        self.assertFalse(outcome.ok)
        self.assertEqual((outcome.terminal, outcome.diagnostics[0].code), ("host_failure", "E1607"))
        self.assertTrue(outcome.diagnostics[0].cause.endswith("policy denied"))

    def test_core_frames_fail_closed_on_shape_and_ids(self) -> None:
        outcome = KIT.transcript("pure-success")["core_output"][1]
        request = KIT.transcript("one-once-effect-success")["core_output"][1]
        with self.assertRaises(MalformedFrame):
            parse_core_frame({**outcome, "display_name": "x"}, limits=HARD_LIMITS)
        with self.assertRaises(OrderViolation):
            parse_core_frame({**outcome, "invocation_id": "0000000000000001"}, limits=HARD_LIMITS)
        with self.assertRaises(MalformedFrame):
            parse_core_frame({**request, "mode": "multi"}, limits=HARD_LIMITS)
        with self.assertRaises(MalformedFrame):
            parse_core_frame({**request, "request_id": "1"}, limits=HARD_LIMITS)
        with self.assertRaises(MalformedFrame):
            parse_core_frame({**request, "protocol": "jacquard-host-v1"}, limits=HARD_LIMITS)
        with self.assertRaises(OrderViolation):
            parse_core_frame(KIT.transcript("pure-success")["core_output"][0], limits=HARD_LIMITS)
        with self.assertRaises(MalformedFrame):
            parse_core_frame({"kind": "surprise", "protocol": "jacquard-host-v0"}, limits=HARD_LIMITS)
        fatal = KIT.transcript("unknown-version")["core_output"][1]
        bad_diag = {**fatal["diagnostics"][0], "schema": "other"}
        with self.assertRaises(MalformedFrame):
            parse_core_frame({**fatal, "diagnostics": [bad_diag]}, limits=HARD_LIMITS)
        with self.assertRaises(MalformedFrame):
            parse_core_frame({**fatal, "diagnostics": []}, limits=HARD_LIMITS)
        # identities in Core frames fail closed as E1601, never as a Python ValueError
        with self.assertRaises(MalformedFrame):
            parse_core_frame({**request, "effect": request["effect"].upper()}, limits=HARD_LIMITS)
        with self.assertRaises(MalformedFrame):
            parse_core_frame({**request, "operation": 7}, limits=HARD_LIMITS)
        with self.assertRaises(MalformedFrame):
            parse_core_frame({**outcome, "invocation_id": 0}, limits=HARD_LIMITS)

    def test_evidence_and_diagnostic_limits_fail_closed(self) -> None:
        import copy
        base = KIT.transcript("one-once-effect-success")["core_output"][2]
        def mutated(path, value):
            frame = copy.deepcopy(base)
            node = frame
            for key in path[:-1]:
                node = node[key]
            node[path[-1]] = value
            return frame
        for path, value in (
            (("evidence", "core", "target"), "not-an-object"),
            (("evidence", "core", "target", "kind"), "module-v0"),
            (("evidence", "core", "interface"), "x"),
            (("evidence", "core", "capabilities", "operations", 0, "mode"), "multi"),
            (("evidence", "core", "effect_requests", 0, "effect"), "Z" * 64),
            (("evidence", "core", "effect_requests", 0, "ordinal"), 2),
            (("evidence", "host_observations", "responses", 0, "ordinal"), 2),
            (("evidence", "host_observations", "responses", 0, "category"), "maybe"),
            (("evidence", "host_observations", "responses", 0, "completion"), "done"),
            (("evidence", "core", "terminal"), "finished"),
        ):
            with self.subTest(path=path), self.assertRaises(MalformedFrame):
                parse_core_frame(mutated(path, value), limits=HARD_LIMITS)
        refused = KIT.transcript("refused-authority")["core_output"][2]
        big = copy.deepcopy(refused)
        big["result"]["diagnostics"][0]["cause"] = "x" * 70000
        with self.assertRaises(LimitExceeded):
            parse_core_frame(big, limits=HARD_LIMITS)
        parse_core_frame(big, limits={**HARD_LIMITS, "max_diagnostic_bytes": 80000})
        with_contrast = copy.deepcopy(refused)
        with_contrast["result"]["diagnostics"][0]["contrast"] = {"mistaken": "a", "intended": "b"}
        parsed = parse_core_frame(with_contrast, limits=HARD_LIMITS)
        assert isinstance(parsed, Outcome)
        self.assertEqual(parsed.diagnostics[0].contrast, {"mistaken": "a", "intended": "b"})


if __name__ == "__main__":
    unittest.main()
