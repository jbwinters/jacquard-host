"""Run the polished projects from an installed wheel and record UX evidence."""

from __future__ import annotations

import argparse
from dataclasses import replace
import hashlib
import json
from pathlib import Path
import statistics
import tempfile
import time

import jacquard_host
from jacquard_host import BindingError, InvalidArgument, OperationFailed, Runtime

from app import Assignment, DuplicateOrder, Fulfilled, Incident, Job, Line, Order, Planned, TriagePolicy, Warehouse, json_value, prepare


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    if Path(jacquard_host.__file__).resolve().is_relative_to(Path(__file__).resolve().parents[2] / "src"):
        parser.error("install the wheel first; this exercise must not import the source tree")
    args.report.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="sdk-exercise-", dir=args.report.parent) as temporary:
        root = Path(temporary)
        runtime = Runtime()
        programs, cold, warm = {}, {}, {}
        for name in ("fulfillment", "planner", "triage"):
            start = time.perf_counter()
            programs[name] = prepare(runtime, name, root)
            cold[name] = round((time.perf_counter() - start) * 1000, 3)
            durations = []
            for _ in range(5):
                start = time.perf_counter()
                repeated = prepare(runtime, name, root)
                durations.append((time.perf_counter() - start) * 1000)
                assert repeated.directory == programs[name].directory
            warm[name] = round(statistics.median(durations), 3)
        p = programs["fulfillment"]
        warehouse = Warehouse(root / "inventory.sqlite", p)
        outcomes = {}
        try:
            try:
                p.function("fulfillment.place", effects={"Inventory": warehouse})
            except BindingError as error:
                outcomes["missing_service"] = str(error)
                assert not warehouse.calls
            else:
                raise AssertionError("missing dispatch must fail before invocation")
            place = p.function("fulfillment.place", effects={"Inventory": warehouse, "Dispatch": warehouse})
            try:
                place(Order("bad", [Line("sensor", True)]))
            except InvalidArgument as error:
                outcomes["invalid_field"] = str(error)
                assert not warehouse.calls
            else:
                raise AssertionError("bool cannot be an Int")
            order = Order("one", [Line("sensor", 2), Line("cable", 3)])
            run = place.invoke(order)
            accepted = run.unwrap()
            assert isinstance(accepted, Fulfilled)
            assert place(replace(order, lines=list(reversed(order.lines)))) == DuplicateOrder(accepted.receipt)
            outcomes["success"] = accepted
            outcomes["inspection"] = run.render()
            for fault, code in (("after-commit", "E1612"), ("publish-failed", "E1613"), ("after-delivery", "E1612")):
                warehouse.fault = fault
                try:
                    place(replace(order, id=fault))
                except OperationFailed as error:
                    assert error.code == code
                    before = warehouse.state()
                    warehouse.fault = ""
                    delivered = warehouse.deliver_pending()
                    after = warehouse.state()
                    outcomes[fault] = {"code": error.code, "before": before, "explicit_deliveries": delivered, "after": after}
                    assert after["pending_deliveries"] == 0
                else:
                    raise AssertionError(f"fault {fault} must fail")
            assert warehouse.state()["deliveries"] == 4
        finally:
            warehouse.close()
        planner = programs["planner"].function("planner.plan")
        plan = planner([Job("large", 4, 9, 1), Job("a", 2, 6, 1), Job("b", 2, 6, 1), Job("docs", 3, 4, 2)], [4, 3])
        assert isinstance(plan, Planned) and plan.plan.score == 16
        assert set(plan.plan.assignments) == {Assignment("a", 1), Assignment("b", 1), Assignment("docs", 2)}
        outcomes["planner"] = plan
        triage = programs["triage"].function("triage.batch")
        one = programs["triage"].function("triage.one")
        policy = TriagePolicy(4, 60)
        rows = [Incident(str(i), "api", 5, i * 10, True, None) for i in range(64)]
        batched = triage.map_batches(policy, rows)
        assert [row.id for row in batched] == [row.id for row in rows]
        individual_times, batch_times = [], []
        for _ in range(3):
            start = time.perf_counter()
            individual = [one(policy, row) for row in rows[:24]]
            individual_times.append((time.perf_counter() - start) * 1000)
            start = time.perf_counter()
            batch = triage.map_batches(policy, rows[:24])
            batch_times.append((time.perf_counter() - start) * 1000)
            assert individual == batch
        report = {"schema": "jacquard-python-sdk-exercise-v1", "sdk_version": jacquard_host.__version__,
                  "library_path": str(Path(jacquard_host.__file__).resolve()),
                  "sdk_sha256": hashlib.sha256(Path(jacquard_host.__file__).with_name("sdk.py").read_bytes()).hexdigest(),
                  "data_sha256": hashlib.sha256(Path(jacquard_host.__file__).with_name("data.py").read_bytes()).hexdigest(),
                  "cold_prepare_ms": cold, "warm_prepare_median_ms": warm,
                  "individual_24_median_ms": round(statistics.median(individual_times), 3),
                  "batched_24_median_ms": round(statistics.median(batch_times), 3),
                  "native_triage_rows": len(batched), "outcomes": outcomes}
        args.report.write_text(json.dumps(report, default=json_value, indent=2) + "\n")
        print(json.dumps({key: value for key, value in report.items() if key != "outcomes"}, indent=2))
        print("All three installed SDK applications passed; no automatic retries.")


if __name__ == "__main__":
    main()
