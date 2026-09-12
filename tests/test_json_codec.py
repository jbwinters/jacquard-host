import unittest

from jacquard_host.protocol.errors import LimitExceeded, MalformedFrame
from jacquard_host.protocol.json_codec import canonical_text, decode_object, encode_object


class DecodeObjectTest(unittest.TestCase):
    def test_accepts_one_object_with_any_key_order_and_whitespace(self) -> None:
        self.assertEqual(decode_object(b' {"b": 1 ,\n"a": [true, null]} ', max_depth=64), {"b": 1, "a": [True, None]})

    def test_rejects_invalid_utf8(self) -> None:
        with self.assertRaises(MalformedFrame):
            decode_object(b"{\xff}", max_depth=64)

    def test_rejects_encoded_surrogate_bytes(self) -> None:
        with self.assertRaises(MalformedFrame):
            decode_object(b'{"k":"\xed\xa0\x80"}', max_depth=64)

    def test_rejects_lone_surrogate_escape(self) -> None:
        with self.assertRaises(MalformedFrame):
            decode_object(b'{"k":"\\ud800"}', max_depth=64)
        with self.assertRaises(MalformedFrame):
            decode_object(b'{"\\udc00":1}', max_depth=64)

    def test_accepts_paired_surrogate_escape(self) -> None:
        self.assertEqual(decode_object(b'{"k":"\\ud83d\\ude00"}', max_depth=64), {"k": "\U0001F600"})

    def test_rejects_duplicate_keys_at_any_depth(self) -> None:
        with self.assertRaises(MalformedFrame):
            decode_object(b'{"kind":"host_select","kind":"invoke"}', max_depth=64)
        with self.assertRaises(MalformedFrame):
            decode_object(b'{"a":{"x":1,"x":2}}', max_depth=64)

    def test_rejects_non_object_roots_and_trailing_data(self) -> None:
        for payload in (b"[]", b"1", b'"x"', b"null", b"{} {}", b"{}x", b""):
            with self.subTest(payload=payload), self.assertRaises(MalformedFrame):
                decode_object(payload, max_depth=64)

    def test_rejects_nan_and_infinity_literals(self) -> None:
        for payload in (b'{"a":NaN}', b'{"a":Infinity}', b'{"a":-Infinity}'):
            with self.subTest(payload=payload), self.assertRaises(MalformedFrame):
                decode_object(payload, max_depth=64)

    def test_depth_counts_containers_only(self) -> None:
        self.assertEqual(decode_object(b'{"a":1}', max_depth=1), {"a": 1})
        with self.assertRaises(LimitExceeded):
            decode_object(b'{"a":[]}', max_depth=1)
        self.assertEqual(decode_object(b'{"a":[]}', max_depth=2), {"a": []})
        deep = b'{"a":' * 64 + b"1" + b"}" * 64
        self.assertEqual(len(decode_object(deep, max_depth=64)), 1)
        with self.assertRaises(LimitExceeded):
            decode_object(b'{"a":' * 65 + b"1" + b"}" * 65, max_depth=64)

    def test_absurd_nesting_is_a_limit_not_a_crash(self) -> None:
        with self.assertRaises(LimitExceeded):
            decode_object(b'{"a":' + b"[" * 100000 + b"]" * 100000 + b"}", max_depth=64)

    def test_numbers_follow_the_core_rule(self) -> None:
        self.assertEqual(decode_object(b'{"a":4611686018427387903,"b":-4611686018427387904}', max_depth=64), {"a": 2**62 - 1, "b": -(2**62)})
        for payload in (b'{"a":1.5}', b'{"a":1e5}', b'{"a":4611686018427387904}', b'{"a":-4611686018427387905}', b'{"a":' + b"9" * 4301 + b"}", b'{"a":[' + b"9" * 5000 + b"]}"):
            with self.subTest(payload=payload[:40]), self.assertRaises(MalformedFrame):
                decode_object(payload, max_depth=64)


class EncodeObjectTest(unittest.TestCase):
    def test_canonical_bytes_are_sorted_compact_utf8(self) -> None:
        self.assertEqual(encode_object({"b": "é", "a": [1, {"z": None, "y": True}]}), '{"a":[1,{"y":true,"z":null}],"b":"é"}'.encode("utf-8"))

    def test_only_objects_cross_the_carrier(self) -> None:
        with self.assertRaises(TypeError):
            encode_object([])  # type: ignore[arg-type]

    def test_canonical_text_ignores_key_order(self) -> None:
        self.assertEqual(canonical_text({"b": 1, "a": 2}), canonical_text({"a": 2, "b": 1}))


if __name__ == "__main__":
    unittest.main()
