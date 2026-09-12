"""Boundary types and values (spec section 5).

Python representations:

- ``Int``   -> :class:`int` (never :class:`bool`), 63-bit range
- ``Real``  -> :class:`float`, carried as raw binary64 bits (NaN, infinities,
  and negative zero survive)
- ``Text``  -> :class:`str` with no surrogates
- ``Hash``  -> :class:`Hash`
- tuples    -> :class:`tuple`
- saturated constructors -> :class:`Constructor`

Types are :class:`Nominal` (an exact declared identity plus type arguments)
or :class:`TupleType`. Every encoder and decoder counts one node per
descriptor object against a :class:`Budget`, checks collection sizes and text
bytes against the active limits, and fails closed with the spec's code:
E1601 for shape, E1602 for limits, E1604 for unsupported kinds.
"""

from __future__ import annotations

import struct
from dataclasses import dataclass
from typing import Any, Mapping, Union

from jacquard_host.protocol.errors import LimitExceeded, MalformedFrame, UnsupportedBoundary

INT_MIN = -(1 << 62)
INT_MAX = (1 << 62) - 1


def _is_hex64(text: object) -> bool:
    return isinstance(text, str) and len(text) == 64 and all(c in "0123456789abcdef" for c in text)


@dataclass(frozen=True, order=True)
class Hash:
    """An exact ``HASH_V0`` identity: 64 lowercase hexadecimal digits."""

    hex: str

    def __post_init__(self) -> None:
        if not _is_hex64(self.hex):
            raise ValueError("a hash is exactly 64 lowercase hexadecimal digits")

    def __str__(self) -> str:
        return self.hex


@dataclass(frozen=True)
class Constructor:
    """A saturated constructor value: exact identity plus positional fields."""

    identity: Hash
    arguments: tuple[Any, ...] = ()


@dataclass(frozen=True)
class Nominal:
    identity: Hash
    arguments: tuple["Type", ...] = ()


@dataclass(frozen=True)
class TupleType:
    items: tuple["Type", ...] = ()


Type = Union[Nominal, TupleType]
UNIT = TupleType()


class Budget:
    """Shared per-frame node accounting (spec section 6: counting is exact)."""

    def __init__(self, limits: Mapping[str, int]) -> None:
        self.limits = limits
        self.nodes = 0

    def consume(self) -> None:
        if self.nodes >= self.limits["max_value_nodes"]:
            raise LimitExceeded("the frame exceeds max_value_nodes across its type and value descriptors")
        self.nodes += 1

    def check_count(self, count: int, *, arguments: bool, context: str) -> None:
        if count > self.limits["max_collection_items"]:
            raise LimitExceeded(f"the {context} exceeds max_collection_items")
        if arguments and count > self.limits["max_arguments"]:
            raise LimitExceeded(f"the {context} exceeds max_arguments")

    def check_text(self, text: str) -> None:
        try:
            encoded = text.encode("utf-8")
        except UnicodeEncodeError:
            raise MalformedFrame("a boundary Text value is not Unicode scalar UTF-8") from None
        if len(encoded) > self.limits["max_text_bytes"]:
            raise LimitExceeded("a boundary Text value exceeds max_text_bytes")


