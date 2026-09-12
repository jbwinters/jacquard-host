"""Strict JSON payloads for ``stdio-u32-json-v0`` (spec section 3).

A payload is accepted only when it is valid UTF-8 with no encoded surrogate,
one JSON object, free of duplicate keys at every depth, free of ``NaN`` and
``Infinity`` literals, free of lone surrogate escapes, and no deeper than the
active ``max_json_depth``. Anything else fails closed: shape problems are
E1601 and the depth limit is E1602. Key order and insignificant whitespace
carry no meaning, so encoding is canonical (sorted keys, no spaces) purely
for determinism; nothing hashes these bytes as evidence.
"""

from __future__ import annotations

import json
from typing import Any

from jacquard_host.protocol.errors import LimitExceeded, MalformedFrame


def _reject_duplicates(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise MalformedFrame("a JSON object contains a duplicate field name")
        result[key] = value
    return result


def _reject_constant(name: str) -> Any:
    raise MalformedFrame(f"the JSON literal {name} is not allowed")


def _check(value: Any, depth: int, max_depth: int) -> None:
    if isinstance(value, dict):
        if depth > max_depth:
            raise LimitExceeded("JSON nesting exceeds max_json_depth")
        for key, item in value.items():
            _check_text(key)
            _check(item, depth + 1, max_depth)
    elif isinstance(value, list):
        if depth > max_depth:
            raise LimitExceeded("JSON nesting exceeds max_json_depth")
        for item in value:
            _check(item, depth + 1, max_depth)
    elif isinstance(value, str):
        _check_text(value)
    elif isinstance(value, float):
        # json.loads only produces floats for fraction/exponent spellings; the
        # protocol has no such field, but the value is at least finite here.
        if value != value or value in (float("inf"), float("-inf")):
            raise MalformedFrame("a JSON number is not finite")


def _check_text(text: str) -> None:
    try:
        text.encode("utf-8")
    except UnicodeEncodeError:
        raise MalformedFrame("a JSON string contains a lone surrogate") from None


def decode_object(payload: bytes, *, max_depth: int) -> dict[str, Any]:
    """Decode one payload into a JSON object or raise a fail-closed error."""
    try:
        text = payload.decode("utf-8")
    except UnicodeDecodeError:
        raise MalformedFrame("the payload is not valid UTF-8") from None
    try:
        value = json.loads(text, object_pairs_hook=_reject_duplicates, parse_constant=_reject_constant)
    except json.JSONDecodeError:
        raise MalformedFrame("the payload is not one JSON value") from None
    except RecursionError:
        raise LimitExceeded("JSON nesting exceeds max_json_depth") from None
    if not isinstance(value, dict):
        raise MalformedFrame("the payload is not a JSON object")
    _check(value, 1, max_depth)
    return value


def encode_object(value: dict[str, Any]) -> bytes:
    """Encode a JSON object canonically (sorted keys, compact, raw UTF-8)."""
    if not isinstance(value, dict):
        raise TypeError("only JSON objects cross the carrier")
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True, allow_nan=False).encode(
        "utf-8"
    )


def canonical_text(value: Any) -> str:
    """Canonical JSON text used only for semantic comparison of frames."""
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True, allow_nan=False)
