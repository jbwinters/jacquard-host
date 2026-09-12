"""The one named persistence operation for the synthetic study (roadmap H1.2).

Jacquard declares ``once effect Study where { save-response : (Text, Text,
Text) -> SaveOutcome }`` (see ``jacquard/study.jac``) and decides whether and
what to store; the host performs the persistence behind that single
identity. Nothing else is reachable: no SQL, no database handle, no path, no
generic filesystem access, and no participant identity. The ``session``
argument is a synthetic token minted by the application.

Contract of ``save-response(trial, session, answer)``:

- inputs are validated before any action; an invalid input is reported as
  ``refused_authority``/``not_started`` and nothing is written;
- the receipt is deterministic: SHA-256 over the schema name, trial, session,
  and answer, so the same save always yields the same receipt and no
  timestamp or random identifier enters Jacquard evidence;
- saving is idempotent per ``(trial, session)``: a repeat with the same
  answer returns ``Duplicate(receipt)``; a repeat with a different answer
  returns ``Conflict`` and stores nothing;
- every write is one ``BEGIN IMMEDIATE`` transaction; a failure before the
  commit is ``outside_failure``/``failed``; a failure while committing is
  ``completion_unknown``/``unknown`` and is never retried by the host.
"""

from __future__ import annotations

import hashlib
import os
import re
import sqlite3
from dataclasses import dataclass
from typing import Mapping

from jacquard_host.protocol.values import Constructor, Hash
from jacquard_host.registry import HostFailure, OperationRegistry

SCHEMA = "jacquard-host-study-save-v1"
MAX_TOKEN_CHARS = 128
MAX_ANSWER_BYTES = 4096
_TOKEN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")

_DDL = """
CREATE TABLE IF NOT EXISTS responses (
    schema        TEXT    NOT NULL,
    trial         TEXT    NOT NULL,
    session       TEXT    NOT NULL,
    answer        TEXT    NOT NULL,
    receipt       TEXT    NOT NULL,
    ordinal       INTEGER NOT NULL,
    PRIMARY KEY (trial, session)
);
"""


@dataclass(frozen=True)
class StudyIdentities:
    """The exact identities of the declarations in ``jacquard/study.jac``."""

    effect: Hash
    save_response: Hash
    saved: Hash
    duplicate: Hash
    conflict: Hash

    @classmethod
    def from_names(cls, names: Mapping[str, Hash]) -> "StudyIdentities":
        return cls(
            effect=names["study"],
            save_response=names["save-response"],
            saved=names["saved"],
            duplicate=names["duplicate"],
            conflict=names["conflict"],
        )


def receipt_of(trial: str, session: str, answer: str) -> str:
    digest = hashlib.sha256()
    for part in (SCHEMA, trial, session, answer):
        digest.update(part.encode("utf-8"))
        digest.update(b"\0")
    return digest.hexdigest()


def _validate(trial: str, session: str, answer: str) -> None:
    for label, value in (("trial", trial), ("session", session), ("answer", answer)):
        if not isinstance(value, str):
            raise HostFailure("refused_authority", "not_started", f"{label} must be Text")
    for label, value in (("trial", trial), ("session", session)):
        if not _TOKEN.match(value):
            raise HostFailure(
                "refused_authority", "not_started", f"{label} must be 1..{MAX_TOKEN_CHARS} characters of [A-Za-z0-9._-]"
            )
    if len(answer.encode("utf-8")) > MAX_ANSWER_BYTES:
        raise HostFailure("refused_authority", "not_started", f"answer exceeds {MAX_ANSWER_BYTES} bytes")
    if "\0" in answer:
        raise HostFailure("refused_authority", "not_started", "answer must not contain NUL")


class StudyStore:
    """Host-owned SQLite lifecycle for the study responses."""

    def __init__(self, path: str | os.PathLike[str]) -> None:
        self.path = os.fspath(path)
        self._connection = sqlite3.connect(self.path, isolation_level=None)
        self._connection.execute("PRAGMA journal_mode=WAL")
        self._connection.execute("PRAGMA synchronous=FULL")
        self._connection.execute(_DDL)

    def _execute(self, sql: str, parameters: tuple = ()) -> sqlite3.Cursor:
        """The one path every statement takes (tests fault-inject here)."""
        return self._connection.execute(sql, parameters)

    def close(self) -> None:
        self._connection.close()

    def __enter__(self) -> "StudyStore":
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()

    def count(self) -> int:
        return int(self._execute("SELECT COUNT(*) FROM responses").fetchone()[0])

    def save(self, trial: str, session: str, answer: str) -> tuple[str, str]:
        """Return ``("saved" | "duplicate", receipt)`` or ``("conflict", "")``.

        Raises :class:`HostFailure` with the exact frozen pair for every
        failure; never retries.
        """
        _validate(trial, session, answer)
        receipt = receipt_of(trial, session, answer)
        execute = self._execute
        try:
            execute("BEGIN IMMEDIATE")
        except sqlite3.Error as error:
            raise HostFailure("outside_failure", "failed", f"could not begin: {type(error).__name__}") from None
        try:
            row = execute(
                "SELECT answer, receipt FROM responses WHERE trial = ? AND session = ?", (trial, session)
            ).fetchone()
            if row is not None:
                execute("ROLLBACK")
                if row[0] == answer:
                    return ("duplicate", row[1])
                return ("conflict", "")
            ordinal = int(execute("SELECT COUNT(*) FROM responses").fetchone()[0]) + 1
            execute(
                "INSERT INTO responses (schema, trial, session, answer, receipt, ordinal) VALUES (?, ?, ?, ?, ?, ?)",
                (SCHEMA, trial, session, answer, receipt, ordinal),
            )
        except sqlite3.Error as error:
            try:
                execute("ROLLBACK")
            except sqlite3.Error:
                pass
            raise HostFailure("outside_failure", "failed", f"write failed: {type(error).__name__}") from None
        try:
            execute("COMMIT")
        except sqlite3.Error as error:
            # the commit may or may not have become durable; report exactly that
            raise HostFailure("completion_unknown", "unknown", f"commit outcome unknown: {type(error).__name__}") from None
        return ("saved", receipt)


def bind_study(registry: OperationRegistry, identities: StudyIdentities, store: StudyStore) -> None:
    """Bind ``save-response`` to ``store`` so Jacquard receives a ``SaveOutcome``."""

    def save_response(trial: str, session: str, answer: str) -> Constructor:
        outcome, receipt = store.save(trial, session, answer)
        if outcome == "saved":
            return Constructor(identities.saved, (receipt,))
        if outcome == "duplicate":
            return Constructor(identities.duplicate, (receipt,))
        return Constructor(identities.conflict, ())

    registry.bind(identities.effect, identities.save_response, save_response, name="study.save-response")
