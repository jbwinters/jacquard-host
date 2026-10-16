"""Small command-line front ends for the three application experiments."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import tempfile

from inventory import Inventory
from support import Program


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("project", choices=("fulfillment", "planner", "triage"))
    parser.add_argument("input", type=Path, help="synthetic JSON input; see inputs/")
    parser.add_argument("--workdir", type=Path, default=Path(".scratch/python-projects"))
    parser.add_argument("--jacquard", default=os.environ.get("JACQUARD_BIN"))
    parser.add_argument("--prelude", default=os.environ.get("JACQUARD_PRELUDE"))
    parser.add_argument("--fault", choices=("", "refuse", "wrong-type", "stock-changed", "after-commit", "publish-failed"), default="")
    args = parser.parse_args()
    if not args.jacquard or not args.prelude:
        parser.error("set JACQUARD_BIN and JACQUARD_PRELUDE")
    payload = json.loads(args.input.read_text())
    args.workdir.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="store-", dir=args.workdir) as temporary:
        p = Program(args.project, Path(temporary), binary=str(Path(args.jacquard).resolve()), prelude=str(Path(args.prelude).resolve()))
        v = p.values
        inventory = None
        try:
            if args.project == "fulfillment":
                inventory = Inventory(args.workdir / "inventory.sqlite", p)
                inventory.fault = args.fault
                request = inventory.order(payload["id"], [(line["sku"], line["units"]) for line in payload["lines"]])
                calls = [p.call("fulfillment.place", request, registry=inventory.registry())]
            elif args.project == "planner":
                jobs = v.list(v.make("Job", row["id"], row["effort"], row["value"], row["latest"]) for row in payload["jobs"])
                calls = [p.call("planner.plan", jobs, v.list(payload["capacities"]))]
            else:
                policy = v.make("TriagePolicy", payload["policy"]["page"], payload["policy"]["stale"])
                incidents = []
                for row in payload["incidents"]:
                    owner = v.make("None") if row["owner"] is None else v.make("Some", row["owner"])
                    incidents.append(v.make("Incident", row["id"], row["service"], row["severity"], row["age"], v.make("True" if row["visible"] else "False"), owner))
                # Eight is conservative for these fixed record shapes under v0.
                calls = [p.call("triage.batch", policy, v.list(incidents[start:start + 8])) for start in range(0, len(incidents), 8)]
            output = {"project": args.project, "setup_ms": round(p.setup_ms, 3), "calls": [call.readable for call in calls]}
            if inventory:
                output["durable_bookings"] = inventory.count()
                output["simulated_dispatches_this_process"] = inventory.published
            print(json.dumps(output, indent=2))
            return 0 if all(call.result.ok for call in calls) else 1
        finally:
            if inventory:
                inventory.close()


if __name__ == "__main__":
    raise SystemExit(main())
