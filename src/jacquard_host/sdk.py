"""Prepare first-party programs once, bind services, and call ordinary data.

This convenience layer consumes the pinned public hash/check/run commands.
It does not generate bindings or infer data layouts from source. Automatic
schema/code generation remains gated on Core's host interface export.
"""

from __future__ import annotations

import base64
from dataclasses import dataclass
from functools import cached_property
import hashlib
import inspect
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
from typing import Any, Mapping, Sequence, get_type_hints

from jacquard_host.adapter import Host, InvocationResult
from jacquard_host.compat import CORE_VERSION, PROTOCOL
from jacquard_host.data import Data
from jacquard_host.errors import BindingError, InvalidArgument, JacquardError, PreparationError
from jacquard_host.identities import display_to_store, hash_members, parse_signature, signatures, SignatureError
from jacquard_host.protocol import envelopes
from jacquard_host.protocol.envelopes import Interface
from jacquard_host.protocol.errors import ProtocolError
from jacquard_host.protocol.framing import frame_bytes, validate_deadline_seconds
from jacquard_host.protocol.json_codec import encode_object, validate_object
from jacquard_host.protocol.limits import HARD_LIMITS, validate_limits
from jacquard_host.protocol.values import Hash, Nominal
from jacquard_host.registry import HostFailure, OperationRegistry

PREPARATION_SCHEMA = "jacquard-python-program-v1"


