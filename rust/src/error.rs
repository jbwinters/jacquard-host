//! Frozen v0 protocol error codes.

use std::fmt;

/// A protocol refusal carrying its frozen v0 diagnostic code, such as E1601.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ProtocolError {
    pub code: &'static str,
    pub message: String,
}

impl ProtocolError {
    pub(crate) fn new(code: &'static str, message: impl Into<String>) -> Self {
        ProtocolError { code, message: message.into() }
    }
    /// E1601: malformed input.
    pub(crate) fn malformed(message: impl Into<String>) -> Self {
        Self::new("E1601", message)
    }
    /// E1602: a selected or hard limit is exceeded.
    pub(crate) fn exceeded(message: impl Into<String>) -> Self {
        Self::new("E1602", message)
    }
    /// E1604: a value or type with no v0 encoding.
    pub(crate) fn unsupported(message: impl Into<String>) -> Self {
        Self::new("E1604", message)
    }
    /// E1608: a message out of order for the session state.
    pub(crate) fn order(message: impl Into<String>) -> Self {
        Self::new("E1608", message)
    }
}

impl fmt::Display for ProtocolError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}: {}", self.code, self.message)
    }
}

impl std::error::Error for ProtocolError {}
