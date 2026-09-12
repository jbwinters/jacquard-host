"""Measure where the serial host's one-request-at-a-time model costs (roadmap H2.2).

Runs the synthetic trial behind the serial socket host and drives it with
controlled client patterns, reporting per-request wall time, queue wait, and
the fixed per-invocation overhead (worker spawn, store open, negotiation).
No event loop, threads in the host, or concurrency is added; the client side
uses threads only to create load. Output is one JSON document for
docs/C4-REQUIREMENTS.md.

Usage:
  PYTHONPATH=src JACQUARD_BIN=... JACQUARD_PRELUDE=... python3 scripts/measure_serial.py --workdir .scratch/measure
"""

from __future__ import annotations

import argparse
import json
import os
import socket
import statistics
import sys
import tempfile
import threading
import time

from jacquard_host.protocol.envelopes import Interface
from jacquard_host.protocol.values import TupleType
from jacquard_host.socket_host import Ceilings
from jacquard_host.trial import TrialApp

GET = b"GET /trial HTTP/1.1\r\nHost: localhost\r\n\r\n"


def post(session: str) -> bytes:
    body = f"trial=trial-synthetic-1&session={session}&answer=a".encode()
    return b"POST /response HTTP/1.1\r\nHost: localhost\r\nContent-Length: %d\r\n\r\n%s" % (len(body), body)


def talk(address, data: bytes, timeout: float = 120.0) -> tuple[float, float, bytes]:
    """Return (connect wait, total wall, reply)."""
    started = time.monotonic()
    with socket.create_connection(address, timeout=timeout) as client:
        connected = time.monotonic()
        client.sendall(data)
        chunks = []
        while True:
            chunk = client.recv(65536)
            if not chunk:
                break
            chunks.append(chunk)
    return connected - started, time.monotonic() - started, b"".join(chunks)


def summary(values: list[float]) -> dict:
    values = sorted(values)
    return {
        "n": len(values),
        "min": round(values[0], 4),
        "median": round(statistics.median(values), 4),
        "p90": round(values[int(0.9 * (len(values) - 1))], 4),
        "max": round(values[-1], 4),
        "mean": round(statistics.fmean(values), 4),
    }


def scenario(app: TrialApp, name: str, requests: list[bytes], concurrency: int) -> dict:
    server = app.server(ceilings=Ceilings(max_request_bytes=8192, head_timeout=30, request_timeout=60, response_timeout=60))
    serving = threading.Thread(target=lambda: [server.serve_one(accept_timeout=120) for _ in requests], daemon=True)
    serving.start()
    results: list[tuple[float, float, bytes]] = []
    lock = threading.Lock()

    def one(data: bytes) -> None:
        outcome = talk(server.address, data)
        with lock:
            results.append(outcome)

    started = time.monotonic()
    threads: list[threading.Thread] = []
    for index, data in enumerate(requests):
        thread = threading.Thread(target=one, args=(data,))
        threads.append(thread)
        thread.start()
        if (index + 1) % concurrency == 0:
            for t in threads:
                t.join()
            threads = []
    for t in threads:
        t.join()
    serving.join(300)
    elapsed = time.monotonic() - started
    server.close()
    statuses = sorted({reply.split(b"\r\n", 1)[0].decode("latin-1") for _, _, reply in results})
    return {
        "scenario": name,
        "requests": len(requests),
        "client_concurrency": concurrency,
        "wall_seconds": round(elapsed, 3),
        "throughput_per_second": round(len(requests) / elapsed, 3),
        "request_wall": summary([total for _, total, _ in results]),
        "statuses": statuses,
        "served_outcomes": sorted({s.outcome for s in server.served}),
        "invocation_elapsed": summary([s.elapsed_seconds for s in server.served]),
    }


def main(argv=None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--workdir", required=True)
    parser.add_argument("--prelude", default=os.environ.get("JACQUARD_PRELUDE"))
    parser.add_argument("--jacquard", default=os.environ.get("JACQUARD_BIN", "jacquard"))
    parser.add_argument("--requests", type=int, default=12)
    parser.add_argument("--report", default=None)
    args = parser.parse_args(argv)
    with TrialApp(args.workdir, prelude=args.prelude, command=[args.jacquard]) as app:
        report: dict = {"schema": "jacquard-host-serial-measurement-v0", "python": sys.version.split()[0]}
        # fixed per-invocation overhead: negotiate and shut down, no evaluation
        probes = []
        for _ in range(5):
            started = time.monotonic()
            result = app.host.shutdown_probe()
            probes.append(time.monotonic() - started)
            assert result.kind == "shutdown", result
        report["worker_spawn_negotiate_shutdown"] = summary(probes)
        # a pure invocation through the socket host, one client at a time
        report["scenarios"] = [
            scenario(app, "serial GET, one client", [GET] * args.requests, 1),
            scenario(app, "serial POST, one client", [post(f"s{i}") for i in range(args.requests)], 1),
            scenario(app, "burst GET, all clients at once", [GET] * args.requests, args.requests),
            scenario(app, "burst POST, all clients at once", [post(f"b{i}") for i in range(args.requests)], args.requests),
        ]
    text = json.dumps(report, indent=2)
    print(text)
    if args.report:
        with open(args.report, "w", encoding="utf-8") as stream:
            stream.write(text + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
