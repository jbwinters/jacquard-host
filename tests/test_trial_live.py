"""The synthetic trial end to end: socket -> worker -> Jacquard HTTP -> Study -> SQLite (skipped without Core)."""

from __future__ import annotations

import os
import shutil
import socket
import tempfile
import threading
import unittest

from jacquard_host.socket_host import Ceilings
from jacquard_host.study import receipt_of
from jacquard_host.trial import TrialApp

JACQUARD_BIN = os.environ.get("JACQUARD_BIN")
JACQUARD_PRELUDE = os.environ.get("JACQUARD_PRELUDE")


def talk(address, data: bytes) -> bytes:
    with socket.create_connection(address, timeout=30) as client:
        client.sendall(data)
        chunks = []
        while True:
            chunk = client.recv(65536)
            if not chunk:
                return b"".join(chunks)
            chunks.append(chunk)


def post(body: bytes) -> bytes:
    return b"POST /response HTTP/1.1\r\nHost: localhost\r\nContent-Length: %d\r\n\r\n%s" % (len(body), body)


@unittest.skipUnless(JACQUARD_BIN and JACQUARD_PRELUDE and os.path.isdir(JACQUARD_PRELUDE), "set JACQUARD_BIN and JACQUARD_PRELUDE to run the trial slice")
class TrialSliceTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.scratch = tempfile.mkdtemp(prefix="jacquard-host-trial-")
        cls.app = TrialApp(cls.scratch, prelude=JACQUARD_PRELUDE, command=[JACQUARD_BIN])

    @classmethod
    def tearDownClass(cls) -> None:
        cls.app.close()
        shutil.rmtree(cls.scratch, ignore_errors=True)

    def serve(self, count: int):
        server = self.app.server(ceilings=Ceilings(max_request_bytes=8192))
        thread = threading.Thread(target=lambda: [server.serve_one(accept_timeout=30) for _ in range(count)], daemon=True)
        thread.start()
        return server, thread

    def test_get_trial_is_deterministic(self) -> None:
        server, thread = self.serve(2)
        first = talk(server.address, b"GET /trial HTTP/1.1\r\nHost: localhost\r\n\r\n")
        second = talk(server.address, b"GET /trial HTTP/1.1\r\nHost: localhost\r\n\r\n")
        thread.join(60)
        server.close()
        self.assertEqual(first, second)
        head, body = first.split(b"\r\n\r\n", 1)
        self.assertEqual(head.split(b"\r\n")[0], b"HTTP/1.1 200 OK")
        self.assertIn(b"Content-Length: %d" % len(body), head)
        self.assertIn(b"Connection: close", head)
        self.assertTrue(body.startswith(b"trial=trial-synthetic-1\npassage-a="))
        self.assertTrue(body.endswith(b"answer-choices=a,b\n"))
        self.assertEqual([s.outcome for s in server.served], ["served", "served"])
        self.assertEqual(server.served[0].invocation.evidence["core"]["effect_requests"], [])

    def test_post_response_saves_once_and_returns_a_receipt(self) -> None:
        server, thread = self.serve(4)
        receipt = receipt_of("trial-synthetic-1", "sess-42", "b")
        created = talk(server.address, post(b"trial=trial-synthetic-1&session=sess-42&answer=b"))
        duplicate = talk(server.address, post(b"trial=trial-synthetic-1&session=sess-42&answer=b"))
        conflict = talk(server.address, post(b"trial=trial-synthetic-1&session=sess-42&answer=a"))
        other = talk(server.address, post(b"answer=a&session=sess-43&trial=trial-synthetic-1"))
        thread.join(120)
        server.close()
        self.assertTrue(created.startswith(b"HTTP/1.1 201 Created\r\n"), created)
        self.assertTrue(created.endswith(b"\r\n\r\nreceipt=" + receipt.encode()), created)
        self.assertTrue(duplicate.startswith(b"HTTP/1.1 200 OK\r\n"), duplicate)
        self.assertTrue(duplicate.endswith(b"receipt=" + receipt.encode()), duplicate)
        self.assertTrue(conflict.startswith(b"HTTP/1.1 409 Conflict\r\n"), conflict)
        self.assertTrue(other.startswith(b"HTTP/1.1 201 Created\r\n"), other)
        self.assertEqual(self.app.study.count(), 2)
        evidence = server.served[0].invocation.evidence
        self.assertEqual(
            evidence["core"]["capabilities"]["operations"],
            [{"effect": self.app.identities.effect.hex, "mode": "once", "operation": self.app.identities.save_response.hex}],
        )
        self.assertEqual([r["ordinal"] for r in evidence["core"]["effect_requests"]], [1])
        self.assertEqual(evidence["host_observations"]["responses"], [{"category": "ok", "completion": "completed", "ordinal": 1}])
        self.assertEqual(server.served[0].invocation.exit_status, 0)

    def test_rejections_are_jacquard_authored(self) -> None:
        cases = {
            b"GET /nowhere HTTP/1.1\r\nHost: h\r\n\r\n": b"HTTP/1.1 404 Not Found",
            b"POST /trial HTTP/1.1\r\nHost: h\r\nContent-Length: 0\r\n\r\n": b"HTTP/1.1 405 Method Not Allowed",
            b"PUT /trial HTTP/1.1\r\nHost: h\r\n\r\n": b"HTTP/1.1 501 Not Implemented",
            b"GET /trial HTTP/1.0\r\nHost: h\r\n\r\n": b"HTTP/1.1 505 HTTP Version Not Supported",
            b"GET /trial HTTP/1.1\r\n\r\n": b"HTTP/1.1 400 Bad Request",
            post(b"trial=trial-synthetic-1&session=s&answer=c"): b"HTTP/1.1 400 Bad Request",
            post(b"trial=other&session=s&answer=a"): b"HTTP/1.1 404 Not Found",
            post(b"trial=trial-synthetic-1&session=s"): b"HTTP/1.1 400 Bad Request",
            post(b"trial=trial-synthetic-1&session=bad token&answer=a"): b"HTTP/1.1 400 Bad Request",
            post(b"trial=trial-synthetic-1&session=s&answer=a&answer=b"): b"HTTP/1.1 400 Bad Request",
            post(b"trial=trial-synthetic-1&session=.dot&answer=a"): b"HTTP/1.1 400 Bad Request",
            post(b"trial=trial-synthetic-1&session=-dash&answer=a"): b"HTTP/1.1 400 Bad Request",
            post(b"trial=trial-synthetic-1&session=" + b"y" * 129 + b"&answer=a"): b"HTTP/1.1 400 Bad Request",
            post(b"trial=trial-synthetic-1&session=s&answer=a&extra=1"): b"HTTP/1.1 400 Bad Request",
            post(b"trial=trial-synthetic-1&session=s%20x&answer=a"): b"HTTP/1.1 400 Bad Request",
        }
        server, thread = self.serve(len(cases))
        replies = {request: talk(server.address, request) for request in cases}
        thread.join(300)
        server.close()
        for request, expected in cases.items():
            with self.subTest(request=request[:40]):
                self.assertTrue(replies[request].startswith(expected + b"\r\n"), replies[request])
        self.assertTrue(all(s.outcome == "served" for s in server.served))
        self.assertTrue(all(s.invocation.requests == [] for s in server.served), "no rejection reaches the persistence operation")

    def test_restart_keeps_the_record_and_leaves_nothing_behind(self) -> None:
        def leftovers() -> set:
            return {name for name in os.listdir(tempfile.gettempdir()) if name.startswith("jacquard-host-worker-stderr-")}

        before_files = leftovers()
        first = self.app.serve_text(post(b"trial=trial-synthetic-1&session=sess-restart&answer=a").decode("latin-1"))
        self.assertTrue(first.value.startswith("HTTP/1.1 201 Created\r\n"), first.value)
        trial_before = self.app.serve_text("GET /trial HTTP/1.1\r\nHost: h\r\n\r\n").value
        count_before = self.app.study.count()
        with TrialApp(self.scratch, prelude=JACQUARD_PRELUDE, command=[JACQUARD_BIN]) as restarted:
            result = restarted.serve_text(post(b"trial=trial-synthetic-1&session=sess-restart&answer=a").decode("latin-1"))
            self.assertTrue(result.ok, result)
            self.assertTrue(result.value.startswith("HTTP/1.1 200 OK\r\n"), result.value)
            self.assertEqual(restarted.study.count(), count_before)
            self.assertEqual(restarted.serve_text("GET /trial HTTP/1.1\r\nHost: h\r\n\r\n").value, trial_before)
        self.assertEqual(leftovers() - before_files, set())
        # SQLite's -wal/-shm companions exist while a store is open; nothing else may appear
        present = sorted(name for name in os.listdir(self.scratch) if not name.startswith("study.sqlite-"))
        self.assertEqual(present, ["store", "store.source.sha256", "study.sqlite", "trial-all.jac"])

    def test_a_store_built_from_a_different_source_is_refused(self) -> None:
        with tempfile.TemporaryDirectory(prefix="jacquard-host-trial-stale-") as other:
            sources = os.path.join(other, "src")
            shutil.copytree(os.path.dirname(os.path.dirname(os.path.abspath(__file__))) + "/jacquard", sources)
            workdir = os.path.join(other, "work")
            TrialApp(workdir, prelude=JACQUARD_PRELUDE, command=[JACQUARD_BIN], sources=sources).close()
            path = os.path.join(sources, "trial.jac")
            with open(path, encoding="utf-8") as stream:
                text = stream.read()
            with open(path, "w", encoding="utf-8") as stream:
                stream.write(text.replace("trial-synthetic-1", "trial-synthetic-2"))
            with self.assertRaises(RuntimeError) as caught:
                TrialApp(workdir, prelude=JACQUARD_PRELUDE, command=[JACQUARD_BIN], sources=sources)
            self.assertIn("different source", str(caught.exception))


if __name__ == "__main__":
    unittest.main()
