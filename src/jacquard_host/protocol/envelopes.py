"""Exact ``jacquard-host-v0`` envelopes (spec sections 7 to 11).

Host frames are built here; Core frames are parsed here with exact field
sets. Nothing is tolerated: an unknown, missing, or extra field, a wrong
protocol string, or a malformed identity fails closed with E1601 (or E1608
for an ID that names the wrong invocation).
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Mapping, Sequence

from jacquard_host.compat import CARRIER, PROTOCOL
from jacquard_host.protocol.errors import LimitExceeded, MalformedFrame, OrderViolation
from jacquard_host.protocol.limits import HARD_LIMITS, validate_limits
from jacquard_host.protocol.values import Budget, Hash, Type, decode_type, decode_value, encode_type, encode_value, parse_hash

INVOCATION_ID = "0000000000000000"
TARGET_KIND = "store-term-v0"
CORE_EVIDENCE_SCHEMA = "jacquard-host-core-evidence-v0"
HOST_OBSERVATIONS_SCHEMA = "jacquard-host-observations-v0"
DIAGNOSTIC_SCHEMA = "jacquard-diagnostic-v1"

FAILURE_PAIRS: frozenset[tuple[str, str]] = frozenset(
    {
        ("unsupported_operation", "not_started"),
        ("refused_authority", "not_started"),
        ("outside_failure", "failed"),
        ("completion_unknown", "unknown"),
    }
)
CANCEL_REASONS = frozenset({"cancelled", "timeout", "host_shutdown"})
COMPLETIONS = frozenset({"not_started", "failed", "unknown"})
OBSERVATION_CATEGORIES = frozenset({"ok"} | {category for category, _ in FAILURE_PAIRS} | CANCEL_REASONS)
OBSERVATION_COMPLETIONS = frozenset({"completed"} | COMPLETIONS)
TERMINALS = frozenset({"ok", "diagnostic", "cancelled", "host_failure", "completion_unknown"})


def request_id(ordinal: int) -> str:
    if ordinal < 1:
        raise ValueError("request ordinals start at 1")
    return f"{ordinal:016x}"


# --- host frames -----------------------------------------------------------


@dataclass(frozen=True)
class Interface:
    parameters: tuple[Type, ...]
    result: Type
    effects: tuple[Hash, ...] = ()

    def __post_init__(self) -> None:
        if list(self.effects) != sorted(set(self.effects)):
            raise ValueError("interface effects must be a strictly sorted set")


@dataclass(frozen=True)
class OperationEntry:
    effect: Hash
    operation: Hash


def host_select(limits: Mapping[str, int]) -> dict[str, Any]:
    return {"kind": "host_select", "limits": dict(validate_limits(dict(limits))), "protocol": PROTOCOL}


def shutdown() -> dict[str, Any]:
    return {"kind": "shutdown", "protocol": PROTOCOL}


def invoke(
    target: Hash,
    interface: Interface,
    arguments: Sequence[Any],
    operations: Sequence[OperationEntry],
    *,
    limits: Mapping[str, int],
) -> dict[str, Any]:
    """Build the exact invoke envelope, counting nodes against ``limits``."""
    if len(arguments) != len(interface.parameters):
        raise MalformedFrame("the argument count must equal the parameter count")
    budget = Budget(limits)
    budget.check_count(len(interface.parameters), arguments=True, context="parameters")
    budget.check_count(len(arguments), arguments=True, context="arguments")
    if len(interface.effects) > limits["max_effects"]:
        raise LimitExceeded("the interface exceeds max_effects")
    entries = sorted(set(operations), key=lambda entry: (entry.effect, entry.operation))
    if len(entries) != len(operations):
        raise MalformedFrame("operation entries must be unique")
    if len(entries) > limits["max_operations"]:
        raise LimitExceeded("the registry exceeds max_operations")
    for entry in entries:
        if entry.effect not in interface.effects:
            raise MalformedFrame("an operation entry names an effect outside the interface row")
    return {
        "arguments": [encode_value(value, budget) for value in arguments],
        "capabilities": {
            "effects": [effect.hex for effect in interface.effects],
            "operations": [{"effect": e.effect.hex, "mode": "once", "operation": e.operation.hex} for e in entries],
        },
        "interface": {
            "effects": [effect.hex for effect in interface.effects],
            "parameters": [encode_type(ty, budget) for ty in interface.parameters],
            "result": encode_type(interface.result, budget),
        },
        "invocation_id": INVOCATION_ID,
        "kind": "invoke",
        "protocol": PROTOCOL,
        "target": {"callable": target.hex, "kind": TARGET_KIND},
    }


def effect_ok(ordinal: int, value: Any, *, limits: Mapping[str, int]) -> dict[str, Any]:
    return {
        "invocation_id": INVOCATION_ID,
        "kind": "effect_ok",
        "protocol": PROTOCOL,
        "request_id": request_id(ordinal),
        "value": encode_value(value, Budget(limits)),
    }


def effect_failure(ordinal: int, category: str, completion: str, message: str, *, limits: Mapping[str, int]) -> dict[str, Any]:
    if (category, completion) not in FAILURE_PAIRS:
        raise MalformedFrame(f"({category}, {completion}) is not an allowed effect_failure pair")
    if not isinstance(message, str):
        raise MalformedFrame("a host message must be one string")
    try:
        encoded = message.encode("utf-8")
    except UnicodeEncodeError:
        raise MalformedFrame("a host message must be Unicode scalar text") from None
    if len(encoded) > limits["max_host_message_bytes"]:
        raise LimitExceeded("the host message exceeds max_host_message_bytes")
    return {
        "category": category,
        "completion": completion,
        "invocation_id": INVOCATION_ID,
        "kind": "effect_failure",
        "message": message,
        "protocol": PROTOCOL,
        "request_id": request_id(ordinal),
    }


def cancel(ordinal: int, reason: str, completion: str) -> dict[str, Any]:
    if reason not in CANCEL_REASONS:
        raise MalformedFrame(f"{reason!r} is not a cancel reason")
    if completion not in COMPLETIONS:
        raise MalformedFrame(f"{completion!r} is not a cancel completion")
    return {
        "completion": completion,
        "invocation_id": INVOCATION_ID,
        "kind": "cancel",
        "protocol": PROTOCOL,
        "reason": reason,
        "request_id": request_id(ordinal),
    }


# --- Core frames -------------------------------------------------------------


@dataclass(frozen=True)
class CoreHello:
    limits: dict[str, int]
    versions: tuple[str, ...]


@dataclass(frozen=True)
class EffectRequest:
    ordinal: int
    effect: Hash
    operation: Hash
    arguments: tuple[Any, ...]
    raw: dict[str, Any]


@dataclass(frozen=True)
class Diagnostic:
    code: str | None
    domain: str
    severity: str
    summary: str
    cause: str
    next_step: str
    span: Any
    contrast: dict[str, str] | None
    raw: dict[str, Any]


@dataclass(frozen=True)
class Outcome:
    ok: bool
    value: Any
    diagnostics: tuple[Diagnostic, ...]
    terminal: str
    evidence: dict[str, Any]
    raw: dict[str, Any]


@dataclass(frozen=True)
class Fatal:
    diagnostics: tuple[Diagnostic, ...]
    raw: dict[str, Any]


@dataclass(frozen=True)
class ShutdownAck:
    raw: dict[str, Any]


CoreFrame = CoreHello | EffectRequest | Outcome | Fatal | ShutdownAck


def _exact(frame: Any, fields: tuple[str, ...], what: str) -> dict[str, Any]:
    if not isinstance(frame, dict):
        raise MalformedFrame(f"{what} must be one JSON object")
    if set(frame) != set(fields):
        raise MalformedFrame(f"{what} must carry exactly the fields {', '.join(fields)}")
    return frame


def _string(value: Any, what: str) -> str:
    if not isinstance(value, str):
        raise MalformedFrame(f"{what} must be one string")
    return value


def _protocol(frame: dict[str, Any]) -> None:
    if frame.get("protocol") != PROTOCOL:
        raise MalformedFrame("the frame does not carry protocol jacquard-host-v0")


def _invocation(frame: dict[str, Any]) -> None:
    identity = frame.get("invocation_id")
    if not isinstance(identity, str):
        raise MalformedFrame("invocation_id must be one string")
    if identity != INVOCATION_ID:
        raise OrderViolation("the frame names an invocation other than the one live invocation")


def parse_core_hello(frame: Any) -> CoreHello:
    if not isinstance(frame, dict):
        raise MalformedFrame("a Core frame must be one JSON object")
    if frame.get("kind") != "core_hello":
        raise OrderViolation("the first Core frame must be core_hello")
    fields = _exact(frame, ("carrier", "kind", "limits", "versions"), "core_hello")
    if fields["carrier"] != CARRIER:
        raise MalformedFrame("core_hello names an unknown carrier")
    versions = fields["versions"]
    if not isinstance(versions, list) or not versions or not all(isinstance(v, str) for v in versions):
        raise MalformedFrame("core_hello versions must be a nonempty list of strings")
    limits = validate_limits(fields["limits"], ceiling=HARD_LIMITS)
    if limits != HARD_LIMITS:
        raise MalformedFrame("core_hello must advertise exactly the frozen hard limits")
    return CoreHello(limits=limits, versions=tuple(versions))


def parse_diagnostic(value: Any) -> Diagnostic:
    if not isinstance(value, dict):
        raise MalformedFrame("a diagnostic must be one JSON object")
    required = ("cause", "code", "domain", "next_step", "schema", "severity", "span", "summary")
    keys = set(value)
    if not set(required) <= keys or not keys <= set(required) | {"contrast"}:
        raise MalformedFrame("a diagnostic must carry exactly the diagnostic-v1 fields")
    if value["schema"] != DIAGNOSTIC_SCHEMA:
        raise MalformedFrame("a diagnostic must use schema jacquard-diagnostic-v1")
    code = value["code"]
    if code is not None and not isinstance(code, str):
        raise MalformedFrame("a diagnostic code must be a string or null")
    contrast = value.get("contrast")
    if contrast is not None:
        contrast = _exact(contrast, ("intended", "mistaken"), "a diagnostic contrast")
        contrast = {key: _string(contrast[key], f"contrast {key}") for key in ("intended", "mistaken")}
    span = value["span"]
    if span is not None and not isinstance(span, dict):
        raise MalformedFrame("a diagnostic span must be an object or null")
    return Diagnostic(
        code=code,
        domain=_string(value["domain"], "diagnostic domain"),
        severity=_string(value["severity"], "diagnostic severity"),
        summary=_string(value["summary"], "diagnostic summary"),
        cause=_string(value["cause"], "diagnostic cause"),
        next_step=_string(value["next_step"], "diagnostic next_step"),
        span=span,
        contrast=contrast,
        raw=value,
    )


def _string_bytes(value: Any) -> int:
    """Decoded UTF-8 bytes of every string value in a JSON tree (keys excluded)."""
    if isinstance(value, str):
        return len(value.encode("utf-8", "surrogatepass"))
    if isinstance(value, dict):
        return sum(_string_bytes(item) for item in value.values())
    if isinstance(value, list):
        return sum(_string_bytes(item) for item in value)
    return 0


def _diagnostics(value: Any, limits: Mapping[str, int]) -> tuple[Diagnostic, ...]:
    if not isinstance(value, list) or not value:
        raise MalformedFrame("diagnostics must be a nonempty array")
    if len(value) > limits["max_diagnostics"]:
        raise LimitExceeded("diagnostics exceed max_diagnostics")
    if _string_bytes(value) > limits["max_diagnostic_bytes"]:
        raise LimitExceeded("diagnostics exceed max_diagnostic_bytes")
    return tuple(parse_diagnostic(item) for item in value)


def _evidence(value: Any, budget: Budget) -> dict[str, Any]:
    """Validate evidence; its interface types share the outcome frame's node budget."""
    fields = _exact(value, ("core", "host_observations"), "outcome evidence")
    core = _exact(
        fields["core"],
        ("capabilities", "effect_requests", "interface", "invocation_id", "schema", "target", "terminal"),
        "core evidence",
    )
    if core["schema"] != CORE_EVIDENCE_SCHEMA:
        raise MalformedFrame("core evidence must use schema jacquard-host-core-evidence-v0")
    if core["invocation_id"] != INVOCATION_ID:
        raise OrderViolation("core evidence names the wrong invocation")
    if core["terminal"] not in TERMINALS:
        raise MalformedFrame("core evidence terminal is not a known classification")
    target = _exact(core["target"], ("callable", "kind"), "evidence target")
    if target["kind"] != TARGET_KIND:
        raise MalformedFrame("evidence target kind must be store-term-v0")
    parse_hash(target["callable"], "evidence target callable")
    interface = _exact(core["interface"], ("effects", "parameters", "result"), "evidence interface")
    capabilities = _exact(core["capabilities"], ("effects", "operations"), "evidence capabilities")
    for name, items in (("interface effects", interface["effects"]), ("capability effects", capabilities["effects"])):
        if not isinstance(items, list):
            raise MalformedFrame(f"evidence {name} must be an array")
        hashes = [parse_hash(item, name) for item in items]
        if hashes != sorted(set(hashes)):
            raise MalformedFrame(f"evidence {name} must be a strictly sorted set")
    if not isinstance(interface["parameters"], list) or not isinstance(interface["result"], dict):
        raise MalformedFrame("evidence interface parameters and result are malformed")
    budget.check_count(len(interface["parameters"]), arguments=True, context="evidence parameters")
    for item in interface["parameters"]:
        decode_type(item, budget)
    decode_type(interface["result"], budget)
    if not isinstance(capabilities["operations"], list):
        raise MalformedFrame("evidence capability operations must be an array")
    for item in capabilities["operations"]:
        entry = _exact(item, ("effect", "mode", "operation"), "an evidence operation entry")
        if entry["mode"] != "once":
            raise MalformedFrame("an evidence operation entry must use mode once")
        parse_hash(entry["effect"], "evidence operation effect")
        parse_hash(entry["operation"], "evidence operation")
    requests = core["effect_requests"]
    if not isinstance(requests, list):
        raise MalformedFrame("core evidence effect_requests must be an array")
    for index, item in enumerate(requests, start=1):
        entry = _exact(item, ("effect", "operation", "ordinal"), "an effect_requests entry")
        if entry["ordinal"] != index or isinstance(entry["ordinal"], bool):
            raise MalformedFrame("core evidence effect_requests ordinals must be 1-based and consecutive")
        parse_hash(entry["effect"], "evidence effect")
        parse_hash(entry["operation"], "evidence operation")
    observations = _exact(fields["host_observations"], ("responses", "schema"), "host observations")
    if observations["schema"] != HOST_OBSERVATIONS_SCHEMA:
        raise MalformedFrame("host observations must use schema jacquard-host-observations-v0")
    responses = observations["responses"]
    if not isinstance(responses, list):
        raise MalformedFrame("host observations responses must be an array")
    for item in responses:
        entry = _exact(item, ("category", "completion", "ordinal"), "a host observation")
        if entry["category"] not in OBSERVATION_CATEGORIES:
            raise MalformedFrame("an observation category is not a known claim")
        if entry["completion"] not in OBSERVATION_COMPLETIONS:
            raise MalformedFrame("an observation completion is not a known claim")
        ordinal = entry["ordinal"]
        if isinstance(ordinal, bool) or not isinstance(ordinal, int) or ordinal < 1 or ordinal > len(requests):
            raise MalformedFrame("an observation ordinal must name a recorded request")
    return fields


