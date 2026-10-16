"""Native Python clients for the planner, triage, and fulfillment examples."""

from __future__ import annotations

import argparse
from dataclasses import dataclass, fields, is_dataclass
from enum import Enum
import hashlib
import json
from pathlib import Path
import sqlite3

from jacquard_host import JacquardError, Runtime, Variant
from jacquard_host.registry import HostFailure

SOURCES = Path(__file__).resolve().parents[1]


@dataclass(frozen=True)
class Line:
    sku: str
    units: int


@dataclass(frozen=True)
class Order:
    id: str
    lines: list[Line]


@dataclass(frozen=True)
class Fulfilled:
    receipt: str


@dataclass(frozen=True)
class DuplicateOrder:
    receipt: str


@dataclass(frozen=True)
class OrderRejected:
    reason: str


@dataclass(frozen=True)
class Job:
    id: str
    effort: int
    value: int
    latest: int


@dataclass(frozen=True)
class Assignment:
    id: str
    slot: int


@dataclass(frozen=True)
class Plan:
    score: int
    assignments: list[Assignment]


@dataclass(frozen=True)
class Planned:
    plan: Plan


@dataclass(frozen=True)
class InvalidPlan:
    reason: str


@dataclass(frozen=True)
class TriagePolicy:
    page: int
    stale: int


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


@dataclass(frozen=True)
class InvalidIncident:
    id: str


RECORDS = {
    "fulfillment": [Line, Order, Fulfilled, DuplicateOrder, OrderRejected],
    "planner": [Job, Assignment, Plan, Planned, InvalidPlan],
    "triage": [TriagePolicy, Incident, Routed, InvalidIncident],
}


def prepare(runtime: Runtime, project: str, workdir: Path):
    program = runtime.prepare(SOURCES / project / "rules.jac", cache_dir=workdir / "programs")
    for record in RECORDS[project]:
        program.data.record(record)
    if project == "triage":
        program.data.enum(Urgency)
    return program


class Warehouse:
    """Named synthetic operations with one transaction for booking + outbox.

    Deliveries are a SQLite simulation of a sink that deduplicates by receipt.
    A real queue needs its own reviewed idempotency and reconciliation contract.
    """

    def __init__(self, path: Path, program) -> None:
        self.data = program.data
        self.fault = ""
        self.calls = []
        self.db = sqlite3.connect(path, isolation_level=None, timeout=0.1)
        self.db.executescript("""
            CREATE TABLE IF NOT EXISTS stock(sku TEXT PRIMARY KEY, available INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS bookings(id TEXT PRIMARY KEY, payload TEXT NOT NULL, receipt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS outbox(receipt TEXT PRIMARY KEY, order_id TEXT NOT NULL, delivered INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS deliveries(receipt TEXT PRIMARY KEY, order_id TEXT NOT NULL);
            INSERT OR IGNORE INTO stock VALUES('sensor',20),('cable',30),('battery',15);
        """)

    def close(self):
        self.db.close()

    @staticmethod
    def _payload(purchase: Order) -> str:
        return json.dumps(sorted((line.sku, line.units) for line in purchase.lines), separators=(",", ":"))

    def booking_status(self, purchase: Order) -> Variant:
        self.calls.append("status")
        row = self.db.execute("SELECT payload,receipt FROM bookings WHERE id=?", (purchase.id,)).fetchone()
        if row is None:
            return self.data.BookingPending()
        return self.data.RepeatBooking(row[1]) if row[0] == self._payload(purchase) else self.data.BookingConflict()

    def lookup_stock(self, sku: str) -> Variant:
        self.calls.append("lookup")
        row = self.db.execute("SELECT available FROM stock WHERE sku=?", (sku,)).fetchone()
        return self.data.Stock(row[0]) if row else self.data.UnknownSku()

    def reserve_order(self, purchase: Order) -> Variant:
        self.calls.append("reserve")
        if not purchase.id or not purchase.lines or len(purchase.lines) > 20 or len({line.sku for line in purchase.lines}) != len(purchase.lines) or any(not 1 <= line.units <= 20 for line in purchase.lines):
            raise HostFailure("refused_authority", "not_started", "invalid synthetic reservation")
        payload = self._payload(purchase)
        receipt = hashlib.sha256((purchase.id + "\0" + payload).encode()).hexdigest()
        try:
            self.db.execute("BEGIN IMMEDIATE")
            previous = self.db.execute("SELECT payload,receipt FROM bookings WHERE id=?", (purchase.id,)).fetchone()
            if previous:
                self.db.execute("ROLLBACK")
                return self.data.RepeatBooking(previous[1]) if previous[0] == payload else self.data.BookingConflict()
            for line in purchase.lines:
                changed = self.db.execute("UPDATE stock SET available=available-? WHERE sku=? AND available>=?", (line.units, line.sku, line.units))
                if changed.rowcount != 1:
                    self.db.execute("ROLLBACK")
                    return self.data.StockChanged()
            self.db.execute("INSERT INTO bookings VALUES(?,?,?)", (purchase.id, payload, receipt))
            self.db.execute("INSERT INTO outbox VALUES(?,?,0)", (receipt, purchase.id))
            self.db.execute("COMMIT")
        except sqlite3.Error as error:
            if self.db.in_transaction:
                self.db.execute("ROLLBACK")
                raise HostFailure("outside_failure", "failed", "booking and outbox transaction rolled back") from error
            raise HostFailure("completion_unknown", "unknown", "booking and outbox durability unknown") from error
        if self.fault == "after-commit":
            raise RuntimeError("synthetic failure after reservation commit")
        return self.data.Booked(receipt)

    def publish_booking(self, order_id: str, receipt: str) -> tuple:
        self.calls.append("publish")
        if self.fault == "publish-failed":
            raise HostFailure("outside_failure", "failed", "synthetic delivery unavailable; outbox retained")
        row = self.db.execute("SELECT order_id FROM outbox WHERE receipt=?", (receipt,)).fetchone()
        if row != (order_id,):
            raise HostFailure("refused_authority", "not_started", "delivery has no matching outbox entry")
        try:
            self.db.execute("BEGIN IMMEDIATE")
            self.db.execute("INSERT OR IGNORE INTO deliveries VALUES(?,?)", (receipt, order_id))
            self.db.execute("UPDATE outbox SET delivered=1 WHERE receipt=?", (receipt,))
            self.db.execute("COMMIT")
        except sqlite3.Error as error:
            if self.db.in_transaction:
                self.db.execute("ROLLBACK")
                raise HostFailure("outside_failure", "failed", "simulated delivery rolled back") from error
            raise HostFailure("completion_unknown", "unknown", "simulated delivery durability unknown") from error
        if self.fault == "after-delivery":
            raise RuntimeError("synthetic failure after delivery commit")
        return ()

    def deliver_pending(self) -> int:
        """Explicit operator reconciliation; never run automatically after failure."""
        rows = self.db.execute("SELECT order_id,receipt FROM outbox WHERE delivered=0 ORDER BY receipt").fetchall()
        for order_id, receipt in rows:
            self.publish_booking(order_id, receipt)
        return len(rows)

    def state(self):
        return {"bookings": self.db.execute("SELECT COUNT(*) FROM bookings").fetchone()[0],
                "pending_deliveries": self.db.execute("SELECT COUNT(*) FROM outbox WHERE delivered=0").fetchone()[0],
                "deliveries": self.db.execute("SELECT COUNT(*) FROM deliveries").fetchone()[0]}


