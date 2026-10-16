"""Application-level SDK checks through the pinned real Core worker."""

from dataclasses import dataclass
from enum import Enum
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
from unittest.mock import patch

from jacquard_host import BindingError, DeadlineExceeded, InvalidArgument, InvocationError, OperationFailed, PreparationError, Runtime, WorkerLost
from jacquard_host.sdk import Program

BIN = os.environ.get("JACQUARD_BIN")
PRELUDE = os.environ.get("JACQUARD_PRELUDE")
EXAMPLES = Path(__file__).resolve().parents[1] / "examples"


@dataclass(frozen=True)
class Incident:
    id: str
    service: str
    severity: int
    age: int
    visible: bool
    owner: str | None


class Urgency(Enum):
    PAGE = "PageNow"
    TODAY = "WorkToday"
    WATCH = "WatchOnly"


@dataclass(frozen=True)
class Routed:
    id: str
    team: str
    urgency: Urgency
    reasons: list[str]


@unittest.skipUnless(BIN and PRELUDE, "set JACQUARD_BIN and JACQUARD_PRELUDE to exercise the SDK")
class SDKLiveTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.scratch = tempfile.TemporaryDirectory(prefix="sdk-live-")
        cls.root = Path(cls.scratch.name)
        cls.runtime = Runtime(BIN, prelude=PRELUDE)
        cls.triage = cls.runtime.prepare(EXAMPLES / "triage" / "rules.jac", cache_dir=cls.root / "cache")
        cls.fulfillment = cls.runtime.prepare(EXAMPLES / "fulfillment" / "rules.jac", cache_dir=cls.root / "cache")
        cls.triage.data.record(Incident)
        cls.triage.data.enum(Urgency)
        cls.triage.data.record(Routed)

    @classmethod
    def tearDownClass(cls):
        cls.scratch.cleanup()

    def test_native_dataclasses_enums_and_optional_fields(self):
        p = self.triage
        rows = [Incident("a", "api", 5, 90, True, None), Incident("b", "search", 2, 0, False, "search")]
        result = p.function("triage.batch")(p.data.TriagePolicy(4, 60), rows)
        self.assertEqual(result, [Routed("a", "api-oncall", Urgency.PAGE, ["unowned", "ageing", "customer-impact"]), Routed("b", "search", Urgency.WATCH, [])])

    def test_invalid_nested_field_and_large_input_never_start_a_worker(self):
        p = self.triage
        with patch.object(p.host, "invoke") as invoke:
            with self.assertRaisesRegex(InvalidArgument, "age: expected int"):
                p.function("triage.one")(p.data.TriagePolicy(4, 60), Incident("bad", "api", 5, "old", True, None))
            with self.assertRaisesRegex(InvalidArgument, "smaller batches"):
                p.function("triage.count")(list(range(31)))
            invoke.assert_not_called()

    def test_map_batches_uses_native_lists_and_validates_late_inputs_first(self):
        p = self.triage
        function = p.function("triage.batch")
        rows = [Incident(str(i), "api", 1, 0, False, None) for i in range(64)]
        result = function.map_batches(p.data.TriagePolicy(4, 60), rows)
        self.assertEqual([item.id for item in result], [str(i) for i in range(64)])
        with patch.object(p.host, "invoke") as invoke:
            with self.assertRaises(InvalidArgument):
                function.map_batches(p.data.TriagePolicy(4, 60), rows + [{"bad": "row"}])
            with self.assertRaises(InvalidArgument):
                function.map_batches([])
            invoke.assert_not_called()
        self.assertEqual(function.map_batches(p.data.TriagePolicy(4, 60), []), [])

    def test_oversized_output_is_reported_without_adaptive_retry(self):
        p = self.triage
        rows = [Incident(str(i), "api", 5, 90, True, None) for i in range(60)]
        with patch.object(p.host, "invoke", wraps=p.host.invoke) as invoke:
            with self.assertRaises(InvocationError) as caught:
                p.function("triage.batch").map_batches(p.data.TriagePolicy(4, 60), rows, batch_size=29)
            self.assertEqual(caught.exception.code, "E1602")
            self.assertEqual(caught.exception.batch_index, 0)
            self.assertEqual(caught.exception.completed_values, [])
            self.assertEqual(invoke.call_count, 1)

    def test_missing_later_operation_is_refused_before_any_outside_action(self):
        p = self.fulfillment
        events = []
        inventory = {"booking-status": lambda _: events.append("status"), "lookup-stock": lambda _: None, "reserve-order": lambda _: None}
        with patch.object(p.host, "invoke") as invoke:
            with self.assertRaisesRegex(BindingError, "dispatch.publish_booking"):
                p.function("fulfillment.place", effects={"Inventory": inventory})
            invoke.assert_not_called()
        self.assertEqual(events, [])

    def test_global_services_are_scoped_away_from_pure_terms(self):
        p = self.runtime.prepare(EXAMPLES / "fulfillment" / "rules.jac", cache_dir=self.root / "cache")
        # Shared services are consumed only where the checked effect row asks
        # for them; unrelated global entries do not enter the registry.
        p.provide(Inventory=object(), Dispatch=object())
        self.assertTrue(p.function("order.valid-lines")([], []))

    def test_async_handlers_are_rejected_during_binding(self):
        p = self.fulfillment
        async def publish(*args):
            return ()
        inventory = {"booking-status": lambda _: p.data.BookingPending(), "lookup-stock": lambda _: p.data.Stock(5), "reserve-order": lambda _: p.data.Booked("receipt")}
        with self.assertRaisesRegex(BindingError, "async handlers"):
            p.function("fulfillment.place", effects={"inventory": inventory, "dispatch": {"publish-booking": publish}})

    def test_handler_failure_and_no_automatic_retry(self):
        p = self.fulfillment
        events = []
        def reserve(_):
            events.append("reserve")
            raise RuntimeError("synthetic post-commit failure")
        function = p.function("fulfillment.place", effects={
            "Inventory": {"booking-status": lambda _: p.data.BookingPending(), "lookup-stock": lambda _: p.data.Stock(5), "reserve-order": reserve},
            "Dispatch": {"publish-booking": lambda *_: ()},
        })
        with self.assertRaises(OperationFailed) as caught:
            function(p.data.Order("a", [p.data.Line("sensor", 1)]))
        self.assertEqual(caught.exception.code, "E1612")
        self.assertEqual(events, ["reserve"])
        self.assertEqual(len(caught.exception.result.requests), 3)
        with self.assertRaises(BindingError):
            function.map_batches([])

    def test_inspection_retains_raw_frames_and_saves_without_new_core_fields(self):
        run = self.triage.function("triage.count").invoke([1, 2, 3])
        self.assertEqual(run.unwrap(), 3)
        self.assertEqual(run.raw.value, 3)
        self.assertIn("triage.count: ok", run.render())
        destination = self.root / "inspection.json"
        run.save(destination)
        document = json.loads(destination.read_text())
        self.assertEqual(document["core_frames"], run.raw.core_frames)
        self.assertEqual(document["host_frames"], run.raw.host_frames)

    def test_cached_prepare_does_not_run_hash_or_check_commands(self):
        with patch("jacquard_host.sdk.hash_members") as hashes, patch("jacquard_host.sdk.signatures") as check:
            cached = self.runtime.prepare(EXAMPLES / "triage" / "rules.jac", cache_dir=self.root / "cache")
            hashes.assert_not_called()
            check.assert_not_called()
        self.assertEqual(cached.directory, self.triage.directory)
        self.assertEqual(cached.function("triage.count")([1]), 1)

    def test_source_change_gets_a_new_cache_and_corruption_is_refused(self):
        source = self.root / "changed.jac"
        source.write_bytes((EXAMPLES / "triage" / "rules.jac").read_bytes().replace(b"\n", b"\r\n") + b"\r\n-- changed source\r\n")
        changed = self.runtime.prepare(source, cache_dir=self.root / "cache")
        self.assertNotEqual(changed.directory, self.triage.directory)
        self.assertEqual((changed.directory / "source.jac").read_bytes(), source.read_bytes())
        (changed.directory / "store" / "names.jqd").write_text("broken")
        with self.assertRaisesRegex(PreparationError, "incomplete or changed"):
            self.runtime.prepare(source, cache_dir=self.root / "cache")

    def test_spawn_and_deadline_errors_share_the_same_result_interface(self):
        p = self.triage
        function = p.function("triage.ready")
        with patch.object(p.host, "command", (str(self.root / "absent-worker"),)):
            run = function.invoke()
            self.assertEqual((run.ok, run.code), (False, "E1611"))
            with self.assertRaises(WorkerLost) as caught:
                run.unwrap()
            self.assertIn("Could not start", str(caught.exception))
        with patch.object(p.host, "command", (sys.executable, "-c", "import time;time.sleep(2)")), patch.object(p.host, "deadline_seconds", 0.1):
            with self.assertRaises(DeadlineExceeded) as caught:
                function()
            self.assertFalse(caught.exception.result.diagnostics)

    def test_optional_handler_annotations_work_through_core(self):
        source = self.root / "lookup.jac"
        source.write_text("once effect Lookup where { find-owner : (Text) -> Option Text }\nresolve : (Text) ->{Lookup} Option Text\nresolve(key) = find-owner(key)\n")
        program = self.runtime.prepare(source, cache_dir=self.root / "cache")
        def find_owner(key: str) -> str | None:
            return "ops" if key == "known" else None
        resolve = program.function("resolve", effects={"Lookup": {"find-owner": find_owner}})
        self.assertEqual(resolve("known"), "ops")
        self.assertIsNone(resolve("missing"))

    def test_all_declared_operations_are_required_even_if_a_path_does_not_use_them(self):
        source = self.root / "conservative.jac"
        source.write_text("once effect Counter where { one : () -> Int; unused : () -> Int }\ncount-one : () ->{Counter} Int\ncount-one() = one()\n")
        program = self.runtime.prepare(source, cache_dir=self.root / "cache")
        with self.assertRaisesRegex(BindingError, "counter.unused"):
            program.function("count-one", effects={"Counter": {"one": lambda: 1}})

    def test_ambiguous_public_kinds_are_refused_instead_of_guessed(self):
        source = self.root / "ambiguous.jac"
        source.write_text("once effect First where { read-first : () -> Int }\nfirst : () ->{First} Int\nfirst() = read-first()\n")
        with self.assertRaisesRegex(BindingError, "Public name 'first'"):
            self.runtime.prepare(source, cache_dir=self.root / "cache")

    def test_output_mapping_failure_keeps_the_completed_raw_result(self):
        program = self.runtime.prepare(EXAMPLES / "triage" / "rules.jac", cache_dir=self.root / "cache")
        @program.data.record(constructor="Routed")
        @dataclass(frozen=True)
        class WrongLayout:
            only_one_field: str
        function = program.function("triage.one")
        with patch.object(program.host, "invoke", wraps=program.host.invoke) as invoke:
            with self.assertRaisesRegex(BindingError, "Core completed") as caught:
                function(program.data.TriagePolicy(4, 60), program.data.Incident("a", "api", 5, 90, True, None))
            self.assertTrue(caught.exception.result.ok)
            self.assertEqual(invoke.call_count, 1)


if __name__ == "__main__":
    unittest.main()
