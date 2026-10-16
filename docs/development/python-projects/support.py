"""Small application-side glue, deliberately kept outside the library.

This is the boilerplate the experiments actually needed: store preparation,
identity discovery, constructor/list conversion, and readable result envelopes.
It does not generate bindings or implement another protocol.
"""
from __future__ import annotations

from dataclasses import dataclass
import os
from pathlib import Path
import subprocess
import time
from typing import Any

from jacquard_host.adapter import Host, InvocationResult
from jacquard_host.identities import display_to_store, hash_members, parse_signature, signatures
from jacquard_host.protocol.values import Constructor
from jacquard_host.registry import OperationRegistry


class Values:
    def __init__(self, members: list) -> None:
        self.names = {}
        self.constructors = {}
        for _, name, identity in members:
            self.names.setdefault(name, identity)
            # Core prints a same-named constructor after its type declaration.
            self.constructors[name] = identity
        self.labels = {identity: name for name, identity in self.constructors.items()}

    def make(self, name: str, *fields: Any) -> Constructor:
        return Constructor(self.constructors[display_to_store(name)], tuple(fields))

    def list(self, items) -> Constructor:
        result = self.make("Nil")
        for item in reversed(list(items)):
            result = self.make("Cons", item, result)
        return result

    def unlist(self, value: Constructor) -> list:
        out = []
        while value.identity == self.constructors["cons"]:
            item, value = value.arguments
            out.append(item)
        if value != self.make("Nil"):
            raise ValueError("not a proper List")
        return out

    def plain(self, value: Any) -> Any:
        if isinstance(value, Constructor):
            if value.identity in (self.constructors["cons"], self.constructors["nil"]):
                return [self.plain(item) for item in self.unlist(value)]
            return {"tag": self.labels.get(value.identity, str(value.identity)), "fields": [self.plain(item) for item in value.arguments]}
        if isinstance(value, tuple):
            return [self.plain(item) for item in value]
        return value


@dataclass
class Call:
    result: InvocationResult
    elapsed_ms: float
    readable: dict


class Program:
    def __init__(self, name: str, workdir: Path, *, binary: str, prelude: str) -> None:
        started = time.perf_counter()
        self.name = name
        self.command = [binary]
        self.prelude = prelude
        source = Path(__file__).resolve().parents[3] / "examples" / name / "rules.jac"
        self.store = workdir / name / "store"
        self.store.parent.mkdir(parents=True, exist_ok=True)
        self.env = dict(os.environ, JACQUARD_PRELUDE=prelude)
        populated = subprocess.run([binary, "run", str(source), "--store", str(self.store)], env=self.env, text=True, capture_output=True, timeout=60)
        if populated.returncode:
            raise RuntimeError(populated.stderr)
        members = []
        for filename in ("01-prim.jqd", "02-data.jqd"):
            members.extend(hash_members(Path(prelude) / filename, command=self.command, prelude=prelude))
        members.extend(hash_members(source, command=self.command, prelude=prelude))
        self.values = Values(members)
        self.sigs = signatures(source, command=self.command, prelude=prelude)
        self.host = Host(self.store, command=self.command, env=self.env)
        self.setup_ms = (time.perf_counter() - started) * 1000

    def call(self, term: str, *arguments: Any, registry: OperationRegistry | None = None, host: Host | None = None) -> Call:
        interface = parse_signature(self.sigs[term], self.values.names)
        started = time.perf_counter()
        result = (host or self.host).invoke(self.values.names[term], interface, arguments, registry)
        elapsed = (time.perf_counter() - started) * 1000
        readable = {
            "kind": result.kind, "terminal": result.terminal,
            "primary_code": result.primary_code, "host_evidence": result.host_evidence,
            "value": self.values.plain(result.value),
            "diagnostics": [{"code": d.code, "summary": d.summary, "cause": d.cause, "next_step": d.next_step} for d in result.diagnostics],
            "requests": [self.values.labels.get(r.operation, str(r.operation)) for r in result.requests],
            "elapsed_ms": round(elapsed, 3),
        }
        return Call(result, elapsed, readable)
