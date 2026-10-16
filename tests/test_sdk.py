"""SDK data and failures without a Core executable."""

from dataclasses import dataclass
from enum import Enum, StrEnum
import math
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

from jacquard_host import BindingError, InvalidArgument, InvocationError, PreparationError, Runtime, Some, Variant
from jacquard_host.adapter import InvocationResult
from jacquard_host.data import Data
from jacquard_host.errors import DeadlineExceeded, OperationFailed, WorkerLost
from jacquard_host.protocol.json_codec import validate_object
from jacquard_host.protocol.errors import LimitExceeded
from jacquard_host.protocol.values import Constructor, Hash, Nominal, INT_MAX, INT_MIN


NAMES = {name: f"{i:064x}" for i, name in enumerate(("int", "real", "text", "bool", "list", "option", "hash", "true", "false", "nil", "cons", "none", "some", "row", "red", "blue"), 1)}


@dataclass(frozen=True)
class Row:
    title: str
    count: int
    enabled: bool
    owner: str | None
    tags: list[str]


class Color(Enum):
    RED = "Red"
    BLUE = "Blue"


class StringColor(StrEnum):
    RED = "Red"


class DataTest(unittest.TestCase):
    def setUp(self):
        self.data = Data(NAMES, NAMES)

    def test_native_lists_bools_options_and_tuples_round_trip(self):
        data = self.data
        value = ([True, False, None, Some("owner"), Some(None)], (INT_MIN, INT_MAX, "héllo"))
        self.assertEqual(data.decode(data.encode(value)), value)
        expected = Nominal(Hash(NAMES["option"]), (Nominal(Hash(NAMES["text"])),))
        self.assertEqual(data.decode(data.encode("owner", expected), expected), "owner")
        self.assertIsNone(data.decode(data.encode(None, expected), expected))
        nested = Nominal(Hash(NAMES["option"]), (expected,))
        self.assertEqual(data.decode(data.encode(Some(None), nested), nested), Some(None))
        value = data.Red()
        self.assertEqual(data.decode(data.encode(value, annotation=Variant | str | None), annotation=Variant | str | None), value)

    def test_dataclass_and_enum_round_trip(self):
        self.data.record(Row)
        self.data.enum(Color)
        value = Row("row", 3, True, "ops", ["a", "b"])
        self.assertEqual(self.data.decode(self.data.encode(value)), value)
        for color in Color:
            self.assertIs(self.data.decode(self.data.encode(color)), color)

    def test_bad_nested_fields_report_the_path(self):
        self.data.record(Row)
        with self.assertRaisesRegex(InvalidArgument, r"purchase.count: expected int"):
            self.data.encode(Row("row", True, True, None, []), path="purchase")
        with self.assertRaisesRegex(InvalidArgument, r"purchase.tags\[1\]: expected str"):
            self.data.encode(Row("row", 1, True, None, ["a", 7]), path="purchase")
        with self.assertRaises(InvalidArgument):
            self.data.encode(INT_MAX + 1)

    def test_depth_and_cyclic_values_are_local_failures(self):
        self.data.encode(list(range(30)))
        with self.assertRaisesRegex(InvalidArgument, "smaller batches"):
            self.data.encode(list(range(31)))
        cycle = []
        cycle.append(cycle)
        with self.assertRaises(InvalidArgument):
            self.data.encode(cycle)

    def test_float_bits_and_hashes_are_preserved(self):
        negative_zero = self.data.decode(self.data.encode(-0.0))
        self.assertEqual(math.copysign(1.0, negative_zero), -1.0)
        self.assertTrue(math.isnan(self.data.decode(self.data.encode(float("nan")))))
        value = Hash("a" * 64)
        self.assertEqual(self.data.decode(self.data.encode(value)), value)

    def test_variants_and_raw_values_still_work(self):
        value = self.data.Red()
        self.assertEqual(self.data.decode(self.data.encode(value)), value)
        value = Constructor(Hash(NAMES["row"]), ("value",))
        self.assertEqual(self.data.encode(value), value)
        with self.assertRaises(InvalidArgument):
            self.data.encode({"not": "a typed record"})

    def test_string_enums_still_encode_as_constructors(self):
        self.data.enum(StringColor)
        wire = self.data.encode(StringColor.RED)
        self.assertIsInstance(wire, Constructor)
        self.assertIs(self.data.decode(wire), StringColor.RED)
        with self.assertRaisesRegex(InvalidArgument, "expected str"):
            self.data.encode(StringColor.RED, Nominal(Hash(NAMES["text"])))

    def test_text_validation_names_the_field(self):
        data = Data(NAMES, NAMES, max_text_bytes=3)
        with self.assertRaisesRegex(InvalidArgument, "title: Text exceeds"):
            data.encode("éé", path="title")
        with self.assertRaisesRegex(InvalidArgument, "title: Text cannot"):
            data.encode("\ud800", path="title")

    def test_invalid_mappings_are_rejected(self):
        with self.assertRaises(BindingError):
            self.data.record(object)
        self.data.record(Row)
        with self.assertRaises(BindingError):
            self.data.record(Row)
        with self.assertRaises(BindingError):
            self.data.enum(Row)


