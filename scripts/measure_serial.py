"""Measure where the serial host's one-request-at-a-time model costs (roadmap H2.2).

Runs the synthetic trial behind the serial socket host and drives it with
controlled client patterns, reporting connect wait (time until the kernel
accepts the connection, which under a full accept backlog is SYN retransmit
time), per-request wall time, and the fixed per-invocation overhead (worker
spawn, store open, negotiation). Every burst runs twice, once with the
host's default accept backlog of 1 and once with a backlog at least as
large as the burst, so kernel admission effects and true head-of-line wait
are reported separately. No event loop, threads in the host, or concurrency
is added; the client side uses threads only to create load. Output is one
JSON document for docs/C4-REQUIREMENTS.md.

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


def talk(address, data: bytes, timeout: float) -> tuple[float, float, bytes | None]:
    """Return (connect wait, total wall, reply); reply is None when the client gave up.

    Under a full accept backlog a client can complete its handshake and still
    never be accepted by the host (the kernel dropped its final ACK); such a
    client reports the handshake time as connect wait and then times out.
    """
    started = time.monotonic()
    connected = None
    try:
        with socket.create_connection(address, timeout=timeout) as client:
            connected = time.monotonic()
            client.sendall(data)
            chunks = []
            while True:
                chunk = client.recv(65536)
                if not chunk:
                    break
                chunks.append(chunk)
    except OSError:
        now = time.monotonic()
        return (connected or now) - started, now - started, None
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


def scenario(app: TrialApp, name: str, requests: list[bytes], concurrency: int, *, backlog: int = 1, client_timeout: float = 30.0) -> dict:
    server = app.server(ceilings=Ceilings(max_request_bytes=8192, head_timeout=30, request_timeout=60, response_timeout=60), backlog=backlog)
    serving = threading.Thread(target=lambda: [server.serve_one(accept_timeout=client_timeout + 5) for _ in requests], daemon=True)
    serving.start()
    results: list[tuple[float, float, bytes | None]] = []
    lock = threading.Lock()

    def one(data: bytes) -> None:
        outcome = talk(server.address, data, client_timeout)
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
    elapsed = time.monotonic() - started
    server.close()
    serving.join(30)
    answered = [r for r in results if r[2] is not None]
    gave_up = len(results) - len(answered)
    statuses = sorted({reply.split(b"\r\n", 1)[0].decode("latin-1") for _, _, reply in answered})
    return {
        "scenario": name,
        "requests": len(requests),
        "client_concurrency": concurrency,
        "accept_backlog": backlog,
        "client_timeout_seconds": client_timeout,
        "wall_seconds": round(elapsed, 3),
        "served": len(server.served),
        "clients_gave_up": gave_up,
        "throughput_per_second": round(len(server.served) / elapsed, 3),
        "connect_wait": summary([wait for wait, _, _ in results]),
        "request_wall": summary([total for _, total, _ in answered]) if answered else None,
        "queue_wait_after_connect": summary([total - wait for wait, total, _ in answered]) if answered else None,
        "statuses": statuses,
        "served_outcomes": sorted({s.outcome for s in server.served}),
        "invocation_elapsed": summary([s.elapsed_seconds for s in server.served]) if server.served else None,
    }


def main(argv=None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--workdir", required=True)
    parser.add_argument("--prelude", default=os.environ.get("JACQUARD_PRELUDE"))
    parser.add_argument("--jacquard", default=os.environ.get("JACQUARD_BIN", "jacquard"))
    parser.add_argument("--requests", type=int, default=12)
    parser.add_argument("--large-burst", type=int, default=0, help="also run a large burst (e.g. 40) with backlog 1 and with a matching backlog")
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
        n = args.requests
        scenarios = [
            scenario(app, "serial GET, one client", [GET] * n, 1),
            scenario(app, "serial POST, one client", [post(f"s{i}") for i in range(n)], 1),
        ]
        for burst in sorted({4, 8, n}):
            scenarios.append(scenario(app, f"burst GET x{burst}, backlog 1", [GET] * burst, burst, backlog=1))
            scenarios.append(scenario(app, f"burst GET x{burst}, backlog {burst}", [GET] * burst, burst, backlog=burst))
        scenarios.append(scenario(app, f"burst POST x{n}, backlog {n}", [post(f"b{i}") for i in range(n)], n, backlog=n))
        if args.large_burst:
            scenarios.append(scenario(app, f"burst GET x{args.large_burst}, backlog 1, clients give up after 10 s", [GET] * args.large_burst, args.large_burst, backlog=1, client_timeout=10.0))
            scenarios.append(scenario(app, f"burst GET x{args.large_burst}, backlog {args.large_burst}", [GET] * args.large_burst, args.large_burst, backlog=args.large_burst))
        report["scenarios"] = scenarios
    text = json.dumps(report, indent=2)
    print(text)
    if args.report:
        with open(args.report, "w", encoding="utf-8") as stream:
            stream.write(text + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
