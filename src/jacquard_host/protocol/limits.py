"""Hard and selected limits (spec section 6)."""

from __future__ import annotations

from typing import Mapping

from jacquard_host.protocol.errors import LimitExceeded, MalformedFrame

HARD_LIMITS: dict[str, int] = {
    "max_arguments": 64,
    "max_collection_items": 1024,
    "max_diagnostic_bytes": 65536,
    "max_diagnostics": 32,
    "max_effect_requests": 1024,
    "max_effects": 64,
    "max_frame_bytes": 1_048_576,
    "max_host_message_bytes": 4096,
    "max_json_depth": 64,
    "max_operations": 256,
    "max_stderr_bytes": 65536,
    "max_text_bytes": 262_144,
    "max_value_nodes": 4096,
}

LIMIT_FIELDS: tuple[str, ...] = tuple(sorted(HARD_LIMITS))


def validate_limits(candidate: object, *, ceiling: Mapping[str, int] = HARD_LIMITS) -> dict[str, int]:
    """Return ``candidate`` as an exact limits object or fail closed.

    Every field must be present, no other field may appear, and each value
    must be a positive JSON integer no greater than the ceiling. A missing or
    foreign field is a shape error (E1601); a nonpositive or excessive value is
    a limit error (E1602).
    """
    if not isinstance(candidate, dict):
        raise MalformedFrame("limits must be a JSON object")
    if set(candidate) != set(LIMIT_FIELDS):
        raise MalformedFrame("limits must carry exactly the thirteen limit fields")
    selected: dict[str, int] = {}
    for field in LIMIT_FIELDS:
        value = candidate[field]
        if isinstance(value, bool) or not isinstance(value, int):
            raise MalformedFrame(f"limit {field} must be a JSON integer")
        if value < 1:
            raise LimitExceeded(f"limit {field} must be positive")
        if value > ceiling[field]:
            raise LimitExceeded(f"limit {field} exceeds the advertised maximum")
        selected[field] = value
    return selected
