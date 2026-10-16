"""The experiment projects, now using native data and complete SDK services."""

from dataclasses import replace
import importlib.util
import itertools
import os
from pathlib import Path
import random
import sys
import tempfile
import unittest

from jacquard_host import BindingError, InvalidArgument, OperationFailed, Runtime

BIN, PRELUDE = os.environ.get("JACQUARD_BIN"), os.environ.get("JACQUARD_PRELUDE")
APP = Path(__file__).resolve().parents[1] / "examples" / "python_sdk" / "app.py"
spec = importlib.util.spec_from_file_location("sdk_example_app", APP)
app = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = app
spec.loader.exec_module(app)


def optimum(jobs, capacities):
    best = 0
    for destinations in itertools.product(range(len(capacities) + 1), repeat=len(jobs)):
        loads, score = [0] * len(capacities), 0
        for job, slot in zip(jobs, destinations):
            if slot:
                if slot > job.latest:
                    break
                loads[slot - 1] += job.effort
                score += job.value
        else:
            if all(load <= capacity for load, capacity in zip(loads, capacities)):
                best = max(best, score)
    return best


@unittest.skipUnless(BIN and PRELUDE, "set JACQUARD_BIN and JACQUARD_PRELUDE for the SDK project acceptance tests")
class SDKProjectsLiveTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.scratch = tempfile.TemporaryDirectory(prefix="sdk-projects-")
        cls.root = Path(cls.scratch.name)
        cls.runtime = Runtime(BIN, prelude=PRELUDE)
        cls.programs = {name: app.prepare(cls.runtime, name, cls.root) for name in app.RECORDS}

    @classmethod
    def tearDownClass(cls):
        cls.scratch.cleanup()

    def test_native_inventory_receipt_and_restart(self):
        program = self.programs["fulfillment"]
        path = self.root / "happy.sqlite"
        store = app.Warehouse(path, program)
        purchase = app.Order("one", [app.Line("sensor", 2), app.Line("cable", 3)])
        try:
            function = program.function("fulfillment.place", effects={"Inventory": store, "Dispatch": store})
            accepted = function(purchase)
            self.assertIsInstance(accepted, app.Fulfilled)
            self.assertEqual(store.state(), {"bookings": 1, "pending_deliveries": 0, "deliveries": 1})
            duplicate = function(replace(purchase, lines=list(reversed(purchase.lines))))
            self.assertEqual(duplicate, app.DuplicateOrder(accepted.receipt))
            conflict = function(replace(purchase, lines=[app.Line("sensor", 3)]))
            self.assertEqual(conflict, app.OrderRejected("idempotency conflict"))
        finally:
            store.close()
        restarted = app.Warehouse(path, program)
        try:
            function = program.function("fulfillment.place", effects={"Inventory": restarted, "Dispatch": restarted})
            self.assertEqual(function(purchase), app.DuplicateOrder(accepted.receipt))
            self.assertEqual(restarted.deliver_pending(), 0)
        finally:
            restarted.close()

    def test_outbox_survives_every_partial_completion_and_explicit_reconciliation(self):
        program = self.programs["fulfillment"]
        for fault, code, delivered in (("after-commit", "E1612", 0), ("publish-failed", "E1613", 0), ("after-delivery", "E1612", 1)):
            with self.subTest(fault=fault):
                store = app.Warehouse(self.root / (fault + ".sqlite"), program)
                try:
                    function = program.function("fulfillment.place", effects={"Inventory": store, "Dispatch": store})
                    store.fault = fault
                    purchase = app.Order(fault, [app.Line("sensor", 1)])
                    with self.assertRaises(OperationFailed) as caught:
                        function(purchase)
                    self.assertEqual(caught.exception.code, code)
                    self.assertEqual(store.calls.count("reserve"), 1)
                    self.assertEqual(store.state(), {"bookings": 1, "pending_deliveries": 1 - delivered, "deliveries": delivered})
                    store.fault = ""
                    self.assertIsInstance(function(purchase), app.DuplicateOrder)
                    # No callback is retried automatically. The operator chooses
                    # reconciliation, under the sink's receipt-dedup contract.
                    self.assertEqual(store.deliver_pending(), 1 - delivered)
                    self.assertEqual(store.deliver_pending(), 0)
                    self.assertEqual(store.state(), {"bookings": 1, "pending_deliveries": 0, "deliveries": 1})
                finally:
                    store.close()

    def test_missing_service_cannot_reserve_and_bad_fields_have_names(self):
        program = self.programs["fulfillment"]
        store = app.Warehouse(self.root / "missing.sqlite", program)
        try:
            with self.assertRaisesRegex(BindingError, "dispatch.publish_booking"):
                program.function("fulfillment.place", effects={"Inventory": store})
            self.assertEqual(store.state()["bookings"], 0)
            function = program.function("fulfillment.place", effects={"Inventory": store, "Dispatch": store})
            with self.assertRaisesRegex(InvalidArgument, r"lines\[0\].units"):
                function(app.Order("wrong", [app.Line("sensor", True)]))
            self.assertEqual(store.calls, [])
        finally:
            store.close()

    def test_planner_matches_an_independent_exhaustive_oracle(self):
        planner = self.programs["planner"].function("planner.plan")
        rng = random.Random(42)
        problems = [([app.Job("big", 4, 9, 1), app.Job("a", 2, 6, 1), app.Job("b", 2, 6, 1)], [4])]
        for _ in range(24):
            capacities = [rng.randrange(0, 6) for _ in range(rng.randrange(1, 4))]
            jobs = [app.Job(str(i), rng.randrange(1, 6), rng.randrange(0, 21), rng.randrange(1, len(capacities) + 1)) for i in range(rng.randrange(1, 7))]
            problems.append((jobs, capacities))
        for jobs, capacities in problems:
            with self.subTest(jobs=jobs, capacities=capacities):
                report = planner(jobs, capacities)
                self.assertIsInstance(report, app.Planned)
                self.assertEqual(report.plan.score, optimum(jobs, capacities))
                by_id = {job.id: job for job in jobs}
                loads = [0] * len(capacities)
                self.assertEqual(len({item.id for item in report.plan.assignments}), len(report.plan.assignments))
                for assignment in report.plan.assignments:
                    task = by_id[assignment.id]
                    self.assertTrue(1 <= assignment.slot <= min(task.latest, len(capacities)))
                    loads[assignment.slot - 1] += task.effort
                self.assertTrue(all(load <= cap for load, cap in zip(loads, capacities)))
                self.assertEqual(sum(by_id[item.id].value for item in report.plan.assignments), report.plan.score)

    def test_triage_matrix_and_chunking_return_native_records(self):
        triage = self.programs["triage"].function("triage.batch")
        rows = [app.Incident(str(i), "api", severity, age, visible, owner) for i, (severity, age, visible, owner) in enumerate(itertools.product(range(1, 6), (0, 59, 60), (False, True), (None, "ops")))]
        results = triage.map_batches(app.TriagePolicy(4, 60), rows)
        for row, result in zip(rows, results):
            page = row.visible and row.severity >= 4
            expected = app.Urgency.PAGE if page else app.Urgency.TODAY if row.age >= 60 or row.severity == 5 else app.Urgency.WATCH
            reasons = (["unowned"] if row.owner is None else []) + (["ageing"] if row.age >= 60 else []) + (["customer-impact"] if page else [])
            self.assertEqual(result, app.Routed(row.id, row.owner or "api-oncall", expected, reasons))
        self.assertEqual(len(results), len(rows))


if __name__ == "__main__":
    unittest.main()
