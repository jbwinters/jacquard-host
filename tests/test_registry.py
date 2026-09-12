import unittest

from jacquard_host.kit import load_kit
from jacquard_host.protocol.envelopes import EffectRequest, OperationEntry, parse_core_frame
from jacquard_host.protocol.limits import HARD_LIMITS
from jacquard_host.protocol.values import Hash
from jacquard_host.registry import HostFailure, OperationRegistry

KIT = load_kit()
IDS = {name: Hash(value) for name, value in KIT.manifest["identities"].items()}
REQUEST = parse_core_frame(KIT.transcript("one-once-effect-success")["core_output"][1], limits=HARD_LIMITS)
assert isinstance(REQUEST, EffectRequest)


class RegistryTest(unittest.TestCase):
    def test_dispatch_by_identity_calls_the_bound_function_with_decoded_arguments(self) -> None:
        registry = OperationRegistry()
        seen = []

        @registry.operation(IDS["kit-world"], IDS["send"])
        def send(body: str) -> str:
            seen.append(body)
            return body[::-1]

        response = registry.dispatch(REQUEST)
        self.assertTrue(response.ok)
        self.assertEqual((response.value, seen), ("gnip", ["ping"]))
        self.assertEqual(registry.entries(), [OperationEntry(IDS["kit-world"], IDS["send"])])
        self.assertEqual(registry.effects(), (IDS["kit-world"],))

    def test_unbound_operations_are_reported_as_unsupported_not_started(self) -> None:
        response = OperationRegistry().dispatch(REQUEST)
        self.assertFalse(response.ok)
        assert response.failure is not None
        self.assertEqual((response.failure.category, response.failure.completion), ("unsupported_operation", "not_started"))

    def test_host_failure_is_relayed_and_other_exceptions_become_unknown(self) -> None:
        registry = OperationRegistry()
        registry.bind(IDS["kit-world"], IDS["send"], lambda body: (_ for _ in ()).throw(HostFailure("refused_authority", "not_started", "policy denied")))
        failure = registry.dispatch(REQUEST).failure
        assert failure is not None
        self.assertEqual((failure.category, failure.completion, failure.message), ("refused_authority", "not_started", "policy denied"))

        registry = OperationRegistry()
        registry.bind(IDS["kit-world"], IDS["send"], lambda body: 1 / 0)
        failure = registry.dispatch(REQUEST).failure
        assert failure is not None
        self.assertEqual((failure.category, failure.completion), ("completion_unknown", "unknown"))
        self.assertEqual(failure.message, "handler raised ZeroDivisionError")

    def test_invalid_pairs_and_duplicate_bindings_are_refused(self) -> None:
        with self.assertRaises(ValueError):
            HostFailure("refused_authority", "unknown")
        registry = OperationRegistry()
        registry.bind(IDS["kit-world"], IDS["send"], lambda body: body)
        with self.assertRaises(ValueError):
            registry.bind(IDS["kit-world"].hex, IDS["send"].hex, lambda body: body)
        self.assertEqual(len(registry), 1)


if __name__ == "__main__":
    unittest.main()
