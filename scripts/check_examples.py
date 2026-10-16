"""Smoke-test all public examples through the installed SDK and real Core.

Uses fresh synthetic state under .scratch/ and checks expected CLI outcomes,
including failed dispatch followed by explicit reconciliation.
"""

from __future__ import annotations

import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile

import jacquard_host

REPO = Path(__file__).resolve().parents[1]
APP = REPO / "examples" / "python_sdk" / "app.py"


def run(cwd: Path, *arguments: str | Path, status: int = 0) -> str:
    result = subprocess.run([sys.executable, *map(str, arguments)], cwd=cwd,
                            text=True, capture_output=True, timeout=90)
    if result.returncode != status:
        raise AssertionError(f"{arguments}: expected exit {status}, got {result.returncode}\n{result.stdout}\n{result.stderr}")
    return result.stdout


def documents(output: str) -> list:
    # JSON roots are unindented; render() may precede them with plain text.
    return [json.JSONDecoder().raw_decode(output[match.start():])[0]
            for match in re.finditer(r"(?m)^[\[{]", output)]


def main() -> None:
    if Path(jacquard_host.__file__).resolve().is_relative_to(REPO / "src"):
        raise SystemExit("Install the SDK first; this check must use the installed distribution.")
    if not os.environ.get("JACQUARD_BIN") or not os.environ.get("JACQUARD_PRELUDE"):
        raise SystemExit("Set JACQUARD_BIN and JACQUARD_PRELUDE using docs/SETUP.md.")
    scratch = REPO / ".scratch"
    scratch.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="public-examples-", dir=scratch) as temporary:
        root = Path(temporary)
        output = run(root, REPO / "examples" / "quickstart" / "run.py")
        assert output.splitlines() == ["Order total 35: shipping 5", "Order total 75: shipping 0"], output
        for project in ("planner", "triage", "fulfillment"):
            arguments = (APP, project, REPO / "examples" / project / "input.json", "--workdir", root / project)
            values = documents(run(root, *arguments))
            if project == "planner":
                result = values[0]
                assert result["type"] == "Planned" and result["plan"]["score"] == 16, result
                assert {(item["id"], item["slot"]) for item in result["plan"]["assignments"]} == {("fix-a", 1), ("fix-b", 1), ("docs", 2)}
            elif project == "triage":
                assert [(item["id"], item["team"], item["urgency"], item["reasons"]) for item in values[0]] == [
                    ("inc-1", "checkout-oncall", "PageNow", ["unowned", "customer-impact"]),
                    ("inc-2", "search-team", "WorkToday", ["ageing"]),
                    ("inc-3", "billing-team", "WatchOnly", []),
                ], values
            else:
                assert values[0]["type"] == "Fulfilled", values
                repeated = documents(run(root, *arguments))
                assert repeated[0] == {"type": "DuplicateOrder", "receipt": values[0]["receipt"]}, repeated
                assert repeated[1] == {"bookings": 1, "pending_deliveries": 0, "deliveries": 1}, repeated
        recovery = root / "recovery"
        output = run(root, APP, "fulfillment", REPO / "examples" / "fulfillment" / "input.json",
                     "--workdir", recovery, "--fault", "publish-failed", status=1)
        assert "E1613" in output, output
        assert documents(output)[0] == {"bookings": 1, "pending_deliveries": 1, "deliveries": 0}, output
        for expected in (1, 0):
            output = run(root, APP, "fulfillment", "--workdir", recovery, "--deliver-pending")
            assert documents(output)[0] == {"delivered": expected, "bookings": 1, "pending_deliveries": 0, "deliveries": 1}, output
    print("All four public examples passed, including duplicate fulfillment and explicit recovery.")


if __name__ == "__main__":
    main()
