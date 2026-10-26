"""Build an sdist, rebuild its wheel, and exercise the installed library.

Requires setuptools, wheel, and pip in the development interpreter. No network
or Core build is needed. Temporary artifacts stay under .scratch/.
"""

from __future__ import annotations

import os
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import tempfile

REPO = Path(__file__).resolve().parents[1]


def run(*args: str, cwd: Path, env: dict[str, str] | None = None) -> None:
    completed = subprocess.run(args, cwd=cwd, env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    if completed.returncode:
        print(completed.stdout, file=sys.stderr)
        completed.check_returncode()


def main() -> None:
    scratch = REPO / ".scratch"
    scratch.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="distribution-", dir=scratch) as temporary:
        root = Path(temporary)
        dist = root / "dist"
        dist.mkdir()
        run(sys.executable, "-c", "from setuptools.build_meta import build_sdist;import sys;build_sdist(sys.argv[1])", str(dist), cwd=REPO)
        with tarfile.open(next(dist.glob("*.tar.gz"))) as archive:
            # Only our freshly built archive is extracted, never an external input.
            archive.extractall(root / "source", filter="data")
        source = next((root / "source").iterdir())
        for relative in ("LICENSE", "NOTICE", "go/NOTICE", "rust/NOTICE", "CONTRIBUTING.md", "CHANGELOG.md", "docs/SETUP.md", "scripts/check_examples.py",
                         "examples/python_sdk/app.py", "examples/python_sdk/exercise.py"):
            assert (source / relative).is_file(), f"sdist missing {relative}"
        for project in ("quickstart", "triage", "planner", "fulfillment"):
            for filename in ("README.md", "rules.jac", "run.py" if project == "quickstart" else "input.json"):
                relative = Path("examples") / project / filename
                assert (source / relative).is_file(), f"sdist missing {relative}"
        run(sys.executable, "-c", "from setuptools.build_meta import build_wheel;import sys;build_wheel(sys.argv[1])", str(dist), cwd=source)
        installed = root / "installed"
        run(sys.executable, "-m", "pip", "install", "--no-index", "--no-deps", "--no-compile", "--target", str(installed), str(next(dist.glob("*.whl"))), cwd=root)
        outside = root / "outside"
        outside.mkdir()
        shutil.copyfile(REPO / "tests" / "replay_worker.py", outside / "replay_worker.py")
        env = dict(os.environ, PYTHONPATH=str(installed))
        run(sys.executable, "-c", '''
import importlib.metadata
import os
from pathlib import Path
import sys

import jacquard_host
from jacquard_host import Runtime, Some, Variant, JacquardError
from jacquard_host.adapter import Host
from jacquard_host.kit import load_kit, replay, verify_pins
from jacquard_host.protocol.envelopes import Interface
from jacquard_host.protocol.values import TupleType
from jacquard_host.trial import assemble

assert Path(jacquard_host.__file__).is_relative_to(Path(os.environ["PYTHONPATH"]))
assert importlib.metadata.version("jacquard-host") == jacquard_host.__version__
metadata = importlib.metadata.distribution("jacquard-host")
for filename in ("LICENSE", "NOTICE"):
    assert any(str(item).endswith("/" + filename) for item in metadata.files), filename
assert Path(jacquard_host.__file__).with_name("py.typed").is_file()
assert Variant("Red").name == "red"
assert Some(None).value is None
assert "trial.serve" in assemble()
kit = load_kit()
verify_pins(kit)
for transcript in kit.transcripts:
    env = dict(os.environ, REPLAY_TRANSCRIPTS=str(kit.directory / "kit" / "transcripts.json"), REPLAY_CASE=transcript["name"])
    results = replay([sys.executable, "replay_worker.py"], "unused", kit, names=[transcript["name"]], env=env)
    assert all(result.matches_recording for result in results), results
env["REPLAY_CASE"] = "pure-success"
host = Host("unused", command=[sys.executable, "replay_worker.py"], env=env)
result = host.invoke(kit.manifest["identities"]["kit-ready"], Interface((), TupleType()))
assert result.ok and result.value == (), result
print("installed library: version, assets, all 21 transcripts, and invocation pass")
''', cwd=outside, env=env)
        for command in ("jacquard-host-kit", "jacquard-host-trial"):
            run(str(installed / "bin" / command), "--help", cwd=outside, env=env)
    print("Distribution check passed (sdist -> wheel -> installation outside checkout).")


if __name__ == "__main__":
    main()
