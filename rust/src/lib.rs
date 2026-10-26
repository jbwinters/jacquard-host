//! An independent Rust adapter for Jacquard Core's experimental
//! `jacquard-host-v0` protocol: strict codecs, the host session state
//! machine, a bounded process carrier for `jacquard host worker`, a low-level
//! [`host::Host`] with a closed operation registry, and a replayer for Core's
//! pinned conformance kit. Standard library only.

pub mod carrier;
pub mod compat;
pub mod error;
pub mod host;
pub mod json;
pub mod kit;
pub mod protocol;
mod sha256;

pub use error::ProtocolError;

/// Compiles the README's example.
#[cfg(doctest)]
#[doc = include_str!("../README.md")]
struct ReadmeDoctests;
