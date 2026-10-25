"""The exact Jacquard Core release this adapter is pinned to.

Generated from core-pin.json by scripts/core_pin.py; do not edit by hand.
Run `python3 scripts/core_pin.py update --tag ...` to adopt a new release.
"""

from __future__ import annotations

PROTOCOL = "jacquard-host-v0"
CARRIER = "stdio-u32-json-v0"
CORE_VERSION = "0.3.0"
CORE_TAG = "jacquard-core-0.3.0"
CORE_COMMIT = "daf87fb4837cc69339116746818f64821dc4cb8e"

# SHA-256 of the kit files under spec/host-protocol-v0/ at CORE_COMMIT. The
# vendored copies under data/conformance/host-protocol-v0/ must match.
KIT_DIGESTS = {
    "kit/kit.json": "a01b9401d7c2df4e17771b9c0d22f9ea5ce1a923dfb7d3396c27821c0e058a61",
    "kit/transcripts.json": "ebaf590e3d1b7e8399f56de5ad85043ac3658416dd7f0af7b55b9f9f3ce93240",
    "kit/fixtures.jac": "037bb8209ca0344ba0a2cd0fcc16e69f13c8f9b91e3404dc3f3161e351831d80",
    "vectors.json": "9ba1bff8eee0b6dbea6475dc0c7e9cc6313d847ad80bd8d832457115eb1c738a",
}

# Decisions Core has recorded as pending; the adapter must not paper over them.
PENDING_DECISIONS = ("noncanonical-target-hash",)
