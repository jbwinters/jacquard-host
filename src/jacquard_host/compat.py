"""The exact Jacquard Core artifacts this adapter is pinned to.

`docs/COMPATIBILITY.md` is the reviewable record; the test suite checks that
the vendored kit under `conformance/` and that document agree with these
constants, so the pin cannot drift silently in either direction.
"""

from __future__ import annotations

PROTOCOL = "jacquard-host-v0"
CARRIER = "stdio-u32-json-v0"
CORE_VERSION = "0.2.0"

# jacquard-lang main after the squash merge of pull request 115 (the
# conformance kit). The kit files are byte-identical to the reviewed branch
# head c9947c2 they were first pinned from; the file digests below remain the
# durable pin and the commit records where the kit lives on Core main.
CORE_COMMIT = "61575c7bcd98c8922e739de647556d1368729ad8"

# SHA-256 of the kit files as published under spec/host-protocol-v0/ in Core,
# keyed by their path relative to that directory. The vendored copies under
# conformance/host-protocol-v0/ must match byte for byte.
KIT_DIGESTS = {
    "kit/kit.json": "a286328c457696a898aaa6731f0044fb90c78d8657d57270384a76976b070b53",
    "kit/transcripts.json": "ebaf590e3d1b7e8399f56de5ad85043ac3658416dd7f0af7b55b9f9f3ce93240",
    "kit/fixtures.jac": "037bb8209ca0344ba0a2cd0fcc16e69f13c8f9b91e3404dc3f3161e351831d80",
    "vectors.json": "9ba1bff8eee0b6dbea6475dc0c7e9cc6313d847ad80bd8d832457115eb1c738a",
}

# Decisions Core has recorded as pending; the adapter must not paper over them.
PENDING_DECISIONS = ("noncanonical-target-hash",)
