# Agent Notes

This repository is an integration consumer of Jacquard Core, not an alternate
language implementation. Before changing it, read:

1. `README.md`
2. `docs/ROADMAP.md`
3. `docs/LIMITS.md`
4. `docs/COMPATIBILITY.md`

Read the pinned Jacquard host-boundary specification and conformance fixtures
before implementing any protocol behavior. If the pin still says that no
protocol is released, stop at scaffolding, tests, or documentation; do not
invent messages that Core has not specified.

## Ownership rules

- Jacquard Core owns language semantics, checked effect requirements,
  canonical identity, runtime grants, diagnostics, and the host contract.
- This repository owns adapters, sockets, persistence, deployment-facing
  resource lifetimes, and integration evidence.
- The real readability study and participant data belong in a separate
  application repository.
- Proxy products are outside the program model. Do not add nginx-,
  Cloudflare-, or forwarded-header semantics to the host contract.

## Safety rules

- Treat the initial programs as first-party code. Do not claim hostile-code
  isolation.
- Treat the host and operating system as trusted components and say so.
- Use a closed, configured operation registry. Never expose arbitrary command
  execution, raw SQL, unrestricted filesystem access, or a general RPC bus.
- Do not automatically retry a side effect after an ambiguous failure.
- Default network listeners to loopback. Public binding, TLS, authentication,
  proxy integration, and abuse prevention are separate deployment work.
- Keep HTTP meaning in Jacquard application code. A host may impose byte,
  count, and time ceilings for resource safety without interpreting a request.
- Use only synthetic data until a separate study repository has explicit
  consent, privacy, retention, and publication authority.
- Record limitations beside evidence and name the condition required to remove
  each one.

## Development rules

- Keep the first carrier serial, bounded, versioned, and fail-closed.
- Preserve transport independence: process framing is one carrier, not the
  semantic definition of the boundary.
- Start with the Python adapter, then prove the same fixtures independently in
  Rust after the protocol stabilizes.
- Add tests with behavior. Use the standard library until a reviewed need
  justifies a dependency.
- Work on branches and merge through pull requests. The empty repository seed
  is the only direct initialization commit on `main`.
- Keep temporary files under `.scratch/` and secrets out of the repository.

Task Master is local planning state and is ignored by Git. Use it only to
mirror the reviewed roadmap:

```sh
task-master next
task-master show <id>
task-master set-status <id> in-progress
task-master validate-dependencies
```
