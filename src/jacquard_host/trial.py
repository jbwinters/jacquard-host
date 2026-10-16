"""The synthetic readability-trial slice (roadmap H1.3).

Assembles the three first-party Jacquard files (``http.jac``, ``study.jac``,
``trial.jac``) into one source, populates a Core store with it, resolves the
identities the protocol needs, binds the one persistence operation, and
serves ``trial.serve : (Text) ->{Study} Text`` behind the serial socket host.
It is an integration proof over synthetic data: not the readability study, not
participant collection, not a public service.
"""

from __future__ import annotations

import hashlib
import os
import subprocess
import tempfile
from pathlib import Path
from typing import Sequence

from jacquard_host.adapter import Host
from jacquard_host.identities import hash_file, parse_signature, signatures
from jacquard_host.protocol.envelopes import Interface
from jacquard_host.protocol.values import Hash
from jacquard_host.registry import OperationRegistry
from jacquard_host.socket_host import Ceilings, SerialSocketHost
from jacquard_host.study import StudyIdentities, StudyStore, bind_study
from jacquard_host.worker import DEFAULT_COMMAND

JACQUARD_DIR = Path(__file__).resolve().parent / "data" / "jacquard"
SOURCES: tuple[str, ...] = ("http.jac", "study.jac", "trial.jac")


def assemble(directory: Path | str = JACQUARD_DIR, sources: Sequence[str] = SOURCES) -> str:
    """Concatenate the first-party sources in dependency order (no import syntax)."""
    return "\n".join((Path(directory) / name).read_text(encoding="utf-8") for name in sources) + "\n"


class TrialApp:
    """Everything needed to serve the synthetic trial from one directory."""

    def __init__(
        self,
        workdir: str | os.PathLike[str],
        *,
        prelude: str | os.PathLike[str],
        command: Sequence[str] = DEFAULT_COMMAND,
        sources: Path | str = JACQUARD_DIR,
        deadline_seconds: float = 30.0,
    ) -> None:
        self.workdir = Path(workdir)
        self.workdir.mkdir(parents=True, exist_ok=True)
        self.command = tuple(command)
        self.prelude = os.path.abspath(os.fspath(prelude))
        self.source_path = self.workdir / "trial-all.jac"
        source = assemble(sources)
        digest = hashlib.sha256(source.encode("utf-8")).hexdigest()
        self.store_dir = self.workdir / "store"
        marker = self.workdir / "store.source.sha256"
        if self.store_dir.exists():
            # the store is Core-owned state built from one exact source; a different
            # source means a different store, never a repopulation in place
            recorded = marker.read_text(encoding="utf-8").strip() if marker.exists() else ""
            if recorded != digest:
                raise RuntimeError(
                    f"{self.store_dir} was populated from a different source (recorded {recorded[:12] or 'nothing'}, "
                    f"assembled {digest[:12]}); use a fresh work directory"
                )
        self.source_path.write_text(source, encoding="utf-8")
        if not self.store_dir.exists():
            env = dict(os.environ, JACQUARD_PRELUDE=self.prelude, TMPDIR=tempfile.gettempdir())
            completed = subprocess.run(
                [*self.command, "run", str(self.source_path), "--store", str(self.store_dir)],
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                env=env,
                check=False,
            )
            if completed.returncode != 0:
                raise RuntimeError(f"populating the trial store exited {completed.returncode}")
            marker.write_text(digest + "\n", encoding="utf-8")
        prim = hash_file(os.path.join(self.prelude, "01-prim.jqd"), command=self.command, prelude=self.prelude)
        self.names: dict[str, Hash] = prim | hash_file(self.source_path, command=self.command, prelude=self.prelude)
        self.interface: Interface = parse_signature(
            signatures(self.source_path, command=self.command, prelude=self.prelude)["trial.serve"], self.names
        )
        self.identities = StudyIdentities.from_names(self.names)
        self.study = StudyStore(self.workdir / "study.sqlite")
        self.registry = OperationRegistry()
        bind_study(self.registry, self.identities, self.study)
        self.host = Host(
            self.store_dir, command=self.command, deadline_seconds=deadline_seconds,
            env=dict(os.environ, JACQUARD_PRELUDE=self.prelude),
        )

    @property
    def target(self) -> Hash:
        return self.names["trial.serve"]

    def serve_text(self, raw_request: str):
        """One request through the worker without a socket (for tests and tools)."""
        return self.host.invoke(self.target, self.interface, (raw_request,), self.registry)

    def server(self, *, bind: tuple[str, int] = ("127.0.0.1", 0), ceilings: Ceilings = Ceilings(), backlog: int = 1) -> SerialSocketHost:
        return SerialSocketHost.for_term(self.host, self.target, self.interface, self.registry, bind=bind, ceilings=ceilings, backlog=backlog)

    def close(self) -> None:
        self.study.close()

    def __enter__(self) -> "TrialApp":
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()


def main(argv: Sequence[str] | None = None) -> int:
    """Serve the synthetic trial on loopback until interrupted."""
    import argparse
    import signal

    parser = argparse.ArgumentParser(description="Serve the synthetic readability trial on loopback (integration proof, synthetic data only).")
    parser.add_argument("--workdir", required=True, help="directory for the assembled source, the Core store, and study.sqlite")
    parser.add_argument("--prelude", default=os.environ.get("JACQUARD_PRELUDE"), help="Core prelude directory")
    parser.add_argument("--jacquard", default=os.environ.get("JACQUARD_BIN", DEFAULT_COMMAND[0]), help="worker executable")
    parser.add_argument("--port", type=int, default=0, help="loopback port (0 picks a free one)")
    parser.add_argument("--max-request-bytes", type=int, default=8192)
    args = parser.parse_args(argv)
    if not args.prelude:
        parser.error("--prelude (or JACQUARD_PRELUDE) is required")
    with TrialApp(args.workdir, prelude=args.prelude, command=[args.jacquard]) as app:
        with app.server(bind=("127.0.0.1", args.port), ceilings=Ceilings(max_request_bytes=args.max_request_bytes)) as server:
            host, port = server.address
            print(f"serving the synthetic trial on http://{host}:{port}/trial (one request at a time; Ctrl-C to stop)", flush=True)
            signal.signal(signal.SIGINT, lambda *_: server.stop())
            signal.signal(signal.SIGTERM, lambda *_: server.stop())
            server.serve_forever()
            for record in server.served[-10:]:
                print(f"{record.outcome:12} {record.request_bytes:6}B -> {record.response_bytes:6}B {record.detail}")
    return 0


if __name__ == "__main__":
    import sys

    sys.exit(main())
