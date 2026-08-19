# Compatibility

## Planning baseline

The repository currently plans against:

- Jacquard release: `jacquard-core-0.2.0`
- release commit: `aceca65e364db6dcf91cbdd54586c136646dfa37`
- host protocol: **not released**
- conformance fixture set: **not released**

The release pin identifies the semantic baseline; it does not imply that Core
0.2 contains a foreign-host ABI or process protocol. It does not.

## Adoption rule

Implementation of an adapter begins only after `jacquard-lang` publishes all
of the following together:

1. a versioned, transport-neutral host contract;
2. a concrete carrier version;
3. canonical positive and hostile conformance fixtures;
4. explicit bounds and unknown-input behavior; and
5. an evidence and limitations statement.

This repository pins that exact set before consuming it. Adapter-specific
normalization may not weaken, repair, or silently reinterpret a fixture.
Protocol divergence is reported upstream or fixed in the adapter; it is never
hidden behind a language-specific compatibility rule.

The process carrier is expected to be provisional. A later native or
WebAssembly carrier may replace it while preserving the transport-neutral
invocation, effect, diagnostic, cancellation, and evidence contract.
