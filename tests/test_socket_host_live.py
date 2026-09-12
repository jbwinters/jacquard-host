"""The serial socket host in front of a real Jacquard term (skipped without Core)."""

from __future__ import annotations

import os
import shutil
import socket
import subprocess
import tempfile
import threading
import unittest

from jacquard_host.adapter import Host
from jacquard_host.identities import hash_file, parse_signature, signatures
from jacquard_host.socket_host import Ceilings, SerialSocketHost

JACQUARD_BIN = os.environ.get("JACQUARD_BIN")
JACQUARD_PRELUDE = os.environ.get("JACQUARD_PRELUDE")

# The term sees the raw request bytes as Latin-1 codepoints and answers with a
# complete HTTP/1.1 response whose body reports the request length. All HTTP
# meaning is here, in Jacquard; the host only frames, bounds, and relays.
DECLS = """\
serve : (Text) ->{} Text
serve(request) = {
  let body = text.concat("bytes=", text.from-int(text.length(request)))
  text.concat(
    "HTTP/1.1 200 OK\\r\\nContent-Type: text/plain\\r\\nConnection: close\\r\\nContent-Length: ",
    text.concat(text.from-int(text.length(body)), text.concat("\\r\\n\\r\\n", body)))
}
"""


def talk(address, data: bytes) -> bytes:
    with socket.create_connection(address, timeout=10) as client:
        client.sendall(data)
        chunks = []
        while True:
            chunk = client.recv(65536)
            if not chunk:
                return b"".join(chunks)
            chunks.append(chunk)


@unittest.skipUnless(JACQUARD_BIN and JACQUARD_PRELUDE and os.path.isdir(JACQUARD_PRELUDE), "set JACQUARD_BIN and JACQUARD_PRELUDE to run the socket host against Core")
class LiveSocketHostTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.scratch = tempfile.mkdtemp(prefix="jacquard-host-socket-live-")
        decls = os.path.join(cls.scratch, "serve.jac")
        with open(decls, "w", encoding="utf-8") as stream:
            stream.write(DECLS)
        cls.store = os.path.join(cls.scratch, "store")
        env = dict(os.environ, JACQUARD_PRELUDE=os.path.abspath(JACQUARD_PRELUDE))
        subprocess.run([JACQUARD_BIN, "run", decls, "--store", cls.store], check=True, stdout=subprocess.DEVNULL, env=env)
        command, prelude = [JACQUARD_BIN], JACQUARD_PRELUDE
        names = hash_file(decls, command=command, prelude=prelude) | hash_file(os.path.join(prelude, "01-prim.jqd"), command=command, prelude=prelude)
        cls.target = names["serve"]
        cls.interface = parse_signature(signatures(decls, command=command, prelude=prelude)["serve"], names)

    @classmethod
    def tearDownClass(cls) -> None:
        shutil.rmtree(cls.scratch, ignore_errors=True)

    def test_requests_are_answered_by_the_jacquard_term(self) -> None:
        host = Host(self.store, command=[JACQUARD_BIN], deadline_seconds=30)
        with SerialSocketHost.for_term(host, self.target, self.interface, ceilings=Ceilings(max_request_bytes=4096)) as server:
            thread = threading.Thread(target=lambda: [server.serve_one(accept_timeout=10) for _ in range(3)], daemon=True)
            thread.start()
            plain_request = b"GET /hello HTTP/1.1\r\nHost: localhost\r\n\r\n"
            binary_request = b"POST /bin HTTP/1.1\r\nContent-Length: 256\r\n\r\n" + bytes(range(256))
            plain = talk(server.address, plain_request)
            binary = talk(server.address, binary_request)
            too_big = talk(server.address, b"POST /big HTTP/1.1\r\nContent-Length: 5000\r\n\r\n")
            thread.join(30)
        self.assertTrue(plain.startswith(b"HTTP/1.1 200 OK\r\n"), plain)
        self.assertTrue(plain.endswith(b"\r\n\r\nbytes=%d" % len(plain_request)), plain)
        self.assertTrue(binary.endswith(b"bytes=%d" % len(binary_request)), binary)
        self.assertEqual(too_big, b"")
        outcomes = [s.outcome for s in server.served]
        self.assertEqual(outcomes, ["served", "served", "oversized"])
        self.assertEqual(server.served[0].invocation.exit_status, 0)
        self.assertEqual(server.served[1].invocation.requests, [])


if __name__ == "__main__":
    unittest.main()
