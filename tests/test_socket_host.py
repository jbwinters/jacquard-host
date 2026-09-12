"""The serial socket host with an in-process invoker (no Core needed)."""

from __future__ import annotations

import os
import socket
import threading
import time
import unittest

from jacquard_host.adapter import InvocationResult
from jacquard_host.socket_host import Ceilings, SerialSocketHost, bytes_of_text, text_of_bytes


def echo_invoker(request_text: str) -> InvocationResult:
    body = f"len={len(request_text)}".encode("latin-1")
    response = b"HTTP/1.1 200 OK\r\nContent-Length: %d\r\nConnection: close\r\n\r\n%s" % (len(body), body)
    return InvocationResult(kind="ok", value=text_of_bytes(response), terminal="ok")


def failing_invoker(request_text: str) -> InvocationResult:
    return InvocationResult(kind="error", terminal="diagnostic")


def serve_in_thread(host: SerialSocketHost, count: int = 1) -> threading.Thread:
    def run() -> None:
        for _ in range(count):
            host.serve_one(accept_timeout=5)

    thread = threading.Thread(target=run, daemon=True)
    thread.start()
    return thread


def talk(address, data: bytes, *, read: bool = True, close_early: bool = False) -> bytes:
    with socket.create_connection(address, timeout=5) as client:
        client.sendall(data)
        if close_early:
            return b""
        if not read:
            # hold the connection open past the host's request timeout without sending more
            time.sleep(0.8)
            return b""
        chunks = []
        while True:
            try:
                chunk = client.recv(65536)
            except ConnectionResetError:
                # the host closed with unread input; a reset is the same "no response"
                return b"".join(chunks)
            if not chunk:
                return b"".join(chunks)
            chunks.append(chunk)


class MappingTest(unittest.TestCase):
    def test_latin1_round_trip_is_byte_exact(self) -> None:
        data = bytes(range(256)) * 3
        self.assertEqual(bytes_of_text(text_of_bytes(data)), data)
        self.assertEqual(len(text_of_bytes(data)), len(data))
        from jacquard_host.socket_host import RequestError
        with self.assertRaises(RequestError):
            bytes_of_text("éĀ")