def parse_core_frame(frame: Any, *, limits: Mapping[str, int]) -> CoreFrame:
    """Parse any post-hello Core frame into its typed envelope."""
    if not isinstance(frame, dict):
        raise MalformedFrame("a Core frame must be one JSON object")
    kind = frame.get("kind")
    if kind == "effect_request":
        fields = _exact(
            frame,
            ("arguments", "effect", "invocation_id", "kind", "mode", "operation", "protocol", "request_id"),
            "effect_request",
        )
        _protocol(fields)
        _invocation(fields)
        if fields["mode"] != "once":
            raise MalformedFrame("effect_request mode must be once")
        ordinal_text = _string(fields["request_id"], "request_id")
        if len(ordinal_text) != 16 or not all(c in "0123456789abcdef" for c in ordinal_text):
            raise MalformedFrame("request_id must be 16 lowercase hexadecimal digits")
        ordinal = int(ordinal_text, 16)
        if ordinal < 1:
            raise OrderViolation("request ordinals start at 1")
        budget = Budget(limits)
        arguments = fields["arguments"]
        if not isinstance(arguments, list):
            raise MalformedFrame("effect_request arguments must be an array")
        budget.check_count(len(arguments), arguments=True, context="request arguments")
        return EffectRequest(
            ordinal=ordinal,
            effect=parse_hash(fields["effect"], "request effect"),
            operation=parse_hash(fields["operation"], "request operation"),
            arguments=tuple(decode_value(item, budget) for item in arguments),
            raw=fields,
        )
    if kind == "outcome":
        fields = _exact(frame, ("evidence", "invocation_id", "kind", "protocol", "result"), "outcome")
        _protocol(fields)
        _invocation(fields)
        # Core encodes evidence types and the result value against one frame budget.
        budget = Budget(limits)
        evidence = _evidence(fields["evidence"], budget)
        result = fields["result"]
        if not isinstance(result, dict):
            raise MalformedFrame("outcome result must be an object")
        terminal = evidence["core"]["terminal"]
        if result.get("kind") == "ok":
            _exact(result, ("kind", "value"), "ok result")
            if terminal != "ok":
                raise MalformedFrame("an ok result must carry terminal ok")
            return Outcome(True, decode_value(result["value"], budget), (), terminal, evidence, fields)
        if result.get("kind") == "error":
            _exact(result, ("diagnostics", "kind"), "error result")
            if terminal == "ok":
                raise MalformedFrame("an error result cannot carry terminal ok")
            return Outcome(False, None, _diagnostics(result["diagnostics"], limits), terminal, evidence, fields)
        raise MalformedFrame("outcome result kind must be ok or error")
    if kind == "fatal":
        fields = _exact(frame, ("diagnostics", "kind", "protocol"), "fatal")
        _protocol(fields)
        return Fatal(_diagnostics(fields["diagnostics"], limits), fields)
    if kind == "shutdown_ack":
        fields = _exact(frame, ("kind", "protocol"), "shutdown_ack")
        _protocol(fields)
        return ShutdownAck(fields)
    if kind == "core_hello":
        raise OrderViolation("core_hello may only be the first frame")
    raise MalformedFrame(f"unknown Core frame kind {kind!r}")
