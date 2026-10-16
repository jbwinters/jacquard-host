"""Exercise three applications against an installed jacquard-host distribution.

Run with JACQUARD_BIN and JACQUARD_PRELUDE from the pinned build. See README.
All data and fault injection are synthetic; no network access is used.
"""
from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import itertools
import json
import os
from pathlib import Path
import platform
import random
import statistics
import sys
import tempfile
import time

import jacquard_host
from jacquard_host.adapter import Host
from jacquard_host.identities import SignatureError, parse_signature
from jacquard_host.protocol.values import Constructor, Hash
from jacquard_host.registry import OperationRegistry

from inventory import Inventory
from support import Program


class Experience:
    def __init__(self, root: Path, binary: str, prelude: str):
        self.root = root
        self.programs = {name: Program(name, root, binary=binary, prelude=prelude) for name in ("fulfillment", "planner", "triage")}
        self.checks = []
        self.measurements = {}

    def check(self, name, call, *, kind="ok", code=None, tag=None):
        self.checks.append({"case": name, **call.readable})
        result = call.result
        assert (result.kind, result.primary_code) == (kind, code), (name, call.readable, result.stderr)
        if tag:
            assert call.readable["value"]["tag"] == tag, (name, call.readable)
        return call

    def fulfillment(self):
        p = self.programs["fulfillment"]
        v = p.values
        database = self.root / "inventory.sqlite"
        db = Inventory(database, p)
        def place(label, order_id, lines, **expected):
            return self.check(label, p.call("fulfillment.place", db.order(order_id, lines), registry=db.registry()), **expected)
        try:
            first = place("multi-line order", "o-1", [("sensor", 2), ("cable", 3)], tag="fulfilled")
            assert first.readable["requests"] == ["booking-status", "lookup-stock", "lookup-stock", "reserve-order", "publish-booking"]
            assert (db.count(), db.stock("sensor"), db.stock("cable"), len(db.published)) == (1, 18, 27, 1)
            again = place("idempotent repeat", "o-1", [("cable", 3), ("sensor", 2)], tag="duplicate-order")
            assert again.result.value.arguments == first.result.value.arguments
            assert again.readable["requests"] == ["booking-status"]
            assert (db.count(), len(db.published)) == (1, 1)
            place("idempotency conflict", "o-1", [("sensor", 1)], tag="order-rejected")
            place("unknown SKU", "o-unknown", [("no-such-sku", 1)], tag="order-rejected")
            place("short stock", "o-short", [("sensor", 19)], tag="order-rejected")
            for label, lines in (("empty order", []), ("zero units", [("sensor", 0)]), ("duplicate SKU", [("sensor", 1), ("sensor", 2)])):
                before = len(db.events)
                place(label, "o-invalid", lines, tag="order-rejected")
                assert len(db.events) == before, label
            before = db.count()
            for fault, code, terminal in (("refuse", "E1607", "host_failure"), ("wrong-type", "E1608", "diagnostic")):
                db.fault = fault
                failed = place(fault, "o-" + fault, [("sensor", 1)], kind="error", code=code)
                assert failed.result.terminal == terminal
                assert db.count() == before
            db.fault = "stock-changed"
            sensor_before = db.stock("sensor")
            place("stock changes after lookup", "o-race", [("sensor", 1), ("cable", 1)], tag="order-rejected")
            assert db.count() == before
            assert db.stock("sensor") == sensor_before and db.stock("cable") == 0
            db.fault = ""
            result = p.call("fulfillment.place", db.order("o-no-dispatch", [("sensor", 1)]), registry=db.registry(dispatch=False))
            self.check("unbound dispatch operation", result, kind="error", code="E1606")
            # Core does not provide an all-or-nothing invocation transaction:
            # earlier configured operations can run before an unbound root is reached.
            self.measurements["unbound_dispatch"] = {"bookings_before": before, "bookings_after": db.count(), "requests": result.readable["requests"]}
            before = db.count()
            db.fault = "publish-failed"
            failed = place("dispatch fails after durable reservation", "o-partial", [("sensor", 1)], kind="error", code="E1613")
            assert db.count() == before + 1 and failed.result.value is None
            receipt_in_frames = any(
                frame.get("kind") == "effect_ok" and frame.get("value", {}).get("identity") == v.constructors["booked"].hex
                for frame in failed.result.host_frames
            )
            assert receipt_in_frames
            db.fault = ""
            place("explicit repeat after partial completion", "o-partial", [("sensor", 1)], tag="duplicate-order")
            assert all(order != "o-partial" for order, _ in db.published)
            self.measurements["partial_completion"] = {"durable_booking": True, "receipt_in_error_value": False, "receipt_in_host_frames": receipt_in_frames, "repeat_repairs_dispatch": False}
            before = db.count()
            db.fault = "after-commit"
            events_before = len(db.events)
            failed = place("exception after commit", "o-unknown-completion", [("sensor", 1)], kind="error", code="E1612")
            assert failed.result.terminal == "completion_unknown" and db.count() == before + 1
            assert db.events[events_before:].count("reserve") == 1
            db.fault = ""
            place("explicit reconciliation after unknown completion", "o-unknown-completion", [("sensor", 1)], tag="duplicate-order")
            # Repeating a fulfilled order must work after its stock is exhausted.
            remaining = db.stock("sensor")
            place("consume remaining stock", "o-exhaust", [("sensor", remaining)], tag="fulfilled")
            place("repeat with exhausted stock", "o-exhaust", [("sensor", remaining)], tag="duplicate-order")
            # Slow synchronous handlers cannot be preempted by the carrier budget.
            db.lookup_delay = 0.3
            host = Host(p.store, command=p.command, env=p.env, deadline_seconds=0.15)
            slow = p.call("fulfillment.place", db.order("o-slow", [("cable", 1)]), registry=db.registry(), host=host)
            self.check("slow Python handler exceeds carrier deadline", slow, kind="carrier_loss")
            assert slow.result.host_evidence["reason"] == "deadline" and slow.elapsed_ms >= 300
            self.measurements["slow_handler"] = {"configured_deadline_ms": 150, "observed_ms": round(slow.elapsed_ms, 3)}
            db.lookup_delay = 0
            count = db.count()
        finally:
            db.close()
        reopened = Inventory(database, p)
        try:
            assert reopened.count() == count
            result = p.call("fulfillment.place", reopened.order("o-1", [("sensor", 2), ("cable", 3)]), registry=reopened.registry())
            self.check("restart recovers duplicate receipt", result, tag="duplicate-order")
            assert result.result.value.arguments == first.result.value.arguments
        finally:
            reopened.close()

    @staticmethod
    def optimum(jobs, capacities):
        best = 0
        for destinations in itertools.product(range(len(capacities) + 1), repeat=len(jobs)):
            loads = [0] * len(capacities)
            score = 0
            feasible = True
            for (_, effort, value, latest), slot in zip(jobs, destinations):
                if slot:
                    if slot > latest:
                        feasible = False
                        break
                    loads[slot - 1] += effort
                    score += value
            if feasible and all(load <= cap for load, cap in zip(loads, capacities)):
                best = max(best, score)
        return best

    def planner(self):
        p = self.programs["planner"]
        v = p.values
        rng = random.Random(42)
        problems = [([("big", 4, 9, 1), ("small-a", 2, 6, 1), ("small-b", 2, 6, 1)], [4]), ([], [0]), ([("late", 5, 10, 1)], [0, 9])]
        for _ in range(24):
            capacities = [rng.randrange(0, 6) for _ in range(rng.randrange(1, 4))]
            jobs = [(f"job-{i}", rng.randrange(1, 6), rng.randrange(0, 21), rng.randrange(1, len(capacities) + 1)) for i in range(rng.randrange(1, 7))]
            problems.append((jobs, capacities))
        for index, (jobs, capacities) in enumerate(problems):
            call = p.call("planner.plan", v.list(v.make("Job", *job) for job in jobs), v.list(capacities))
            self.check(f"planner independent oracle {index}", call, tag="planned")
            encoded = call.result.value.arguments[0]
            score, assignments = encoded.arguments
            assert score == self.optimum(jobs, capacities), (index, score, jobs, capacities)
            rows = [item.arguments for item in v.unlist(assignments)]
            assert len({name for name, _ in rows}) == len(rows)
            lookup = {job[0]: job for job in jobs}
            loads = [0] * len(capacities)
            actual_score = 0
            for name, slot in rows:
                _, effort, value, latest = lookup[name]
                assert 1 <= slot <= min(latest, len(capacities))
                loads[slot - 1] += effort
                actual_score += value
            assert actual_score == score and all(load <= cap for load, cap in zip(loads, capacities))
        for label, jobs, capacities in (("duplicate IDs", [("a", 1, 1, 1), ("a", 1, 2, 1)], [2]), ("negative capacity", [], [-1]), ("bounded search", [(str(i), 1, 1, 1) for i in range(7)], [3]), ("empty capacity", [], [])):
            self.check(label, p.call("planner.plan", v.list(v.make("Job", *job) for job in jobs), v.list(capacities)), tag="invalid-plan")
        self.measurements["planner_problems_checked_against_exhaustive_oracle"] = len(problems)
        ties = v.list([v.make("Job", "a", 1, 5, 1), v.make("Job", "b", 1, 5, 1)])
        first = self.check("planner equal-score choice", p.call("planner.plan", ties, v.list([1])), tag="planned")
        second = self.check("planner deterministic repeat", p.call("planner.plan", ties, v.list([1])), tag="planned")
        assert first.result.value == second.result.value and first.result.evidence == second.result.evidence

    @staticmethod
    def expected_triage(row):
        identity, service, severity, age, visible, owner = row
        if not identity or severity not in range(1, 6) or age < 0:
            return {"tag": "invalid-incident", "fields": [identity]}
        page = visible and severity >= 4
        reasons = (["unowned"] if owner is None else []) + (["ageing"] if age >= 60 else []) + (["customer-impact"] if page else [])
        return {"tag": "routed", "fields": [identity, owner if owner is not None else service + "-oncall", {"tag": "page-now" if page else "work-today" if age >= 60 or severity == 5 else "watch-only", "fields": []}, reasons]}

    def triage(self):
        p = self.programs["triage"]
        v = p.values
        policy = v.make("TriagePolicy", 4, 60)
        def incident(row):
            identity, service, severity, age, visible, owner = row
            return v.make("Incident", identity, service, severity, age, v.make("True" if visible else "False"), v.make("None") if owner is None else v.make("Some", owner))
        rows = [(f"inc-{i}", "api", severity, age, visible, owner) for i, (severity, age, visible, owner) in enumerate(itertools.product(range(1, 6), (0, 59, 60), (False, True), (None, "ops")))]
        rows += [("bad-severity", "api", 0, 0, False, None), ("bad-age", "api", 3, -1, True, None), ("", "api", 1, 0, False, None)]
        for start in range(0, len(rows), 10):
            part = rows[start:start + 10]
            call = self.check(f"triage matrix {start}:{start + len(part)}", p.call("triage.batch", policy, v.list(incident(row) for row in part)))
            assert call.readable["value"] == [self.expected_triage(row) for row in part]
        self.measurements["triage_rows_checked_against_python_oracle"] = len(rows)
        # Feed ordinary Python containers first, recording the raw API behavior.
        native = p.call("triage.batch", policy, [incident(rows[0])])
        self.check("native Python list is refused", native, kind="host_error")
        assert native.result.host_evidence["code"] == "E1604"
        try:
            native.result.diagnostics[0].summary
        except IndexError:
            self.measurements["readme_error_renderer"] = "diagnostics[0] raises IndexError for a host_error"
        else:
            raise AssertionError("update the failure-surface observation")
        absent = Host(p.store, command=[str(self.root / "no-such-worker")])
        try:
            p.call("triage.ready", host=absent)
        except FileNotFoundError:
            self.measurements["spawn_failure"] = "FileNotFoundError is raised instead of returning InvocationResult"
        else:
            raise AssertionError("expected an absent worker to raise")
        wrong_bool = v.make("Incident", "bool", "api", 5, 0, True, v.make("None"))
        native = p.call("triage.one", policy, wrong_bool)
        self.check("native Python bool is refused", native, kind="host_error")
        assert native.result.host_evidence["code"] == "E1604"
        wrong_constructor = Constructor(Hash("f" * 64))
        self.check("unknown constructor identity", p.call("triage.one", policy, wrong_constructor), kind="fatal", code="E1603")
        # Find the actual input list limit rather than assuming max_collection_items.
        sizes = []
        for count in range(0, 41):
            call = p.call("triage.count", v.list(range(count)))
            sizes.append({"items": count, **call.readable})
            if not call.result.ok:
                if call.result.kind == "host_error":
                    # 0.2 validates outgoing frame depth locally. The original
                    # 0.1 recording reached Core and received a fatal instead.
                    self.check("list depth boundary", call, kind="host_error")
                    assert call.result.host_evidence["code"] == "E1602"
                else:
                    self.check("list depth boundary", call, kind="fatal", code="E1602")
                break
            assert call.result.value == count
        assert not call.result.ok, "extend probe range to locate the ceiling"
        self.measurements["list_depth_probe"] = {"largest_successful_items": sizes[-2]["items"], "first_failure": sizes[-1]}
        # Rich values can hit the nesting ceiling sooner than a List Int.
        rich_sizes = []
        for count in range(10, 36):
            call = p.call("triage.batch", policy, v.list(incident(rows[i % len(rows)]) for i in range(count)))
            rich_sizes.append({"items": count, "kind": call.result.kind, "code": call.result.primary_code, "host_evidence": call.result.host_evidence})
            if not call.result.ok:
                self.check("rich batch output depth boundary", call, kind="error", code="E1602")
                break
            assert call.readable["value"] == [self.expected_triage(rows[i % len(rows)]) for i in range(count)]
        assert not call.result.ok, "extend the rich probe range to locate the ceiling"
        self.measurements["rich_list_depth_probe"] = {"largest_successful_items": rich_sizes[-2]["items"], "first_failure": {"items": rich_sizes[-1]["items"], **call.readable}}
        # Measure a real 24-row workload; compare output equality on every round.
        encoded = [incident(row) for row in rows[:24]]
        timings = {"one_item_per_worker": [], "batches_of_eight": []}
        for _ in range(3):
            start = time.perf_counter()
            individual = [p.call("triage.one", policy, item) for item in encoded]
            timings["one_item_per_worker"].append((time.perf_counter() - start) * 1000)
            assert all(call.result.ok for call in individual)
            start = time.perf_counter()
            batches = [p.call("triage.batch", policy, v.list(encoded[i:i + 8])) for i in range(0, len(encoded), 8)]
            timings["batches_of_eight"].append((time.perf_counter() - start) * 1000)
            assert all(call.result.ok for call in batches)
            assert [call.readable["value"] for call in individual] == [row for call in batches for row in call.readable["value"]]
        controls = [p.call("triage.ready").elapsed_ms for _ in range(10)]
        self.measurements["batch_benchmark"] = {name: {"runs_ms": [round(t, 3) for t in values], "median_ms": round(statistics.median(values), 3)} for name, values in timings.items()}
        self.measurements["empty_worker_median_ms"] = round(statistics.median(controls), 3)
        # A registry reused across unrelated terms is not automatically narrowed.
        registry = OperationRegistry()
        f = self.programs["fulfillment"].values
        registry.bind(f.names["inventory"], f.names["lookup-stock"], lambda _: None)
        self.check("registry from another term", p.call("triage.ready", registry=registry), kind="host_error")
        self.measurements["signature_parser"] = {}
        for signature in ("(Int,) ->{} Int", "((Int,)) ->{} Int"):
            try:
                self.measurements["signature_parser"][signature] = {"parsed": repr(parse_signature(signature, v.names))}
            except SignatureError as error:
                self.measurements["signature_parser"][signature] = {"error": str(error)}

    def report(self):
        return {
            "schema": "jacquard-host-python-project-experiments-v1",
            "date": "2026-10-16", "python": platform.python_version(), "platform": platform.platform(),
            "library_version": importlib.metadata.version("jacquard-host"),
            "library_path": str(Path(jacquard_host.__file__).resolve()),
            "library_adapter_sha256": hashlib.sha256(Path(jacquard_host.__file__).with_name("adapter.py").read_bytes()).hexdigest(),
            "core_binary": self.programs["triage"].command[0],
            "core_binary_sha256": hashlib.sha256(Path(self.programs["triage"].command[0]).read_bytes()).hexdigest(),
            "source_sha256": {path.parent.name + ".jac": hashlib.sha256(path.read_bytes()).hexdigest() for path in sorted((Path(__file__).resolve().parents[3] / "examples").glob("*/rules.jac"))},
            "python_source_sha256": {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in sorted(Path(__file__).parent.glob("*.py"))},
            "setup_ms": {name: round(p.setup_ms, 3) for name, p in self.programs.items()},
            "checks": self.checks, "measurements": self.measurements,
        }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--jacquard", default=os.environ.get("JACQUARD_BIN"))
    parser.add_argument("--prelude", default=os.environ.get("JACQUARD_PRELUDE"))
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    if not args.jacquard or not args.prelude:
        parser.error("set JACQUARD_BIN and JACQUARD_PRELUDE, or pass --jacquard and --prelude")
    # Prevent an accidentally editable/source-tree import from masquerading as
    # evidence about the installed distribution.
    if Path(jacquard_host.__file__).resolve().is_relative_to(Path(__file__).resolve().parents[3] / "src"):
        parser.error("use an installed library, not PYTHONPATH=src")
    args.report.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="python-projects-", dir=args.report.parent) as temporary:
        experience = Experience(Path(temporary), str(Path(args.jacquard).resolve()), str(Path(args.prelude).resolve()))
        for name in ("fulfillment", "planner", "triage"):
            getattr(experience, name)()
            print(f"{name}: passed", flush=True)
        report = experience.report()
        args.report.write_text(json.dumps(report, indent=2) + "\n")
        print(f"{len(report['checks'])} named cases pass; full report: {args.report}")
        print(json.dumps(report["measurements"], indent=2))


if __name__ == "__main__":
    main()
