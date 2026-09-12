# Limits

These are claim boundaries for the first host integration, not background
aspirations. Evidence may remove a limit only through a reviewed change that
also updates this document.

| Initial limit | Consequence | Evidence required to remove it |
|---|---|---|
| First-party Jacquard programs only | Capability checks do not make hostile uploaded code safe. | A separate-process or equivalent isolation design, adversarial review, resource ceilings, and a narrower public threat model. |
| Trusted host and operating system | A malicious or buggy adapter can misreport or perform actions outside Jacquard. | An authenticated, independently enforced host boundary and external security review; canonical hashes alone are insufficient. |
| Application capability boundary, not OS sandbox | Declared and checked effects are review and application enforcement, not kernel isolation. | An explicit OS/WebAssembly/container isolation layer with its own evidence and deployment contract. |
| Pinned pre-merge conformance kit | The adapter claims only that it replays the pinned kit; Core's evidence pack for the kit is not yet published. | Core's Task 206 closes with its evidence and limitations statement and the pin in `COMPATIBILITY.md` is refreshed to the merged commit. |
| Experimental process carrier | Message framing may change and is not a permanent ABI promise. | Two independent adapters and the serial application pass the same frozen fixtures before protocol v1 is declared stable. |
| Handlers run inside the adapter's read loop | A Python operation handler that blocks holds the worker until it returns; the host deadline covers only reads from the worker. | A handler timeout policy with its own cancel/unknown-completion evidence, once an application needs one. |
| Serial execution | One request is processed at a time; no throughput or concurrency claim exists. | Measured need, a reviewed Core C4 contract, cancellation/leak evidence, schedule provenance, and strict replay boundaries. |
| Loopback listener by default | The integration is not a public network service. | Explicit non-loopback configuration and authority, deployment hardening, external review, and supported operational evidence. |
| No TLS, authentication, proxy semantics, or abuse prevention | Those are ordinary deployment responsibilities outside Jacquard Host. | They remain outside this repository unless a concrete generic host requirement is separately approved; no vendor-specific coupling is implied. |
| No outbound network in the first application | The synthetic trial cannot call APIs, analytics, or email services. | A concrete application need and reviewed host/port authority with refusal evidence. |
| Host-owned SQLite through one named operation | Jacquard receives neither raw SQL nor generic filesystem authority. | Repeated concrete operations justify a reusable storage contract with migration, recovery, and authority evidence. |
| No automatic side-effect retry | An ambiguous completion fails rather than risking duplicate external work. | Operation-specific idempotency keys, authenticated receipts, and crash/recovery evidence. |
| Exact replay uses controlled sanitized fixtures | Production traffic is not silently archived for replay. | A reviewed data classification, consent/retention policy, redaction proof, and secure evidence store. |
| Linux deployment first; macOS development support | Windows and other hosts have no support claim. | CI, lifecycle, path/socket, packaging, and cleanup evidence on each added platform. |
| Synthetic readability slice only | No participant result or readability claim may be produced here. | A separate study repository with approved protocol, consent, privacy, retention, accessibility, and publication authority. |

The integration also inherits every applicable limitation of its pinned
Jacquard Core release, including coarse 0.2 world grants and the absence of a
production-security claim.