class SocketHostTest(unittest.TestCase):
    def test_binds_loopback_only(self) -> None:
        with self.assertRaises(ValueError):
            SerialSocketHost(echo_invoker, bind=("0.0.0.0", 0))

    def test_serves_one_request_and_closes(self) -> None:
        with SerialSocketHost(echo_invoker) as host:
            thread = serve_in_thread(host)
            request = b"POST /x HTTP/1.1\r\nHost: a\r\nContent-Length: 5\r\n\r\nhello"
            reply = talk(host.address, request)
            thread.join(5)
        self.assertTrue(reply.startswith(b"HTTP/1.1 200 OK"))
        self.assertTrue(reply.endswith(b"len=%d" % len(request)))
        self.assertEqual(host.served[0].outcome, "served")
        self.assertEqual((host.served[0].request_bytes, host.served[0].response_bytes), (len(request), len(reply)))

    def test_no_keep_alive_second_request_is_not_served(self) -> None:
        with SerialSocketHost(echo_invoker) as host:
            thread = serve_in_thread(host)
            with socket.create_connection(host.address, timeout=5) as client:
                client.sendall(b"GET /1 HTTP/1.1\r\n\r\n")
                first = client.recv(65536)
                client.sendall(b"GET /2 HTTP/1.1\r\n\r\n")
                rest = client.recv(65536)
            thread.join(5)
        self.assertTrue(first.startswith(b"HTTP/1.1 200"))
        self.assertEqual(rest, b"")
        self.assertEqual(len(host.served), 1)

    def test_serial_by_construction(self) -> None:
        order = []

        def slow(request_text: str) -> InvocationResult:
            order.append("start")
            time.sleep(0.3)
            order.append("end")
            return echo_invoker(request_text)

        with SerialSocketHost(slow) as host:
            thread = serve_in_thread(host, count=2)
            threads = [threading.Thread(target=talk, args=(host.address, b"GET / HTTP/1.1\r\n\r\n")) for _ in range(2)]
            for t in threads:
                t.start()
            for t in threads:
                t.join(10)
            thread.join(10)
        self.assertEqual(order, ["start", "end", "start", "end"])
        self.assertEqual([s.outcome for s in host.served], ["served", "served"])

    def test_ceilings_close_the_connection_without_a_response(self) -> None:
        ceilings = Ceilings(max_request_bytes=64, max_head_bytes=64, head_timeout=0.3, request_timeout=0.5)
        cases = {
            "oversized": b"GET / HTTP/1.1\r\nX: " + b"a" * 100 + b"\r\n\r\n",
            "truncated": b"GET / HTTP/1.1\r\nHost",
            "timeout": b"GET / HTTP/1.1\r\nContent-Length: 10\r\n\r\nabc",
        }
        for expected, data in cases.items():
            with self.subTest(expected=expected), SerialSocketHost(echo_invoker, ceilings=ceilings) as host:
                thread = serve_in_thread(host)
                reply = talk(host.address, data, close_early=(expected == "truncated"), read=(expected != "timeout"))
                thread.join(5)
                self.assertEqual(reply, b"")
                self.assertEqual(host.served[0].outcome, expected, host.served[0])
                self.assertIsNone(host.served[0].invocation)

    def test_oversized_declared_body_is_refused_before_reading_it(self) -> None:
        with SerialSocketHost(echo_invoker, ceilings=Ceilings(max_request_bytes=100)) as host:
            thread = serve_in_thread(host)
            reply = talk(host.address, b"POST / HTTP/1.1\r\nContent-Length: 1000\r\n\r\n")
            thread.join(5)
        self.assertEqual((reply, host.served[0].outcome, host.served[0].detail), (b"", "oversized", "body"))

    def test_invocation_failure_closes_without_fabricating_a_response(self) -> None:
        with SerialSocketHost(failing_invoker) as host:
            thread = serve_in_thread(host)
            reply = talk(host.address, b"GET / HTTP/1.1\r\n\r\n")
            thread.join(5)
        self.assertEqual(reply, b"")
        self.assertEqual((host.served[0].outcome, host.served[0].detail), ("invocation_failed", "error:diagnostic:"))

    def test_response_above_latin1_is_refused(self) -> None:
        with SerialSocketHost(lambda _: InvocationResult(kind="ok", value="Ā", terminal="ok")) as host:
            thread = serve_in_thread(host)
            reply = talk(host.address, b"GET / HTTP/1.1\r\n\r\n")
            thread.join(5)
        self.assertEqual((reply, host.served[0].outcome), (b"", "response_unencodable"))

    def test_stop_interrupts_a_blocked_accept_and_port_can_be_reused(self) -> None:
        host = SerialSocketHost(echo_invoker)
        address = host.address
        thread = threading.Thread(target=host.serve_forever, daemon=True)
        thread.start()
        time.sleep(0.1)
        host.stop()
        thread.join(5)
        self.assertFalse(thread.is_alive())
        with SerialSocketHost(echo_invoker, bind=address) as again:
            self.assertEqual(again.address, address)

    def test_unframeable_content_length_and_invoker_errors_never_take_the_host_down(self) -> None:
        def exploding(request_text: str) -> InvocationResult:
            raise OSError("worker could not be spawned")

        with SerialSocketHost(exploding) as host:
            thread = threading.Thread(target=host.serve_forever, daemon=True)
            thread.start()
            huge = talk(host.address, b"POST / HTTP/1.1\r\nContent-Length: " + b"1" * 4301 + b"\r\n\r\n")
            digits = talk(host.address, b"POST / HTTP/1.1\r\nContent-Length: abc\r\n\r\n")
            crashed = talk(host.address, b"GET / HTTP/1.1\r\n\r\n")
            still_alive = talk(host.address, b"GET / HTTP/1.1\r\n\r\n")
            host.stop()
            thread.join(5)
        self.assertEqual((huge, digits, crashed, still_alive), (b"", b"", b"", b""))
        self.assertEqual([s.outcome for s in host.served], ["oversized", "oversized", "host_error", "host_error"])
        self.assertEqual(host.served[2].detail, "OSError")
        self.assertFalse(thread.is_alive())

    def test_serve_one_from_two_threads_is_still_serial(self) -> None:
        active = []
        peak = []

        def slow(request_text: str) -> InvocationResult:
            active.append(1)
            peak.append(len(active))
            time.sleep(0.2)
            active.pop()
            return echo_invoker(request_text)

        with SerialSocketHost(slow) as host:
            servers = [threading.Thread(target=lambda: host.serve_one(accept_timeout=5), daemon=True) for _ in range(2)]
            for t in servers:
                t.start()
            clients = [threading.Thread(target=talk, args=(host.address, b"GET / HTTP/1.1\r\n\r\n")) for _ in range(2)]
            for t in clients:
                t.start()
            for t in clients + servers:
                t.join(10)
        self.assertEqual(max(peak), 1)
        self.assertEqual(len(host.served), 2)

    def test_stop_closes_the_listener_and_resets_a_queued_connection(self) -> None:
        def slow(request_text: str) -> InvocationResult:
            time.sleep(0.5)
            return echo_invoker(request_text)

        with SerialSocketHost(slow) as host:
            thread = threading.Thread(target=host.serve_forever, daemon=True)
            thread.start()
            first = threading.Thread(target=talk, args=(host.address, b"GET / HTTP/1.1\r\n\r\n"))
            first.start()
            time.sleep(0.1)
            queued = socket.create_connection(host.address, timeout=5)
            queued.sendall(b"GET /queued HTTP/1.1\r\n\r\n")
            host.stop()
            first.join(5)
            thread.join(5)
            try:
                reply = queued.recv(100)
            except OSError:
                reply = b""
            queued.close()
        self.assertEqual(reply, b"")
        self.assertFalse(thread.is_alive())
        self.assertEqual([s.outcome for s in host.served], ["served"])

    def test_response_timeout_is_labelled_timeout(self) -> None:
        big = "HTTP/1.1 200 OK\r\nContent-Length: 8000000\r\n\r\n" + "x" * 8_000_000
        with SerialSocketHost(lambda _: InvocationResult(kind="ok", value=big, terminal="ok"), ceilings=Ceilings(response_timeout=0.3)) as host:
            thread = serve_in_thread(host)
            with socket.create_connection(host.address, timeout=5) as client:
                client.sendall(b"GET / HTTP/1.1\r\n\r\n")
                time.sleep(1.0)  # never read
            thread.join(10)
        self.assertEqual((host.served[0].outcome, host.served[0].detail), ("timeout", "response"))

    def test_client_leaving_after_the_request_is_recorded_not_raised(self) -> None:
        def slow(request_text: str) -> InvocationResult:
            time.sleep(0.3)
            return echo_invoker(request_text)

        with SerialSocketHost(slow) as host:
            thread = serve_in_thread(host)
            with socket.create_connection(host.address, timeout=5) as client:
                client.sendall(b"GET / HTTP/1.1\r\n\r\n")
            thread.join(5)
        self.assertIn(host.served[0].outcome, ("served", "disconnected"))
        self.assertEqual(host.served[0].request_bytes, 18)

    def test_served_records_are_bounded(self) -> None:
        with SerialSocketHost(echo_invoker, ceilings=Ceilings(max_served_records=3)) as host:
            thread = serve_in_thread(host, count=5)
            for _ in range(5):
                talk(host.address, b"GET / HTTP/1.1\r\n\r\n")
            thread.join(10)
        self.assertEqual(len(host.served), 3)

    def test_ceilings_are_validated_and_bind_failure_leaks_nothing(self) -> None:
        with self.assertRaises(ValueError):
            Ceilings(max_request_bytes=131073)
        with self.assertRaises(ValueError):
            Ceilings(head_timeout=0)
        if not os.path.isdir("/proc/self/fd"):
            self.skipTest("no /proc")
        with SerialSocketHost(echo_invoker) as host:
            before = len(os.listdir("/proc/self/fd"))
            with self.assertRaises(OSError):
                SerialSocketHost(echo_invoker, bind=host.address)
            self.assertEqual(len(os.listdir("/proc/self/fd")), before)

    def test_descriptors_are_released_across_many_connections(self) -> None:
        if not os.path.isdir("/proc/self/fd"):
            self.skipTest("no /proc")
        with SerialSocketHost(echo_invoker) as host:
            before = len(os.listdir("/proc/self/fd"))
            thread = serve_in_thread(host, count=20)
            for i in range(20):
                talk(host.address, b"GET / HTTP/1.1\r\n\r\n" if i % 2 else b"GET /", close_early=bool(i % 2 == 0))
            thread.join(20)
            self.assertEqual(len(os.listdir("/proc/self/fd")), before)
        self.assertEqual(len(host.served), 20)


if __name__ == "__main__":
    unittest.main()
