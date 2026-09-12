import math
import unittest

from jacquard_host.protocol.errors import LimitExceeded, MalformedFrame, UnsupportedBoundary
from jacquard_host.protocol.limits import HARD_LIMITS
from jacquard_host.protocol.values import (
    INT_MAX,
    INT_MIN,
    Budget,
    Constructor,
    Hash,
    Nominal,
    TupleType,
    bits_of_real,
    decode_type,
    decode_value,
    encode_type,
    encode_value,
)

H1 = Hash("1" * 64)
H2 = Hash("2" * 64)


def budget(**overrides):
    return Budget({**HARD_LIMITS, **overrides})


class HashTest(unittest.TestCase):
    def test_only_lowercase_hex_64(self) -> None:
        Hash("a" * 64)
        for bad in ("A" * 64, "a" * 63, "g" * 64, ""):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                Hash(bad)


class ValueRoundTripTest(unittest.TestCase):
    def test_ints_are_canonical_decimal_in_the_63_bit_range(self) -> None:
        for value in (0, 1, -1, INT_MAX, INT_MIN, 12345678901234567):
            with self.subTest(value=value):
                encoded = encode_value(value, budget())
                self.assertEqual(encoded, {"kind": "int", "value": str(value)})
                self.assertEqual(decode_value(encoded, budget()), value)
        for spelling in ("+1", "01", "-0", "1.0", "1e3", " 1", "１", str(INT_MAX + 1), str(INT_MIN - 1), ""):
            with self.subTest(spelling=spelling), self.assertRaises(MalformedFrame):
                decode_value({"kind": "int", "value": spelling}, budget())
        with self.assertRaises(MalformedFrame):
            decode_value({"kind": "int", "value": 1}, budget())
        for out_of_range in (INT_MAX + 1, INT_MIN - 1):
            with self.assertRaises(UnsupportedBoundary):
                encode_value(out_of_range, budget())

    def test_bool_is_not_an_int(self) -> None:
        with self.assertRaises(UnsupportedBoundary):
            encode_value(True, budget())

    def test_reals_carry_raw_bits(self) -> None:
        for value in (0.0, -0.0, 1.5, math.inf, -math.inf, 1e308, 5e-324):
            with self.subTest(value=value):
                encoded = encode_value(value, budget())
                self.assertEqual(encoded["kind"], "real")
                self.assertRegex(encoded["bits"], r"^[0-9a-f]{16}$")
                decoded = decode_value(encoded, budget())
                self.assertEqual(bits_of_real(decoded), encoded["bits"])
        self.assertEqual(encode_value(-0.0, budget())["bits"], "8000000000000000")
        nan = decode_value({"bits": "7ff8000000000001", "kind": "real"}, budget())
        self.assertTrue(math.isnan(nan))
        self.assertEqual(bits_of_real(nan), "7ff8000000000001")
        for bad in ("7FF8000000000001", "0" * 15, "0" * 17, 0):
            with self.subTest(bad=bad), self.assertRaises(MalformedFrame):
                decode_value({"bits": bad, "kind": "real"}, budget())

    def test_text_is_scalar_utf8_within_max_text_bytes(self) -> None:
        self.assertEqual(decode_value({"kind": "text", "value": "héllo"}, budget()), "héllo")
        with self.assertRaises(MalformedFrame):
            encode_value("\ud800", budget())
        with self.assertRaises(LimitExceeded):
            encode_value("é" * 3, budget(max_text_bytes=5))
        with self.assertRaises(LimitExceeded):
            decode_value({"kind": "text", "value": "é" * 3}, budget(max_text_bytes=5))
        with self.assertRaises(MalformedFrame):
            decode_value({"kind": "text", "value": 3}, budget())

    def test_hash_tuple_and_constructor(self) -> None:
        value = (1, "x", H1, Constructor(H2, (2.5, ())))
        encoded = encode_value(value, budget())
        self.assertEqual(
            encoded,
            {
                "items": [
                    {"kind": "int", "value": "1"},
                    {"kind": "text", "value": "x"},
                    {"kind": "hash", "value": H1.hex},
                    {
                        "arguments": [{"bits": "4004000000000000", "kind": "real"}, {"items": [], "kind": "tuple"}],
                        "identity": H2.hex,
                        "kind": "constructor",
                    },
                ],
                "kind": "tuple",
            },
        )
        self.assertEqual(decode_value(encoded, budget()), value)

    def test_exact_field_sets_and_unknown_kinds(self) -> None:
        for bad in (
            {"kind": "int"},
            {"kind": "int", "value": "1", "extra": 1},
            {"kind": "text", "value": "x", "bits": "0"},
            {"kind": "constructor", "identity": H1.hex},
            {"kind": "tuple", "items": {}},
            {"value": "1"},
            "1",
        ):
            with self.subTest(bad=bad), self.assertRaises(MalformedFrame):
                decode_value(bad, budget())
        for kind in ("secret", "code", "closure", "list"):
            with self.subTest(kind=kind), self.assertRaises(UnsupportedBoundary):
                decode_value({"kind": kind, "value": "x"}, budget())
        with self.assertRaises(UnsupportedBoundary):
            encode_value([1, 2], budget())
        with self.assertRaises(UnsupportedBoundary):
            encode_value(object(), budget())

    def test_node_and_collection_limits_count_exactly(self) -> None:
        b = budget()
        encode_value((1, (2, 3)), b)
        self.assertEqual(b.nodes, 5)
        with self.assertRaises(LimitExceeded):
            encode_value((1, (2, 3)), budget(max_value_nodes=4))
        with self.assertRaises(LimitExceeded):
            decode_value({"items": [{"kind": "int", "value": "1"}] * 3, "kind": "tuple"}, budget(max_collection_items=2))
        with self.assertRaises(LimitExceeded):
            encode_value(Constructor(H1, (1, 2, 3)), budget(max_arguments=2))
        encode_value((1, 2, 3), budget(max_arguments=2))  # tuples are not argument lists


class TypeRoundTripTest(unittest.TestCase):
    def test_nominal_and_tuple(self) -> None:
        ty = TupleType((Nominal(H1), Nominal(H2, (Nominal(H1),)), TupleType()))
        encoded = encode_type(ty, budget())
        self.assertEqual(
            encoded,
            {
                "items": [
                    {"arguments": [], "identity": H1.hex, "kind": "nominal"},
                    {"arguments": [{"arguments": [], "identity": H1.hex, "kind": "nominal"}], "identity": H2.hex, "kind": "nominal"},
                    {"items": [], "kind": "tuple"},
                ],
                "kind": "tuple",
            },
        )
        self.assertEqual(decode_type(encoded, budget()), ty)

    def test_unsupported_type_kinds_and_shapes(self) -> None:
        with self.assertRaises(UnsupportedBoundary):
            decode_type({"kind": "arrow", "parameters": [], "result": {}}, budget())
        with self.assertRaises(MalformedFrame):
            decode_type({"kind": "nominal", "identity": "X" * 64, "arguments": []}, budget())
        with self.assertRaises(MalformedFrame):
            decode_type({"kind": "nominal", "identity": H1.hex}, budget())


if __name__ == "__main__":
    unittest.main()
