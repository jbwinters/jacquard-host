// Generated from core-pin.json by scripts/core_pin.py; do not edit by hand.

package protocol

// The Jacquard Core release and host protocol this adapter is pinned to.
const (
	Protocol    = "jacquard-host-v0"
	Carrier     = "stdio-u32-json-v0"
	CoreVersion = "0.3.0"
	CoreTag     = "jacquard-core-0.3.0"
	CoreCommit  = "daf87fb4837cc69339116746818f64821dc4cb8e"
)

// KitDigests are the SHA-256 digests of the pinned conformance kit files.
var KitDigests = map[string]string{
	"kit/kit.json":         "a01b9401d7c2df4e17771b9c0d22f9ea5ce1a923dfb7d3396c27821c0e058a61",
	"kit/transcripts.json": "ebaf590e3d1b7e8399f56de5ad85043ac3658416dd7f0af7b55b9f9f3ce93240",
	"kit/fixtures.jac":     "037bb8209ca0344ba0a2cd0fcc16e69f13c8f9b91e3404dc3f3161e351831d80",
	"vectors.json":         "9ba1bff8eee0b6dbea6475dc0c7e9cc6313d847ad80bd8d832457115eb1c738a",
}

// PendingDecisions are decisions Core recorded as pending; replay reproduces them.
var PendingDecisions = []string{"noncanonical-target-hash"}