def _exact_fields(value: Any, expected: tuple[str, ...], what: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise MalformedFrame(f"a boundary {what} must be one exact JSON object")
    if set(value) != set(expected):
        raise MalformedFrame(f"a boundary {what} must carry exactly the fields {', '.join(expected)}")
    return value


def _kind(value: Any, what: str) -> str:
    if not isinstance(value, dict):
        raise MalformedFrame(f"a boundary {what} must be one exact JSON object")
    kind = value.get("kind")
    if not isinstance(kind, str):
        raise MalformedFrame(f"a boundary {what} kind must be one string")
    return kind


def _hash(value: Any, context: str) -> Hash:
    if not isinstance(value, str):
        raise MalformedFrame(f"the {context} must be one HASH_V0 string")
    if not _is_hex64(value):
        raise MalformedFrame(f"the {context} must be exactly 64 lowercase hexadecimal digits")
    return Hash(value)


def _list(value: Any, budget: Budget, *, arguments: bool, context: str) -> list[Any]:
    if not isinstance(value, list):
        raise MalformedFrame(f"the {context} must be one JSON array")
    budget.check_count(len(value), arguments=arguments, context=context)
    return value


# --- types ---------------------------------------------------------------


def decode_type(json: Any, budget: Budget) -> Type:
    kind = _kind(json, "type")
    if kind == "nominal":
        fields = _exact_fields(json, ("arguments", "identity", "kind"), "type")
        budget.consume()
        identity = _hash(fields["identity"], "nominal type identity")
        arguments = _list(fields["arguments"], budget, arguments=False, context="nominal type arguments")
        return Nominal(identity, tuple(decode_type(item, budget) for item in arguments))
    if kind == "tuple":
        fields = _exact_fields(json, ("items", "kind"), "type")
        budget.consume()
        items = _list(fields["items"], budget, arguments=False, context="tuple type items")
        return TupleType(tuple(decode_type(item, budget) for item in items))
    raise UnsupportedBoundary(f"boundary type kind {kind!r} is not supported in jacquard-host-v0")


def encode_type(ty: Type, budget: Budget) -> dict[str, Any]:
    if isinstance(ty, Nominal):
        budget.consume()
        budget.check_count(len(ty.arguments), arguments=False, context="nominal type arguments")
        return {
            "arguments": [encode_type(item, budget) for item in ty.arguments],
            "identity": ty.identity.hex,
            "kind": "nominal",
        }
    if isinstance(ty, TupleType):
        budget.consume()
        budget.check_count(len(ty.items), arguments=False, context="tuple type items")
        return {"items": [encode_type(item, budget) for item in ty.items], "kind": "tuple"}
    raise UnsupportedBoundary(f"{type(ty).__name__} is not a boundary type")


# --- values --------------------------------------------------------------


def _canonical_int(text: Any) -> int:
    if not isinstance(text, str):
        raise MalformedFrame("a boundary Int value must be one canonical decimal string")
    digits = text[1:] if text.startswith("-") else text
    canonical = digits.isdigit() and digits.isascii() and (digits == "0" or not digits.startswith("0")) and text != "-0"
    if not canonical:
        raise MalformedFrame("a boundary Int must use canonical decimal text in the released OCaml 63-bit range")
    value = int(text)
    if value < INT_MIN or value > INT_MAX:
        raise MalformedFrame("a boundary Int must use canonical decimal text in the released OCaml 63-bit range")
    return value


def _real_of_bits(text: Any) -> float:
    if not isinstance(text, str):
        raise MalformedFrame("a boundary Real bits field must be one string")
    if len(text) != 16 or not all(c in "0123456789abcdef" for c in text):
        raise MalformedFrame("a boundary Real must use exactly 16 lowercase hexadecimal digits")
    return struct.unpack(">d", bytes.fromhex(text))[0]


def bits_of_real(value: float) -> str:
    return struct.pack(">d", value).hex()


def decode_value(json: Any, budget: Budget) -> Any:
    kind = _kind(json, "value")
    if kind in ("int", "text", "hash"):
        fields = _exact_fields(json, ("kind", "value"), "value")
        budget.consume()
        if kind == "int":
            return _canonical_int(fields["value"])
        if kind == "text":
            text = fields["value"]
            if not isinstance(text, str):
                raise MalformedFrame("a boundary Text value must be one string")
            budget.check_text(text)
            return text
        return _hash(fields["value"], "boundary Hash value")
    if kind == "real":
        fields = _exact_fields(json, ("bits", "kind"), "value")
        budget.consume()
        return _real_of_bits(fields["bits"])
    if kind == "tuple":
        fields = _exact_fields(json, ("items", "kind"), "value")
        budget.consume()
        items = _list(fields["items"], budget, arguments=False, context="tuple value items")
        return tuple(decode_value(item, budget) for item in items)
    if kind == "constructor":
        fields = _exact_fields(json, ("arguments", "identity", "kind"), "value")
        budget.consume()
        identity = _hash(fields["identity"], "constructor identity")
        arguments = _list(fields["arguments"], budget, arguments=True, context="constructor arguments")
        return Constructor(identity, tuple(decode_value(item, budget) for item in arguments))
    raise UnsupportedBoundary(f"boundary value kind {kind!r} is not supported in jacquard-host-v0")


def encode_value(value: Any, budget: Budget) -> dict[str, Any]:
    """Encode a Python value; anything without a v0 encoding fails with E1604."""
    if isinstance(value, bool):
        raise UnsupportedBoundary("Python bool has no v0 encoding; use the prelude's true/false constructors")
    if isinstance(value, int):
        budget.consume()
        if value < INT_MIN or value > INT_MAX:
            raise UnsupportedBoundary("an Int is outside the released OCaml 63-bit range")
        return {"kind": "int", "value": str(value)}
    if isinstance(value, float):
        budget.consume()
        return {"bits": bits_of_real(value), "kind": "real"}
    if isinstance(value, str):
        budget.consume()
        budget.check_text(value)
        return {"kind": "text", "value": value}
    if isinstance(value, Hash):
        budget.consume()
        return {"kind": "hash", "value": value.hex}
    if isinstance(value, tuple):
        budget.consume()
        budget.check_count(len(value), arguments=False, context="tuple value items")
        return {"items": [encode_value(item, budget) for item in value], "kind": "tuple"}
    if isinstance(value, Constructor):
        budget.consume()
        budget.check_count(len(value.arguments), arguments=True, context="constructor arguments")
        return {
            "arguments": [encode_value(item, budget) for item in value.arguments],
            "identity": value.identity.hex,
            "kind": "constructor",
        }
    raise UnsupportedBoundary(f"{type(value).__name__} values have no jacquard-host-v0 encoding")
