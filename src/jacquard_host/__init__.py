"""Host integrations for Jacquard.

The package speaks the frozen ``jacquard-host-v0`` protocol over the
``stdio-u32-json-v0`` carrier, pinned in :mod:`jacquard_host.compat` and
``docs/COMPATIBILITY.md``. It includes the embedding adapter, strict carrier
codecs, conformance-kit replayer, and synthetic serial integration.
"""

from jacquard_host.compat import CARRIER, CORE_VERSION, PROTOCOL
from jacquard_host.data import Some, Variant
from jacquard_host.protocol.values import Hash
from jacquard_host.errors import BindingError, DeadlineExceeded, InvalidArgument, InvocationError, JacquardError, OperationFailed, PreparationError, WorkerLost
from jacquard_host.sdk import Function, Invocation, Program, Runtime

__version__ = "0.2.0"

__all__ = ["CARRIER", "CORE_VERSION", "PROTOCOL", "__version__", "Runtime", "Program", "Function", "Invocation",
           "Hash", "Some", "Variant", "JacquardError", "PreparationError", "BindingError", "InvalidArgument", "InvocationError",
           "DeadlineExceeded", "WorkerLost", "OperationFailed"]
