"""The host state machine, driven by the recorded transcripts (no Core needed)."""

import unittest

from jacquard_host.kit import equal_semantic, load_kit
from jacquard_host.protocol.envelopes import EffectRequest, Fatal, Interface, OperationEntry, Outcome, ShutdownAck
from jacquard_host.protocol.errors import LimitExceeded, OrderViolation
from jacquard_host.protocol.limits import HARD_LIMITS
from jacquard_host.protocol.values import Hash, Nominal, TupleType
from jacquard_host.session import HostSession, State

KIT = load_kit()
IDS = {name: Hash(value) for name, value in KIT.manifest["identities"].items()}
TEXT = Nominal(IDS["text"])
ECHO = Interface((TEXT,), TEXT, (IDS["kit-world"],))
SEND = (OperationEntry(IDS["kit-world"], IDS["send"]),)


def core(name: str, index: int) -> dict:
    return KIT.transcript(name)["core_output"][index]


def host(name: str, step: int) -> dict:
    return KIT.transcript(name)["host_input"][step]["frame"]


class PositiveTranscriptTest(unittest.TestCase):
    def test_pure_success(self) -> None:
        session = HostSession()
        session.receive(core("pure-success", 0))
        self.assertEqual(session.select(), host("pure-success", 1))
        self.assertEqual(session.invoke(IDS["kit-ready"], Interface((), TupleType()), ()), host("pure-success", 2))
        outcome = session.receive(core("pure-success", 1))
        assert isinstance(outcome, Outcome)
        self.assertEqual((outcome.ok, outcome.value, outcome.terminal), (True, (), "ok"))
        self.assertIs(session.state, State.CLOSED)

    def test_one_once_effect_success(self) -> None:
        session = HostSession()
        session.receive(core("one-once-effect-success", 0))
        session.select()
        self.assertEqual(session.invoke(IDS["kit-echo"], ECHO, ("ping",), SEND), host("one-once-effect-success", 2))
        request = session.receive(core("one-once-effect-success", 1))
        assert isinstance(request, EffectRequest)
        self.assertIs(session.pending, request)
        self.assertEqual(session.respond_ok("pong"), host("one-once-effect-success", 4))
        self.assertIsNone(session.pending)
        outcome = session.receive(core("one-once-effect-success", 2))
        assert isinstance(outcome, Outcome)
        self.assertEqual(outcome.value, "pong")
        self.assertEqual(len(session.requests), 1)

    def test_refused_authority_and_timeout_cancel(self) -> None:
        session = HostSession()
        session.receive(core("refused-authority", 0))
        session.select()
        session.invoke(IDS["kit-echo"], ECHO, ("ping",), SEND)
        session.receive(core("refused-authority", 1))
        self.assertEqual(session.respond_failure("refused_authority", "not_started", "policy denied"), host("refused-authority", 4))
        outcome = session.receive(core("refused-authority", 2))
        assert isinstance(outcome, Outcome)
        self.assertEqual((outcome.terminal, outcome.diagnostics[0].code), ("host_failure", "E1607"))

        session = HostSession()
        session.receive(core("timeout-with-unknown-completion", 0))
        session.select()
        session.invoke(IDS["kit-echo"], ECHO, ("ping",), SEND)
        session.receive(core("timeout-with-unknown-completion", 1))
        self.assertEqual(session.cancel("timeout", "unknown"), host("timeout-with-unknown-completion", 4))
        outcome = session.receive(core("timeout-with-unknown-completion", 2))
        assert isinstance(outcome, Outcome)
        self.assertEqual((outcome.terminal, outcome.diagnostics[0].code), ("completion_unknown", "E1612"))

    def test_selected_shutdown(self) -> None:
        session = HostSession()
        session.receive(core("selected-shutdown", 0))
        session.select()
        self.assertEqual(session.shutdown(), host("selected-shutdown", 2))
        self.assertIsInstance(session.receive(core("selected-shutdown", 3 - 3 + 1)), ShutdownAck)
        self.assertIs(session.state, State.CLOSED)

    def test_fatal_closes_the_session(self) -> None:
        session = HostSession()
        session.receive(core("unknown-version", 0))
        session.select()
        fatal = session.receive(core("unknown-version", 1))
        assert isinstance(fatal, Fatal)
        self.assertEqual(fatal.diagnostics[0].code, "E1600")
        self.assertIs(session.state, State.CLOSED)


