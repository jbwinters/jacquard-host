"""Host integrations for Jacquard.

The package speaks the frozen ``jacquard-host-v0`` protocol over the
``stdio-u32-json-v0`` carrier, pinned in :mod:`jacquard_host.compat` and
``docs/COMPATIBILITY.md``. It includes the embedding adapter, strict carrier
codecs, conformance-kit replayer, and synthetic serial integration.
"""

from jacquard_host.compat import CARRIER, CORE_VERSION, PROTOCOL

__version__ = "0.1.0"

__all__ = ["CARRIER", "CORE_VERSION", "PROTOCOL", "__version__"]