def _digest(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(65536), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _files(directory: Path) -> dict[str, str]:
    return {str(path.relative_to(directory)): _digest(path) for path in sorted(directory.rglob("*")) if path.is_file()}


def _normal(name: str) -> str:
    return display_to_store(name).replace("_", "-")


def _check_groups(groups) -> None:
    seen: dict[str, set[str]] = {}
    for group in groups:
        current: dict[str, set[str]] = {}
        for name, identity in group:
            current.setdefault(name, set()).add(identity)
        for name, identities in current.items():
            if name in seen and not identities <= seen[name]:
                raise BindingError(f"Public name {name!r} appears in several declaration groups with different identities. The pinned CLI cannot disambiguate their kinds; use distinct names or the low-level Host API with explicit identities")
            seen.setdefault(name, set()).update(identities)


class Runtime:
    """One configured Core installation; no worker or listener is kept alive."""

    def __init__(self, binary: str | os.PathLike[str] | None = None, *, prelude: str | os.PathLike[str] | None = None,
                 deadline_seconds: float | None = 30.0, limits: Mapping[str, int] | None = None) -> None:
        self.binary = os.fspath(binary or os.environ.get("JACQUARD_BIN", "jacquard"))
        configured = prelude or os.environ.get("JACQUARD_PRELUDE")
        if configured is None:
            raise PreparationError("Pass prelude= or set JACQUARD_PRELUDE to the pinned Core prelude directory")
        self.prelude = Path(configured).resolve()
        try:
            validate_deadline_seconds(deadline_seconds)
            self.limits = validate_limits({**HARD_LIMITS, **(limits or {})})
        except (ValueError, ProtocolError) as error:
            raise BindingError(str(error)) from error
        self.deadline_seconds = deadline_seconds
        self._version_digest: str | None = None
        self._prelude_cache: tuple[str, list] | None = None

    def _installation(self) -> tuple[str, str, str]:
        executable = shutil.which(self.binary)
        if executable is None:
            raise PreparationError(f"Cannot find Jacquard executable {self.binary!r}; pass binary= or set JACQUARD_BIN")
        executable = str(Path(executable).resolve())
        binary_hash = _digest(Path(executable))
        if self._version_digest != binary_hash:
            try:
                result = subprocess.run([executable, "--version"], capture_output=True, text=True, timeout=10)
            except (OSError, subprocess.TimeoutExpired) as error:
                raise PreparationError(f"Cannot check Jacquard version: {error}") from error
            if result.returncode or result.stdout.strip().split()[-1:] != [CORE_VERSION]:
                raise PreparationError(f"This adapter pins Core {CORE_VERSION}; found {result.stdout.strip() or 'an unreadable version'}. Use the worker and prelude named in COMPATIBILITY.md")
            self._version_digest = binary_hash
        if not (self.prelude / "01-prim.jqd").is_file():
            raise PreparationError(f"No Core prelude found at {self.prelude}")
        prelude_files = sorted(path for path in self.prelude.rglob("*") if path.is_file())
        digest = hashlib.sha256()
        for path in prelude_files:
            digest.update(str(path.relative_to(self.prelude)).encode())
            digest.update(bytes.fromhex(_digest(path)))
        return executable, binary_hash, digest.hexdigest()

    def prepare(self, source: str | os.PathLike[str] | Sequence[str | os.PathLike[str]], *, cache_dir: str | os.PathLike[str] = ".scratch/jacquard-host") -> Program:
        """Check and populate a content-keyed cache, or verify and reopen it.

        Ordered source files are concatenated into a snapshot. The key includes
        source bytes, executable bytes, and every prelude file. Source/store
        payloads are digest-checked on reuse. Partial builds are never published.
        """
        try:
            return self._prepare(source, Path(cache_dir).resolve())
        except (PreparationError, BindingError):
            raise
        except (OSError, RuntimeError, ValueError, subprocess.SubprocessError) as error:
            raise PreparationError(f"Could not prepare Jacquard program: {error}") from error

    def _prepare(self, source, cache: Path) -> Program:
        paths = [Path(source)] if isinstance(source, (str, os.PathLike)) else [Path(path) for path in source]
        if not paths or len({path.suffix for path in paths}) != 1 or paths[0].suffix not in (".jac", ".jqd"):
            raise PreparationError("Pass one or more .jac files, or one or more .jqd files, in dependency order")
        executable, binary_hash, prelude_hash = self._installation()
        content = b"\n".join(path.read_bytes() for path in paths)
        identity = {"schema": PREPARATION_SCHEMA, "protocol": PROTOCOL, "binary": binary_hash, "prelude": prelude_hash,
                    "source": hashlib.sha256(content).hexdigest(), "syntax": paths[0].suffix}
        key = hashlib.sha256(json.dumps(identity, sort_keys=True).encode()).hexdigest()
        cache.mkdir(parents=True, exist_ok=True)
        target = cache / key
        if target.exists():
            return self._load(target, identity, executable)
        with tempfile.TemporaryDirectory(prefix="prepare-", dir=cache) as temporary:
            staging = Path(temporary)
            snapshot = staging / ("source" + paths[0].suffix)
            snapshot.write_bytes(content)
            command = [executable]
            sigs = signatures(snapshot, command=command, prelude=self.prelude)
            prelude_key = binary_hash + prelude_hash
            if self._prelude_cache is None or self._prelude_cache[0] != prelude_key:
                groups = []
                for filename in ("01-prim.jqd", "02-data.jqd", "19-audit.jqd"):
                    groups.extend(self._groups(hash_members(self.prelude / filename, command=command, prelude=self.prelude)))
                self._prelude_cache = (prelude_key, groups)
            groups = self._prelude_cache[1] + self._groups(hash_members(snapshot, command=command, prelude=self.prelude))
            _check_groups(groups)
            env = dict(os.environ, JACQUARD_PRELUDE=str(self.prelude))
            populated = subprocess.run([executable, "run", str(snapshot), "--store", str(staging / "store")],
                                       capture_output=True, text=True, env=env, timeout=30)
            if populated.returncode:
                raise PreparationError(f"Core could not populate {', '.join(str(path) for path in paths)}: {populated.stderr.strip()}")
            names, constructors = {}, {}
            for group in groups:
                first = {}
                for name, value in group:
                    first.setdefault(name, value)
                    constructors[name] = value
                names.update(first)
            metadata = {"identity": identity, "names": names, "constructors": constructors, "groups": groups,
                        "signatures": sigs, "files": _files(staging)}
            (staging / "program.json").write_text(json.dumps(metadata, sort_keys=True), encoding="utf-8")
            try:
                staging.rename(target)
            except OSError:
                if not target.exists():
                    raise
                # A concurrent preparation may have published the same key.
            return self._load(target, identity, executable)

    @staticmethod
    def _groups(members) -> list:
        groups: dict[int, list] = {}
        for index, name, value in members:
            groups.setdefault(index, []).append([name, value.hex])
        return list(groups.values())

    def _load(self, directory: Path, identity: dict, executable: str) -> Program:
        try:
            metadata = json.loads((directory / "program.json").read_text(encoding="utf-8"))
            observed = _files(directory)
            observed.pop("program.json", None)
            if metadata["identity"] != identity or metadata["files"] != observed:
                raise ValueError("cached artifact digests do not match")
            _check_groups(metadata["groups"])
            return Program(self, directory, metadata, executable)
        except BindingError:
            raise
        except (OSError, ValueError, KeyError, TypeError) as error:
            raise PreparationError(f"Prepared cache {directory} is incomplete or changed; remove that cache entry and prepare again ({error})") from error


class Program:
    """An immutable prepared store with explicit application data/service mappings."""

    def __init__(self, runtime: Runtime, directory: Path, metadata: dict, executable: str) -> None:
        self.runtime = runtime
        self.directory = directory
        self.metadata = metadata
        self.data = Data(metadata["names"], metadata["constructors"], max_depth=runtime.limits["max_json_depth"], max_text_bytes=runtime.limits["max_text_bytes"])
        self.host = Host(directory / "store", command=[executable], env=dict(os.environ, JACQUARD_PRELUDE=str(runtime.prelude)),
                         deadline_seconds=runtime.deadline_seconds, limits=runtime.limits)
        self._services: dict[str, Any] = {}
        self._interfaces = {}
        for name, signature in metadata["signatures"].items():
            try:
                self._interfaces[name] = parse_signature(signature, self.data.names)
            except SignatureError:
                pass  # Internal polymorphic helpers need not be boundary-callable.
        self._effects = {}
        effects = {effect for interface in self._interfaces.values() for effect in interface.effects}
        for group in metadata["groups"]:
            for name, value in group:
                if Hash(value) in effects:
                    # A known checked effect identity identifies its declaration
                    # group in public `hash` output. Require every sibling op,
                    # conservatively including ones this term may never reach.
                    self._effects[name] = (Hash(value), tuple((op, Hash(h)) for op, h in group if h != value))

    def provide(self, **services: Any) -> Program:
        """Configure shared services; each function receives only its effect row."""
        normal = {_normal(name): service for name, service in services.items()}
        unknown = set(normal) - set(self._effects)
        if unknown:
            raise BindingError(f"Unknown effect services: {', '.join(sorted(unknown))}")
        self._services.update(normal)
        return self

    def function(self, name: str, *, effects: Mapping[str, Any] | None = None) -> Function:
        """Bind a closed first-order term and validate all its effect services."""
        if name not in self._interfaces:
            raise BindingError(f"{name!r} is absent or has no supported closed v0 signature")
        interface = self._interfaces[name]
        services = dict(self._services)
        explicit = {_normal(key): value for key, value in (effects or {}).items()}
        selected = {name: spec for name, spec in self._effects.items() if spec[0] in interface.effects}
        if set(explicit) - set(selected):
            raise BindingError(f"{name} does not use effects: {', '.join(sorted(set(explicit) - set(selected)))}")
        services.update(explicit)
        if {spec[0] for spec in selected.values()} != set(interface.effects):
            raise BindingError(f"{name}: effect metadata is unavailable in the prepared source; keep the low-level Host API for this entry")
        registry = OperationRegistry()
        missing = []
        for effect, (identity, operations) in selected.items():
            service = services.get(effect)
            for operation, operation_id in operations:
                attribute = operation.replace("-", "_").replace(".", "_")
                handler = service.get(operation, service.get(attribute)) if isinstance(service, Mapping) else getattr(service, attribute, None)
                if not callable(handler):
                    missing.append(f"{effect}.{attribute}")
                    continue
                if inspect.iscoroutinefunction(handler) or inspect.iscoroutinefunction(getattr(handler, "__call__", None)):
                    raise BindingError(f"{effect}.{attribute}: async handlers are unsupported; bind a synchronous function")
                registry.bind(identity, operation_id, self._handler(handler), name=f"{effect}.{operation}")
        if missing:
            raise BindingError(f"{name}: missing handlers: {', '.join(missing)}. Bind every operation in each declared effect before calling")
        return Function(self, name, interface, registry)

    def _handler(self, handler):
        target = handler if inspect.isfunction(handler) or inspect.ismethod(handler) else getattr(handler, "__call__", handler)
        try:
            hints = get_type_hints(target)
            parameters = list(inspect.signature(handler).parameters.values())
        except (TypeError, ValueError):
            hints, parameters = {}, []
        except NameError as error:
            raise BindingError(f"Cannot resolve handler annotations: {error}") from error
        def invoke(*arguments):
            decoded = [self.data.decode(argument, annotation=hints.get(parameters[i].name, Any) if i < len(parameters) else Any) for i, argument in enumerate(arguments)]
            answer = handler(*decoded)
            if inspect.isawaitable(answer):
                if inspect.iscoroutine(answer):
                    answer.close()
                raise BindingError("A synchronous handler returned an awaitable")
            try:
                return self.data.encode(answer, path="handler result", annotation=hints.get("return", Any))
            except InvalidArgument as error:
                # Match the adapter's existing unrepresentable-result mapping.
                # This never promises that earlier outside work was rolled back.
                raise HostFailure("outside_failure", "failed", str(error)) from error
        return invoke


@dataclass
class Invocation:
    """An application value plus the unmodified low-level result and inspection."""
    raw: InvocationResult
    function: Function

    @property
    def ok(self) -> bool:
        return self.raw.ok

    @property
    def code(self) -> str | None:
        return self.raw.code

    @property
    def message(self) -> str:
        return self.raw.message

    @cached_property
    def value(self) -> Any:
        if not self.ok:
            return None
        try:
            return self.function.program.data.decode(self.raw.value, self.function.interface.result)
        except (BindingError, TypeError, ValueError) as error:
            failed = BindingError(f"{self.function.name}: Core completed, but the Python result mapping failed: {error}")
            failed.result = self.raw
            raise failed from error

    def unwrap(self) -> Any:
        self.raw.unwrap()
        return self.value

    def render(self) -> str:
        """Render operation order and outcome, without copying argument values."""
        labels = self.function.program.data.labels
        lines = [f"{self.function.name}: {'ok' if self.ok else self.message}"]
        for request in self.raw.requests:
            lines.append(f"  {request.ordinal}. {labels.get(request.operation, str(request.operation))}")
        return "\n".join(lines)

    def save(self, path: str | os.PathLike[str]) -> None:
        """Explicitly save raw frames (including argument/results) and operator evidence."""
        record = {"schema": "jacquard-host-inspection-v1", "function": self.function.name,
                  "core_frames": self.raw.core_frames, "host_frames": self.raw.host_frames,
                  "host": {"evidence": self.raw.host_evidence, "exit_status": self.raw.exit_status,
                           "signal": self.raw.signal, "stderr_base64": base64.b64encode(self.raw.stderr).decode("ascii")}}
        Path(path).write_text(json.dumps(record, indent=2, allow_nan=False) + "\n", encoding="utf-8")


class Function:
    """A callable with native data, validated services, and an inspectable invoke."""

    def __init__(self, program: Program, name: str, interface: Interface, registry: OperationRegistry) -> None:
        self.program, self.name, self.interface, self.registry = program, name, interface, registry

    def _arguments(self, arguments):
        if len(arguments) != len(self.interface.parameters):
            raise InvalidArgument(f"{self.name}: expected {len(self.interface.parameters)} arguments, received {len(arguments)}")
        try:
            encoded = tuple(self.program.data.encode(value, ty, path=f"{self.name}.argument[{i}]") for i, (value, ty) in enumerate(zip(arguments, self.interface.parameters)))
            frame = envelopes.invoke(self.program.data.names[self.name], self.interface, encoded, self.registry.entries(), limits=self.program.host.limits)
            validate_object(frame, max_depth=self.program.host.limits["max_json_depth"])
            frame_bytes(encode_object(frame), max_frame_bytes=self.program.host.limits["max_frame_bytes"])
            return encoded
        except ProtocolError as error:
            raise InvalidArgument(f"{self.name}: {error.message}; reduce the input or use smaller batches") from error

    def invoke(self, *arguments: Any) -> Invocation:
        encoded = self._arguments(arguments)
        try:
            raw = self.program.host.invoke(self.program.data.names[self.name], self.interface, encoded, self.registry)
        except OSError as error:
            raw = InvocationResult(kind="host_error", host_evidence={"schema": "jacquard-host-python-evidence-v0", "code": "E1611",
                                   "reason": "worker_spawn_failed", "detail": f"Could not start {self.program.host.command[0]} ({type(error).__name__}); check the configured Core installation"})
        return Invocation(raw, self)

    def __call__(self, *arguments: Any) -> Any:
        return self.invoke(*arguments).unwrap()

    def map_batches(self, *arguments: Any, batch_size: int = 8) -> list:
        """Evaluate a pure List -> List entry in explicit, never-retried chunks.

        The final argument is the list to split. Earlier arguments are shared.
        Eight is a convenience default, not an output-size guarantee.
        """
        if self.interface.effects:
            raise BindingError("map_batches is for pure functions; orchestrate effectful calls explicitly")
        if not arguments or not isinstance(arguments[-1], list) or isinstance(batch_size, bool) or not isinstance(batch_size, int) or batch_size < 1:
            raise InvalidArgument("map_batches needs a final list argument and a positive integer batch_size")
        list_id = self.program.data.names["list"]
        if not self.interface.parameters or not isinstance(self.interface.parameters[-1], Nominal) or self.interface.parameters[-1].identity != list_id or not isinstance(self.interface.result, Nominal) or self.interface.result.identity != list_id:
            raise BindingError("map_batches needs a final List parameter and a List result")
        self._arguments((*arguments[:-1], []))
        chunks = [(*arguments[:-1], arguments[-1][i:i + batch_size]) for i in range(0, len(arguments[-1]), batch_size)]
        for chunk in chunks:
            self._arguments(chunk)  # Reject bad late inputs before running any chunk.
        result = []
        for index, chunk in enumerate(chunks):
            invocation = self.invoke(*chunk)
            try:
                result.extend(invocation.unwrap())
            except JacquardError as error:
                error.batch_index = index
                error.completed_values = list(result)
                raise
        return result
