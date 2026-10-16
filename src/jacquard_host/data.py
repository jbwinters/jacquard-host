"""Explicit Python data mappings over the existing v0 value codec.

No Jacquard source is parsed here. Core still checks every constructor's type
and fields. Dataclass annotations describe the application's Python mapping;
they are not a substitute for a Core interface export.
"""

from __future__ import annotations

import dataclasses
from enum import Enum
import types
from typing import Any, Mapping, Union, get_args, get_origin, get_type_hints

from jacquard_host.errors import BindingError, InvalidArgument
from jacquard_host.identities import display_to_store
from jacquard_host.protocol.values import Constructor, Hash, INT_MAX, INT_MIN, Nominal, TupleType, Type
from jacquard_host.protocol.limits import HARD_LIMITS


@dataclasses.dataclass(frozen=True)
class Some:
    """An explicit Some, including Some(None) for nested options."""
    value: Any


@dataclasses.dataclass(frozen=True)
class Variant:
    """A named constructor when no application dataclass/enum is registered."""
    name: str
    fields: tuple[Any, ...] = ()

    def __post_init__(self) -> None:
        object.__setattr__(self, "name", display_to_store(self.name))
        object.__setattr__(self, "fields", tuple(self.fields))

    def __repr__(self) -> str:
        return f"{self.name}({', '.join(repr(field) for field in self.fields)})"


def _optional(annotation: Any) -> Any:
    args = get_args(annotation)
    if get_origin(annotation) in (Union, types.UnionType) and type(None) in args:
        remaining = tuple(arg for arg in args if arg is not type(None))
        return remaining[0] if len(remaining) == 1 else Union[remaining]
    return None


def _matches(value: Any, annotation: Any) -> bool:
    if annotation in (Any, object):
        return True
    if get_origin(annotation) in (Union, types.UnionType):
        return any(_matches(value, item) for item in get_args(annotation))
    required = get_origin(annotation) or annotation
    if not isinstance(required, type):
        return True
    return type(value) is required if required in (int, float, bool) else isinstance(value, required)


