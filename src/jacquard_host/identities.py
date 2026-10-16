"""Resolve display names to the exact identities the protocol requires.

The protocol binds everything by ``HASH_V0`` identity (decision D80):
mutable display names never select a target or an operation. This module is
the host-owned bridge from a reviewed source file to those identities, using
only public Core commands:

- ``jacquard hash FILE`` prints ``INDEX:NAME HASH`` for every member of every
  top-level form, which yields term, effect, operation, type, and constructor
  identities of a ``.jac`` or ``.jqd`` file (including prelude files);
- ``jacquard check FILE --print-sigs`` prints each term's checked signature
  in display form, which :func:`parse_signature` turns into a boundary
  :class:`~jacquard_host.protocol.envelopes.Interface` for the first-order
  subset v0 admits.

Surface display names are PascalCase for types, constructors, and effects
and kebab-case for terms and operations (surface-syntax decision D34);
:func:`display_to_store` maps ``KitWorld`` to ``kit-world`` and ``Int`` to
``int`` so a signature can be resolved against the hashed names.
"""

from __future__ import annotations

import os
import re
import subprocess
from typing import Mapping, Sequence

from jacquard_host.protocol.envelopes import Interface
from jacquard_host.protocol.values import Hash, Nominal, TupleType, Type
from jacquard_host.worker import DEFAULT_COMMAND

_HASH_LINE = re.compile(r"^(\d+):(\S+) ([0-9a-f]{64})$")
_SIG_LINE = re.compile(r"^(\S+) : (.+)$")


def _run(command: Sequence[str], args: Sequence[str], *, prelude: str | os.PathLike[str] | None) -> str:
    env = dict(os.environ)
    if prelude is not None:
        env["JACQUARD_PRELUDE"] = os.path.abspath(os.fspath(prelude))
    try:
        completed = subprocess.run([*command, *args], capture_output=True, text=True, env=env, check=False, timeout=30)
    except subprocess.TimeoutExpired:
        raise RuntimeError(f"{args[0]} exceeded the 30 second preparation deadline") from None
    if completed.returncode != 0:
        raise RuntimeError(f"{' '.join(args[:1])} exited {completed.returncode}: {completed.stderr.strip()}")
    return completed.stdout


def hash_members(path: str | os.PathLike[str], *, command: Sequence[str] = DEFAULT_COMMAND, prelude: str | os.PathLike[str] | None = None) -> list[tuple[int, str, Hash]]:
    """Every ``(form index, store name, identity)`` line ``jacquard hash`` prints, in order.

    A type and a constructor may share a name (``type Wrapper = | Wrapper Text``);
    Core prints the type member first. Use this list when a constructor's
    identity is needed as a value.
    """
    members: list[tuple[int, str, Hash]] = []
    for line in _run(command, ["hash", os.fspath(path)], prelude=prelude).splitlines():
        match = _HASH_LINE.match(line.strip())
        if match:
            members.append((int(match.group(1)), match.group(2), Hash(match.group(3))))
    return members


def hash_file(path: str | os.PathLike[str], *, command: Sequence[str] = DEFAULT_COMMAND, prelude: str | os.PathLike[str] | None = None) -> dict[str, Hash]:
    """Member identities of every top-level form in ``path``, keyed by store name.

    When a name is printed more than once the first identity is kept, which
    is the type for a type/constructor pair; signatures only ever name types
    and effects, so that is the identity :func:`parse_signature` needs.
    """
    names: dict[str, Hash] = {}
    for _, name, identity in hash_members(path, command=command, prelude=prelude):
        names.setdefault(name, identity)
    return names


def signatures(path: str | os.PathLike[str], *, command: Sequence[str] = DEFAULT_COMMAND, prelude: str | os.PathLike[str] | None = None) -> dict[str, str]:
    """Checked display signatures of every term in ``path``."""
    sigs: dict[str, str] = {}
    for line in _run(command, ["check", os.fspath(path), "--print-sigs"], prelude=prelude).splitlines():
        match = _SIG_LINE.match(line.strip())
        if match:
            sigs[match.group(1)] = match.group(2)
    return sigs


