"""The study persistence operation without Core."""

from __future__ import annotations

import os
import sqlite3
import tempfile
import unittest
from unittest import mock

from jacquard_host.protocol.envelopes import EffectRequest
from jacquard_host.protocol.values import Constructor, Hash
from jacquard_host.registry import HostFailure, OperationRegistry
from jacquard_host.study import MAX_ANSWER_BYTES, StudyIdentities, StudyStore, bind_study, receipt_of

IDS = StudyIdentities(effect=Hash("1" * 64), save_response=Hash("2" * 64), saved=Hash("3" * 64), duplicate=Hash("4" * 64), conflict=Hash("5" * 64))


def request(*arguments: object) -> EffectRequest:
    return EffectRequest(ordinal=1, effect=IDS.effect, operation=IDS.save_response, arguments=tuple(arguments), raw={})


class StudyStoreTest(unittest.TestCase):
    def setUp(self) -> None:
        self.scratch = tempfile.TemporaryDirectory(prefix="jacquard-host-study-")
        self.path = os.path.join(self.scratch.name, "study.sqlite")
        self.store = StudyStore(self.path)

    def tearDown(self) -> None:
        self.store.close()
        self.scratch.cleanup()

    def test_receipts_are_deterministic_and_content_bound(self) -> None:
        self.assertEqual(receipt_of("t1", "s1", "a"), receipt_of("t1", "s1", "a"))
        self.assertNotEqual(receipt_of("t1", "s1", "a"), receipt_of("t1", "s1", "b"))
        self.assertNotEqual(receipt_of("t1", "s1", "a"), receipt_of("t1s", "1", "a"))
        self.assertRegex(receipt_of("t1", "s1", "a"), r"^[0-9a-f]{64}$")

    def test_save_duplicate_and_conflict(self) -> None:
        outcome, receipt = self.store.save("trial-1", "session.A", "answer")
        self.assertEqual((outcome, receipt), ("saved", receipt_of("trial-1", "session.A", "answer")))
        self.assertEqual(self.store.save("trial-1", "session.A", "answer"), ("duplicate", receipt))
        self.assertEqual(self.store.save("trial-1", "session.A", "other"), ("conflict", ""))
        self.assertEqual(self.store.count(), 1)
        self.assertEqual(self.store.save("trial-1", "session.B", "other")[0], "saved")
        self.assertEqual(self.store.count(), 2)

    def test_invalid_inputs_are_refused_before_any_write(self) -> None:
        for trial, session, answer in (
            ("", "s", "a"),
            ("t", "", "a"),
            ("t/../x", "s", "a"),
            ("t", "s s", "a"),
            ("-t", "s", "a"),
            ("t" * 129, "s", "a"),
            ("t", "s", "x" * (MAX_ANSWER_BYTES + 1)),
            ("t", "s", "a\0b"),
            ("t\n", "s", "a"),
            ("t", "s\n", "a"),
            ("t\ns", "s", "a"),
        ):
            with self.subTest(trial=trial, session=session), self.assertRaises(HostFailure) as caught:
                self.store.save(trial, session, answer)
            self.assertEqual((caught.exception.category, caught.exception.completion), ("refused_authority", "not_started"))
        self.assertEqual(self.store.count(), 0)
        self.store.save("t", "s", "é" * (MAX_ANSWER_BYTES // 2))  # exactly at the byte limit

    def test_rows_survive_reopen_and_carry_no_timestamps(self) -> None:
        self.store.save("t", "s", "a")
        self.store.close()
        with StudyStore(self.path) as again:
            self.assertEqual(again.save("t", "s", "a"), ("duplicate", receipt_of("t", "s", "a")))
        with sqlite3.connect(self.path) as raw:
            columns = [row[1] for row in raw.execute("PRAGMA table_info(responses)")]
        self.assertEqual(columns, ["schema", "trial", "session", "answer", "receipt", "ordinal"])

    def test_write_failure_is_outside_failure_and_commit_failure_is_unknown(self) -> None:
        real_execute = self.store._execute

        def failing_insert(sql, *args):
            if sql.lstrip().startswith("INSERT"):
                raise sqlite3.OperationalError("disk I/O error")
            return real_execute(sql, *args)

        with mock.patch.object(self.store, "_execute", side_effect=failing_insert):
            with self.assertRaises(HostFailure) as caught:
                self.store.save("t", "s", "a")
        self.assertEqual((caught.exception.category, caught.exception.completion), ("outside_failure", "failed"))
        self.assertEqual(self.store.count(), 0)

        def failing_commit(sql, *args):
            if sql.lstrip().startswith("COMMIT"):
                raise sqlite3.OperationalError("disk I/O error")
            return real_execute(sql, *args)

        with mock.patch.object(self.store, "_execute", side_effect=failing_commit):
            with self.assertRaises(HostFailure) as caught:
                self.store.save("t", "s2", "a")
        # the transaction was still open when COMMIT failed: it is rolled back and the failure is definite
        self.assertEqual((caught.exception.category, caught.exception.completion), ("outside_failure", "failed"))
        self.assertFalse(self.store._connection.in_transaction)
        self.assertEqual(self.store.count(), 0)
        # the store and a second store on the same file remain usable
        self.assertEqual(self.store.save("t", "s2", "a")[0], "saved")
        with StudyStore(self.path) as other:
            self.assertEqual(other.save("t", "s3", "a")[0], "saved")

        def vanishing_commit(sql, *args):
            if sql.lstrip().startswith("COMMIT"):
                real_execute("ROLLBACK")  # the transaction disappears underneath the failed commit
                raise sqlite3.OperationalError("disk I/O error")
            return real_execute(sql, *args)

        with mock.patch.object(self.store, "_execute", side_effect=vanishing_commit):
            with self.assertRaises(HostFailure) as caught:
                self.store.save("t", "s4", "a")
        self.assertEqual((caught.exception.category, caught.exception.completion), ("completion_unknown", "unknown"))
        self.assertFalse(self.store._connection.in_transaction)
        self.assertEqual(self.store.save("t", "s4", "a")[0], "saved")

        def failing_rollback(sql, *args):
            if sql.lstrip().startswith("ROLLBACK"):
                raise sqlite3.OperationalError("cannot rollback")
            return real_execute(sql, *args)

        with mock.patch.object(self.store, "_execute", side_effect=failing_rollback):
            self.assertEqual(self.store.save("t", "s4", "a")[0], "duplicate")
        self.assertFalse(self.store._connection.in_transaction)
        self.assertEqual(self.store.save("t", "s5", "a")[0], "saved")

    def test_journal_mode_is_recorded_not_assumed(self) -> None:
        self.assertIn(self.store.journal_mode, ("wal", "delete", "truncate", "persist", "memory"))

    def test_a_store_can_be_used_from_another_thread(self) -> None:
        import threading
        outcome = []
        thread = threading.Thread(target=lambda: outcome.append(self.store.save("t", "thread", "a")[0]))
        thread.start()
        thread.join(5)
        self.assertEqual(outcome, ["saved"])

    def test_a_locked_file_fails_fast_and_writes_nothing(self) -> None:
        import time
        holder = sqlite3.connect(self.path, isolation_level=None)
        holder.execute("BEGIN IMMEDIATE")
        try:
            store = StudyStore(self.path, busy_timeout=0.2)
            started = time.monotonic()
            with self.assertRaises(HostFailure) as caught:
                store.save("t", "s", "a")
            self.assertLess(time.monotonic() - started, 2.0)
            self.assertEqual((caught.exception.category, caught.exception.completion), ("outside_failure", "failed"))
            store.close()
        finally:
            holder.execute("ROLLBACK")
            holder.close()
        self.assertEqual(self.store.count(), 0)

    def test_bound_operation_returns_save_outcome_constructors(self) -> None:
        registry = OperationRegistry()
        bind_study(registry, IDS, self.store)
        self.assertEqual(len(registry), 1)
        receipt = receipt_of("t", "s", "a")
        self.assertEqual(registry.dispatch(request("t", "s", "a")).value, Constructor(IDS.saved, (receipt,)))
        self.assertEqual(registry.dispatch(request("t", "s", "a")).value, Constructor(IDS.duplicate, (receipt,)))
        self.assertEqual(registry.dispatch(request("t", "s", "b")).value, Constructor(IDS.conflict, ()))
        failure = registry.dispatch(request("", "s", "b")).failure
        assert failure is not None
        self.assertEqual((failure.category, failure.completion), ("refused_authority", "not_started"))
        other = EffectRequest(ordinal=1, effect=IDS.effect, operation=Hash("9" * 64), arguments=("t", "s", "a"), raw={})
        self.assertEqual(registry.dispatch(other).failure.category, "unsupported_operation")


if __name__ == "__main__":
    unittest.main()