class StateMachineTest(unittest.TestCase):
    def running_session(self) -> HostSession:
        session = HostSession()
        session.receive(core("one-once-effect-success", 0))
        session.select()
        session.invoke(IDS["kit-echo"], ECHO, ("ping",), SEND)
        return session

    def test_nothing_is_sent_after_a_terminal_frame(self) -> None:
        session = self.running_session()
        session.receive(core("one-once-effect-success", 1))
        session.respond_ok("pong")
        session.receive(core("one-once-effect-success", 2))
        for action in (lambda: session.respond_ok("late"), lambda: session.cancel("cancelled", "unknown"), lambda: session.invoke(IDS["kit-ready"], Interface((), TupleType()), ()), session.shutdown, session.select):
            with self.subTest(action=action), self.assertRaises(OrderViolation) as caught:
                action()
            self.assertEqual(caught.exception.code, "E1608")
        with self.assertRaises(OrderViolation):
            session.receive(core("one-once-effect-success", 2))

    def test_responses_need_an_outstanding_request(self) -> None:
        session = self.running_session()
        with self.assertRaises(OrderViolation):
            session.respond_ok("early")
        with self.assertRaises(OrderViolation):
            session.respond_failure("outside_failure", "failed", "")
        with self.assertRaises(OrderViolation):
            session.cancel("timeout", "unknown")
        session.receive(core("one-once-effect-success", 1))
        with self.assertRaises(OrderViolation):
            session.invoke(IDS["kit-ready"], Interface((), TupleType()), ())
        with self.assertRaises(OrderViolation):
            session.receive(core("one-once-effect-success", 1))  # a second request while waiting

    def test_request_ordinals_must_be_consecutive(self) -> None:
        session = self.running_session()
        stale = dict(core("one-once-effect-success", 1), request_id="0000000000000002")
        with self.assertRaises(OrderViolation):
            session.receive(stale)
        session.receive(core("one-once-effect-success", 1))
        session.respond_ok("pong")
        with self.assertRaises(OrderViolation):
            session.receive(core("one-once-effect-success", 1))  # ordinal 1 again
        session.receive(dict(core("one-once-effect-success", 1), request_id="0000000000000002"))
        self.assertEqual(len(session.requests), 2)

    def test_negotiation_order(self) -> None:
        session = HostSession()
        with self.assertRaises(OrderViolation):
            session.select()
        with self.assertRaises(OrderViolation):
            session.receive(core("pure-success", 1))  # outcome before hello
        session.receive(core("pure-success", 0))
        with self.assertRaises(OrderViolation):
            session.invoke(IDS["kit-ready"], Interface((), TupleType()), ())
        with self.assertRaises(OrderViolation):
            session.receive(core("pure-success", 0))  # a second hello
        session.select({**HARD_LIMITS, "max_effect_requests": 1})
        with self.assertRaises(OrderViolation):
            session.receive(core("selected-shutdown", 1))  # ack without shutdown
        session.invoke(IDS["kit-echo"], ECHO, ("ping",), SEND)
        session.receive(core("one-once-effect-success", 1))
        session.respond_ok("pong")
        with self.assertRaises(LimitExceeded):
            session.receive(dict(core("one-once-effect-success", 1), request_id="0000000000000002"))

    def test_failure_or_cancel_closes_the_slot_for_good(self) -> None:
        for close in (lambda s: s.respond_failure("outside_failure", "failed", "boom"), lambda s: s.cancel("timeout", "unknown")):
            session = self.running_session()
            session.receive(core("one-once-effect-success", 1))
            close(session)
            self.assertIs(session.state, State.FINISHING)
            with self.assertRaises(OrderViolation):
                session.receive(dict(core("one-once-effect-success", 1), request_id="0000000000000002"))
            with self.assertRaises(OrderViolation):
                session.respond_ok("late")
        session = self.running_session()
        session.receive(core("refused-authority", 1))
        session.respond_failure("refused_authority", "not_started", "policy denied")
        outcome = session.receive(core("refused-authority", 2))
        assert isinstance(outcome, Outcome)
        self.assertIs(session.state, State.CLOSED)

    def test_hello_without_v0_is_refused(self) -> None:
        session = HostSession()
        with self.assertRaises(OrderViolation) as caught:
            session.receive(dict(core("pure-success", 0), versions=["jacquard-host-v1"]))
        self.assertEqual(caught.exception.code, "E1600")


if __name__ == "__main__":
    unittest.main()