def display_to_store(name: str) -> str:
    """``KitWorld`` -> ``kit-world``; kebab names pass through unchanged."""
    if name[:1].isupper():
        return re.sub(r"(?<!^)(?=[A-Z])", "-", name).lower()
    return name


# --- the display type grammar for the v0 first-order subset ---------------

_TOKEN = re.compile(r"\s*(->\{|[(),{}]|[A-Za-z][A-Za-z0-9.?!-]*|\s)")


class SignatureError(ValueError):
    pass


def _tokens(text: str) -> list[str]:
    out: list[str] = []
    position = 0
    while position < len(text):
        match = _TOKEN.match(text, position)
        if not match:
            raise SignatureError(f"unexpected text at {text[position:position + 12]!r}")
        token = match.group(1)
        position = match.end()
        if not token.isspace():
            out.append(token)
    return out


class _Parser:
    def __init__(self, tokens: list[str], names: Mapping[str, Hash]) -> None:
        self.tokens = tokens
        self.names = names
        self.index = 0

    def peek(self) -> str | None:
        return self.tokens[self.index] if self.index < len(self.tokens) else None

    def take(self, expected: str | None = None) -> str:
        token = self.peek()
        if token is None or (expected is not None and token != expected):
            raise SignatureError(f"expected {expected or 'a token'}, found {token!r}")
        self.index += 1
        return token

    def resolve(self, display: str) -> Hash:
        for candidate in (display_to_store(display), display):
            if candidate in self.names:
                return self.names[candidate]
        raise SignatureError(f"no identity is known for {display!r}")

    def type_list(self, close: str) -> tuple[Type, ...]:
        items: list[Type] = []
        if self.peek() == close:
            self.take(close)
            return tuple(items)
        while True:
            items.append(self.type_())
            if self.peek() == ",":
                self.take(",")
                if self.peek() == close:
                    self.take(close)
                    return tuple(items)
                continue
            self.take(close)
            return tuple(items)

    def atom(self) -> Type:
        token = self.take()
        if token == "(":
            items = self.type_list(")")
            trailing_comma = self.tokens[self.index - 2] == ","
            return items[0] if len(items) == 1 and not trailing_comma else TupleType(items)
        if not token[:1].isalpha():
            raise SignatureError(f"unexpected {token!r} in a type")
        return Nominal(self.resolve(token))

    def type_(self) -> Type:
        head = self.atom()
        if isinstance(head, Nominal):
            arguments: list[Type] = []
            while self.peek() not in (None, ",", ")", "}", "->{"):
                arguments.append(self.atom())
            if arguments:
                return Nominal(head.identity, tuple(arguments))
        return head

    def interface(self) -> Interface:
        self.take("(")
        parameters = self.type_list(")")
        self.take("->{")
        effects: list[Hash] = []
        if self.peek() != "}":
            while True:
                effects.append(self.resolve(self.take()))
                if self.peek() == ",":
                    self.take(",")
                    continue
                break
        self.take("}")
        result = self.type_()
        if self.peek() is not None:
            raise SignatureError(f"trailing {self.peek()!r} after the result type")
        return Interface(parameters=parameters, result=result, effects=tuple(sorted(set(effects))))


def parse_signature(text: str, names: Mapping[str, Hash]) -> Interface:
    """Turn ``(Text) ->{KitWorld} Text`` into a boundary interface using ``names``.

    Only the v0 subset is accepted: positional first-order parameters, a
    closed effect row, nominal types with type arguments, tuples, and unit.
    Type variables, arrows inside data, and open rows raise
    :class:`SignatureError`. Explicit one-item tuples use ``(Int,)``. The pinned
    checker prints them as ``(Int)``, indistinguishable from plain ``Int``;
    build such an interface by hand with :class:`TupleType` when consuming
    that ambiguous printer output.
    """
    return _Parser(_tokens(text), names).interface()