class Data:
    """Program-owned constructor factories and optional dataclass mappings."""

    def __init__(self, names: Mapping[str, str], constructors: Mapping[str, str], *, max_depth: int = HARD_LIMITS["max_json_depth"], max_text_bytes: int = HARD_LIMITS["max_text_bytes"]) -> None:
        self.names = {name: Hash(value) for name, value in names.items()}
        self.constructors = {name: Hash(value) for name, value in constructors.items()}
        self.labels = {identity: name for name, identity in self.constructors.items()}
        self.max_depth = max_depth
        self.max_text_bytes = max_text_bytes
        self._records: dict[type, tuple[Hash, dict[str, Any]]] = {}
        self._record_ids: dict[Hash, type] = {}
        self._enums: dict[Hash, Enum] = {}

    def __getattr__(self, name: str):
        if name.startswith("_"):
            raise AttributeError(name)
        store = display_to_store(name)
        if store not in self.constructors:
            raise AttributeError(f"No declaration named {name!r} in this prepared program")
        return lambda *fields: Variant(name, tuple(fields))

    def record(self, cls=None, *, constructor: str | None = None):
        """Register an explicit dataclass mapping, usable as a decorator."""
        def register(record):
            if not dataclasses.is_dataclass(record) or not isinstance(record, type):
                raise BindingError("@program.data.record needs a dataclass class")
            if any(not field.init for field in dataclasses.fields(record)):
                raise BindingError("Mapped dataclasses must have only init=True fields")
            name = display_to_store(constructor or record.__name__)
            if name not in self.constructors:
                raise BindingError(f"No constructor named {constructor or record.__name__!r}")
            identity = self.constructors[name]
            if name in ("true", "false", "nil", "cons", "none", "some"):
                raise BindingError("Prelude constructors use native bool/list/option mappings")
            if identity in self._record_ids or identity in self._enums:
                raise BindingError(f"{name} already has a Python mapping")
            try:
                hints = get_type_hints(record)
            except (NameError, TypeError) as error:
                raise BindingError(f"Cannot resolve {record.__name__}'s annotations: {error}") from error
            self._records[record] = (identity, hints)
            self._record_ids[identity] = record
            return record
        return register(cls) if cls is not None else register

    def enum(self, cls):
        """Map enum members whose string values are Jacquard constructor names."""
        if not isinstance(cls, type) or not issubclass(cls, Enum):
            raise BindingError("program.data.enum needs an Enum class")
        pending = {}
        for member in cls:
            if not isinstance(member.value, str):
                raise BindingError("Mapped enum values must be constructor names")
            name = display_to_store(member.value)
            if name not in self.constructors:
                raise BindingError(f"No constructor named {member.value!r}")
            identity = self.constructors[name]
            if name in ("true", "false", "nil", "cons", "none", "some"):
                raise BindingError("Prelude constructors use native bool/list/option mappings")
            if identity in self._enums or identity in self._record_ids or identity in pending:
                raise BindingError(f"{name} already has a Python mapping")
            pending[identity] = member
        self._enums.update(pending)
        return cls

    def _con(self, name: str, *fields: Any) -> Constructor:
        try:
            return Constructor(self.constructors[name], tuple(fields))
        except KeyError:
            raise BindingError(f"The prepared program has no {name!r} constructor") from None

    def _primitive(self, expected: Type | None) -> str | None:
        if isinstance(expected, Nominal):
            return next((name for name in ("int", "real", "text", "hash", "bool", "list", "option") if self.names.get(name) == expected.identity), None)
        return None

    def encode(self, value: Any, expected: Type | None = None, *, path: str = "argument", depth: int = 3, annotation: Any = Any) -> Any:
        """Convert ordinary Python data, with errors naming the offending field."""
        if depth + 1 > self.max_depth:
            raise InvalidArgument(f"{path}: value exceeds v0 JSON depth {self.max_depth}; use smaller batches")
        primitive = self._primitive(expected)
        optional = _optional(annotation)
        if optional is not None or primitive == "option":
            if value is None:
                return self._con("none")
            inner = expected.arguments[0] if primitive == "option" else None
            raw = value.value if isinstance(value, Some) else value
            return self._con("some", self.encode(raw, inner, path=path + ".some", depth=depth + 2, annotation=optional or Any))
        if isinstance(value, Some):
            return self._con("some", self.encode(value.value, path=path + ".some", depth=depth + 2))
        if not _matches(value, annotation):
            raise InvalidArgument(f"{path}: expected {getattr(annotation, '__name__', str(annotation))}, received {type(value).__name__}")
        required = {"int": int, "real": float, "text": str, "hash": Hash, "bool": bool, "list": list}.get(primitive)
        origin = get_origin(annotation)
        annotation_type = origin or annotation
        if required is None and annotation_type in (int, float, str, bool, list, tuple, Hash):
            required = annotation_type
        if required is not None and (not isinstance(value, required) or required in (int, float, bool) and type(value) is not required):
            raise InvalidArgument(f"{path}: expected {required.__name__}, received {type(value).__name__}")
        if isinstance(expected, TupleType) and (not isinstance(value, tuple) or len(value) != len(expected.items)):
            raise InvalidArgument(f"{path}: expected a tuple with {len(expected.items)} fields")
        if isinstance(value, Enum):
            if required is not None:
                raise InvalidArgument(f"{path}: expected {required.__name__}, received {type(value).__name__}")
            for identity, member in self._enums.items():
                if value is member:
                    return Constructor(identity)
            raise InvalidArgument(f"{path}: register {type(value).__name__} with program.data.enum")
        if isinstance(value, bool):
            return self._con("true" if value else "false")
        if value is None:
            return self._con("none")
        if isinstance(value, int):
            if not INT_MIN <= value <= INT_MAX:
                raise InvalidArgument(f"{path}: Int must be in {INT_MIN}..{INT_MAX}")
            return value
        if isinstance(value, str):
            try:
                encoded = value.encode("utf-8")
            except UnicodeEncodeError:
                raise InvalidArgument(f"{path}: Text cannot contain lone Unicode surrogates") from None
            if len(encoded) > self.max_text_bytes:
                raise InvalidArgument(f"{path}: Text exceeds {self.max_text_bytes} UTF-8 bytes")
            return value
        if isinstance(value, (float, Hash)):
            return value
        if isinstance(value, list):
            if len(value) > HARD_LIMITS["max_collection_items"]:
                raise InvalidArgument(f"{path}: at most {HARD_LIMITS['max_collection_items']} list items; use smaller batches")
            element = expected.arguments[0] if primitive == "list" else None
            annotated = get_args(annotation)[0] if origin is list and get_args(annotation) else Any
            fields = [self.encode(item, element, path=f"{path}[{i}]", depth=depth + 2 * (i + 1), annotation=annotated) for i, item in enumerate(value)]
            if depth + 2 * len(fields) + 1 > self.max_depth:
                raise InvalidArgument(f"{path}: list exceeds v0 JSON depth {self.max_depth}; use smaller batches")
            result = self._con("nil")
            for item in reversed(fields):
                result = self._con("cons", item, result)
            return result
        if isinstance(value, tuple):
            items = expected.items if isinstance(expected, TupleType) else (None,) * len(value)
            args = get_args(annotation) if origin is tuple else ()
            if args and args[-1] is not Ellipsis and len(args) != len(value):
                raise InvalidArgument(f"{path}: expected {len(args)} tuple fields")
            return tuple(self.encode(item, ty, path=f"{path}[{i}]", depth=depth + 2, annotation=args[0] if args and args[-1] is Ellipsis else args[i] if args else Any) for i, (item, ty) in enumerate(zip(value, items)))
        if type(value) in self._records:
            identity, hints = self._records[type(value)]
            return Constructor(identity, tuple(self.encode(getattr(value, field.name), path=f"{path}.{field.name}", depth=depth + 2, annotation=hints.get(field.name, Any)) for field in dataclasses.fields(value)))
        if isinstance(value, Variant):
            name = display_to_store(value.name)
            if name not in self.constructors:
                raise InvalidArgument(f"{path}: unknown constructor {value.name!r}")
            return self._con(name, *(self.encode(field, path=f"{path}.{i}", depth=depth + 2) for i, field in enumerate(value.fields)))
        if isinstance(value, Constructor):
            # Raw protocol values remain an escape hatch, with depth checked.
            return Constructor(value.identity, tuple(self.encode(field, path=f"{path}.{i}", depth=depth + 2) for i, field in enumerate(value.arguments)))
        raise InvalidArgument(f"{path}: {type(value).__name__} has no mapping; use a registered dataclass or program.data constructor")

    def decode(self, value: Any, expected: Type | None = None, *, annotation: Any = Any) -> Any:
        result = self._decode(value, expected, annotation=annotation)
        if not _matches(result, annotation):
            raise BindingError(f"Python mapping expects {getattr(annotation, '__name__', str(annotation))}; Core value decodes as {type(result).__name__}")
        return result

    def _decode(self, value: Any, expected: Type | None = None, *, annotation: Any = Any) -> Any:
        if isinstance(value, tuple):
            items = expected.items if isinstance(expected, TupleType) else (None,) * len(value)
            args = get_args(annotation) if get_origin(annotation) is tuple else ()
            if args and args[-1] is not Ellipsis and len(args) != len(value):
                raise BindingError(f"Python mapping expects {len(args)} tuple fields; Core returned {len(value)}")
            return tuple(self.decode(field, ty, annotation=args[0] if args and args[-1] is Ellipsis else args[i] if args else Any) for i, (field, ty) in enumerate(zip(value, items)))
        if not isinstance(value, Constructor):
            return value
        if value.identity in self._enums:
            return self._enums[value.identity]
        if value.identity in self._record_ids:
            cls = self._record_ids[value.identity]
            _, hints = self._records[cls]
            fields = dataclasses.fields(cls)
            if len(fields) != len(value.arguments):
                raise BindingError(f"{cls.__name__} mapping has {len(fields)} fields; Core returned {len(value.arguments)}")
            return cls(**{field.name: self.decode(arg, annotation=hints.get(field.name, Any)) for field, arg in zip(fields, value.arguments)})
        name = self.labels.get(value.identity)
        if name in ("true", "false") and not value.arguments:
            return name == "true"
        if name == "none" and not value.arguments:
            return None
        if name == "some" and len(value.arguments) == 1:
            primitive = self._primitive(expected)
            inner = expected.arguments[0] if primitive == "option" else None
            decoded = self.decode(value.arguments[0], inner, annotation=_optional(annotation) or Any)
            if _optional(annotation) is not None or primitive == "option" and self._primitive(inner) != "option":
                return decoded
            return Some(decoded)
        if name in ("cons", "nil"):
            result = []
            inner = expected.arguments[0] if self._primitive(expected) == "list" else None
            args = get_args(annotation)
            annotated = args[0] if get_origin(annotation) is list and args else Any
            while value.identity == self.constructors["cons"] and len(value.arguments) == 2:
                head, value = value.arguments
                result.append(self.decode(head, inner, annotation=annotated))
                if not isinstance(value, Constructor):
                    raise BindingError("Core returned an improper List")
            if value != self._con("nil"):
                raise BindingError("Core returned an improper List")
            return result
        if name is None:
            raise BindingError(f"Core returned constructor {value.identity} without a prepared name")
        # Keep store spelling as the stable lookup name; repr is intentionally simple.
        return Variant(name, tuple(self.decode(arg) for arg in value.arguments))