class FailureTest(unittest.TestCase):
    def test_every_failure_surface_has_a_code_message_and_exception(self):
        cases = [
            (InvocationResult("host_error", host_evidence={"code": "E1604", "detail": "bad argument"}), InvocationError),
            (InvocationResult("carrier_loss", host_evidence={"code": "E1611", "reason": "deadline"}), DeadlineExceeded),
            (InvocationResult("carrier_loss", host_evidence={"code": "E1611", "reason": "carrier_loss"}), WorkerLost),
            (InvocationResult("error", terminal="completion_unknown", host_evidence={"code": "E1612"}), OperationFailed),
        ]
        for result, exception in cases:
            with self.subTest(kind=result.kind, message=result.message):
                self.assertTrue(result.code)
                self.assertTrue(result.message)
                with self.assertRaises(exception) as caught:
                    result.unwrap()
                self.assertIs(caught.exception.result, result)
        self.assertEqual(InvocationResult("ok", value=42).unwrap(), 42)

    def test_host_object_depth_is_validated_before_sending(self):
        with self.assertRaises(LimitExceeded):
            validate_object({"a": {"b": []}}, max_depth=2)


class RuntimeConfigurationTest(unittest.TestCase):
    def test_partial_limits_and_invalid_configuration(self):
        runtime = Runtime(sys.executable, prelude="unused", limits={"max_text_bytes": 1024})
        self.assertEqual(runtime.limits["max_text_bytes"], 1024)
        self.assertEqual(runtime.limits["max_json_depth"], 64)
        with self.assertRaises(BindingError):
            Runtime(sys.executable, prelude="unused", deadline_seconds=float("nan"))

    def test_prelude_manifests_are_part_of_the_cache_fingerprint(self):
        with tempfile.TemporaryDirectory() as temporary:
            prelude = Path(temporary)
            (prelude / "01-prim.jqd").write_text("prelude")
            manifest = prelude / "operation-modes.manifest"
            manifest.write_text("original")
            runtime = Runtime(sys.executable, prelude=prelude)
            with patch("jacquard_host.sdk.subprocess.run", return_value=subprocess.CompletedProcess([], 0, "jacquard 0.2.0\n", "")):
                first = runtime._installation()
                manifest.write_text("changed")
                second = runtime._installation()
            self.assertEqual(first[:2], second[:2])
            self.assertNotEqual(first[2], second[2])

    def test_core_version_mismatch_has_an_actionable_sdk_error(self):
        runtime = Runtime(sys.executable, prelude="unused")
        with patch("jacquard_host.sdk.subprocess.run", return_value=subprocess.CompletedProcess([], 0, "jacquard 0.3.0\n", "")):
            with self.assertRaisesRegex(PreparationError, "pins Core 0.2.0; found jacquard 0.3.0"):
                runtime._installation()


if __name__ == "__main__":
    unittest.main()