def json_value(value):
    if isinstance(value, Enum):
        return value.value
    if is_dataclass(value):
        return {"type": type(value).__name__, **{field.name: getattr(value, field.name) for field in fields(value)}}
    raise TypeError(type(value).__name__)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("project", choices=RECORDS)
    parser.add_argument("input", type=Path, nargs="?")
    parser.add_argument("--workdir", type=Path, default=Path(".scratch/python-sdk"))
    parser.add_argument("--inspect", type=Path, help="explicitly save raw protocol frames")
    parser.add_argument("--fault", choices=("", "after-commit", "publish-failed", "after-delivery"), default="")
    parser.add_argument("--deliver-pending", action="store_true")
    args = parser.parse_args()
    if args.input is None and not (args.project == "fulfillment" and args.deliver_pending):
        parser.error("pass an input JSON file, or use fulfillment --deliver-pending")
    warehouse = None
    try:
        program = prepare(Runtime(), args.project, args.workdir)
        if args.project == "fulfillment":
            warehouse = Warehouse(args.workdir / "inventory.sqlite", program)
            warehouse.fault = args.fault
            function = program.function("fulfillment.place", effects={"Inventory": warehouse, "Dispatch": warehouse})
            if args.deliver_pending:
                print(json.dumps({"delivered": warehouse.deliver_pending(), **warehouse.state()}))
                return 0
        payload = json.loads(args.input.read_text())
        if args.project == "fulfillment":
            run = function.invoke(Order(payload["id"], [Line(**line) for line in payload["lines"]]))
        elif args.project == "planner":
            run = program.function("planner.plan").invoke([Job(**row) for row in payload["jobs"]], payload["capacities"])
        else:
            function = program.function("triage.batch")
            policy = TriagePolicy(**payload["policy"])
            rows = [Incident(**row) for row in payload["incidents"]]
            if args.inspect:
                result = []
                for index, start in enumerate(range(0, len(rows), 8)):
                    run = function.invoke(policy, rows[start:start + 8])
                    run.save(args.inspect.with_name(f"{args.inspect.stem}.{index}{args.inspect.suffix or '.json'}"))
                    result.extend(run.unwrap())
            else:
                result = function.map_batches(policy, rows)
            print(json.dumps(result, default=json_value, indent=2))
            return 0
        if args.inspect:
            run.save(args.inspect)
        value = run.unwrap()
        print(run.render())
        print(json.dumps(value, default=json_value, indent=2))
        return 0
    except JacquardError as error:
        print(str(error))
        return 1
    except HostFailure as error:
        print(f"{error.category}/{error.completion}: {error.message}")
        return 1
    except (OSError, ValueError, TypeError, KeyError, sqlite3.Error) as error:
        print(f"Application input or local storage could not be used: {error}")
        return 2
    finally:
        if warehouse is not None:
            print(json.dumps(warehouse.state()))
            warehouse.close()


if __name__ == "__main__":
    raise SystemExit(main())
