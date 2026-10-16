"""Synthetic stock service behind two exact, configured host operations."""
from __future__ import annotations

import hashlib
import json
import sqlite3
import time

from jacquard_host.registry import HostFailure, OperationRegistry

from support import Program


class Inventory:
    def __init__(self, path, program: Program) -> None:
        self.program = program
        self.v = program.values
        self.db = sqlite3.connect(path, isolation_level=None, timeout=0.1)
        self.db.executescript("""
          CREATE TABLE IF NOT EXISTS stock(sku TEXT PRIMARY KEY, available INTEGER NOT NULL);
          CREATE TABLE IF NOT EXISTS bookings(id TEXT PRIMARY KEY, payload TEXT NOT NULL, receipt TEXT NOT NULL);
          INSERT OR IGNORE INTO stock VALUES('sensor', 20), ('cable', 30), ('battery', 15);
        """)
        self.events = []
        self.published = []
        self.fault = ""
        self.lookup_delay = 0.0

    def close(self) -> None:
        self.db.close()

    def registry(self, *, dispatch: bool = True) -> OperationRegistry:
        registry = OperationRegistry()
        registry.bind(self.v.names["inventory"], self.v.names["booking-status"], self.status)
        registry.bind(self.v.names["inventory"], self.v.names["lookup-stock"], self.lookup)
        registry.bind(self.v.names["inventory"], self.v.names["reserve-order"], self.reserve)
        if dispatch:
            registry.bind(self.v.names["dispatch"], self.v.names["publish-booking"], self.publish)
        return registry

    def status(self, request):
        self.events.append("status")
        order_id, encoded = request.arguments
        payload = json.dumps(sorted(tuple(line.arguments) for line in self.v.unlist(encoded)), separators=(",", ":"))
        prior = self.db.execute("SELECT payload, receipt FROM bookings WHERE id=?", (order_id,)).fetchone()
        if prior is None:
            return self.v.make("BookingPending")
        return self.v.make("RepeatBooking", prior[1]) if prior[0] == payload else self.v.make("BookingConflict")

    def lookup(self, sku):
        self.events.append("lookup:" + sku)
        if self.fault == "refuse":
            raise HostFailure("refused_authority", "not_started", "synthetic warehouse is closed")
        if self.fault == "wrong-type":
            return "this should have been Stock"
        time.sleep(self.lookup_delay)
        row = self.db.execute("SELECT available FROM stock WHERE sku=?", (sku,)).fetchone()
        return self.v.make("UnknownSku") if row is None else self.v.make("Stock", row[0])

    def reserve(self, request):
        self.events.append("reserve")
        if request.identity != self.v.constructors["order"]:
            raise HostFailure("refused_authority", "not_started", "expected an order")
        order_id, encoded = request.arguments
        lines = [tuple(line.arguments) for line in self.v.unlist(encoded)]
        if not order_id or not lines or len(lines) > 20 or len({sku for sku, _ in lines}) != len(lines) or any(not 1 <= units <= 20 for _, units in lines):
            raise HostFailure("refused_authority", "not_started", "invalid reservation")
        # Line order has no inventory meaning: canonicalise it for idempotency.
        payload = json.dumps(sorted(lines), separators=(",", ":"))
        receipt = hashlib.sha256((order_id + "\0" + payload).encode()).hexdigest()
        if self.fault == "stock-changed":
            # Simulate another sale after Jacquard's lookups, before this
            # transaction. The later conditional UPDATE must detect it.
            self.db.execute("UPDATE stock SET available=0 WHERE sku=?", (lines[-1][0],))
        self.db.execute("BEGIN IMMEDIATE")
        try:
            prior = self.db.execute("SELECT payload, receipt FROM bookings WHERE id=?", (order_id,)).fetchone()
            if prior:
                self.db.execute("ROLLBACK")
                return self.v.make("RepeatBooking", prior[1]) if prior[0] == payload else self.v.make("BookingConflict")
            for sku, units in lines:
                changed = self.db.execute("UPDATE stock SET available=available-? WHERE sku=? AND available>=?", (units, sku, units))
                if changed.rowcount != 1:
                    self.db.execute("ROLLBACK")
                    return self.v.make("StockChanged")
            self.db.execute("INSERT INTO bookings VALUES(?,?,?)", (order_id, payload, receipt))
            self.db.execute("COMMIT")
        except sqlite3.Error:
            if self.db.in_transaction:
                self.db.execute("ROLLBACK")
                raise HostFailure("outside_failure", "failed", "reservation transaction rolled back") from None
            raise HostFailure("completion_unknown", "unknown", "reservation durability unknown") from None
        if self.fault == "after-commit":
            raise RuntimeError("synthetic failure after commit")
        return self.v.make("Booked", receipt)

    def publish(self, order_id, receipt):
        self.events.append("publish")
        if self.fault == "publish-failed":
            raise HostFailure("outside_failure", "failed", "synthetic dispatch unavailable")
        self.published.append((order_id, receipt))
        return ()

    def order(self, order_id, lines):
        return self.v.make("Order", order_id, self.v.list(self.v.make("Line", sku, units) for sku, units in lines))

    def count(self):
        return self.db.execute("SELECT COUNT(*) FROM bookings").fetchone()[0]

    def stock(self, sku):
        return self.db.execute("SELECT available FROM stock WHERE sku=?", (sku,)).fetchone()[0]
