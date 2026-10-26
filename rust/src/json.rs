//! Strict UTF-8 JSON for the carrier: duplicate keys, lone surrogates,
//! non-integer numbers, integers outside Core's 63-bit range, trailing data,
//! and nesting deeper than the selected limit are refused. Encoding is
//! canonical: object keys sorted by code point, no whitespace, and the same
//! string escaping as JavaScript's `JSON.stringify`.

use std::collections::BTreeMap;
use std::fmt::Write as _;

use crate::error::ProtocolError;

/// The smallest and largest Core `Int` and JSON integer.
pub const INT_MIN: i64 = -(1 << 62);
/// The largest Core `Int` and JSON integer.
pub const INT_MAX: i64 = (1 << 62) - 1;

/// A decoded JSON value. Objects keep keys in code-point order.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Json {
    Null,
    Bool(bool),
    Int(i64),
    Str(String),
    Array(Vec<Json>),
    Object(Object),
}

/// A JSON object.
pub type Object = BTreeMap<String, Json>;

impl Json {
    /// The object, if this is one.
    pub fn as_object(&self) -> Option<&Object> {
        match self {
            Json::Object(object) => Some(object),
            _ => None,
        }
    }

    /// The string, if this is one.
    pub fn as_str(&self) -> Option<&str> {
        match self {
            Json::Str(text) => Some(text),
            _ => None,
        }
    }

    /// The array, if this is one.
    pub fn as_array(&self) -> Option<&[Json]> {
        match self {
            Json::Array(items) => Some(items),
            _ => None,
        }
    }

    /// The integer, if this is one.
    pub fn as_int(&self) -> Option<i64> {
        match self {
            Json::Int(value) => Some(*value),
            _ => None,
        }
    }
}

impl From<&str> for Json {
    fn from(text: &str) -> Self {
        Json::Str(text.to_owned())
    }
}

impl From<String> for Json {
    fn from(text: String) -> Self {
        Json::Str(text)
    }
}

impl From<i64> for Json {
    fn from(value: i64) -> Self {
        Json::Int(value)
    }
}

impl From<Object> for Json {
    fn from(object: Object) -> Self {
        Json::Object(object)
    }
}

impl From<Vec<Json>> for Json {
    fn from(items: Vec<Json>) -> Self {
        Json::Array(items)
    }
}

/// Builds an object from key/value pairs.
pub fn object<const N: usize>(pairs: [(&str, Json); N]) -> Object {
    pairs.into_iter().map(|(key, value)| (key.to_owned(), value)).collect()
}

/// Decodes one JSON value under the strict rules.
pub fn decode(payload: &[u8], max_depth: usize) -> Result<Json, ProtocolError> {
    let text = std::str::from_utf8(payload).map_err(|_| ProtocolError::malformed("payload is not valid UTF-8"))?;
    let mut decoder = Decoder { text: text.as_bytes(), source: text, pos: 0, max_depth };
    let value = decoder.value(1)?;
    decoder.whitespace();
    if decoder.pos != decoder.text.len() {
        return Err(ProtocolError::malformed("payload is not one JSON value"));
    }
    Ok(value)
}

/// Decodes one JSON object.
pub fn decode_object(payload: &[u8], max_depth: usize) -> Result<Object, ProtocolError> {
    match decode(payload, max_depth)? {
        Json::Object(object) => Ok(object),
        _ => Err(ProtocolError::malformed("expected a JSON object")),
    }
}

struct Decoder<'a> {
    text: &'a [u8],
    source: &'a str,
    pos: usize,
    max_depth: usize,
}

impl Decoder<'_> {
    fn peek(&self) -> Option<u8> {
        self.text.get(self.pos).copied()
    }

    fn whitespace(&mut self) {
        while matches!(self.peek(), Some(b' ' | b'\t' | b'\r' | b'\n')) {
            self.pos += 1;
        }
    }

    fn value(&mut self, depth: usize) -> Result<Json, ProtocolError> {
        self.whitespace();
        match self.peek() {
            Some(b'"') => return self.quoted().map(Json::Str),
            Some(open @ (b'{' | b'[')) => {
                if depth > self.max_depth {
                    return Err(ProtocolError::exceeded("JSON nesting exceeds max_json_depth"));
                }
                return self.container(open, depth);
            }
            _ => {}
        }
        for (literal, value) in [("true", Json::Bool(true)), ("false", Json::Bool(false)), ("null", Json::Null)] {
            if self.source[self.pos..].starts_with(literal) {
                self.pos += literal.len();
                return Ok(value);
            }
        }
        let start = self.pos;
        if self.peek() == Some(b'-') {
            self.pos += 1;
        }
        match self.peek() {
            Some(b'0') => self.pos += 1,
            Some(b'1'..=b'9') => {
                while matches!(self.peek(), Some(b'0'..=b'9')) {
                    self.pos += 1;
                }
            }
            _ => return Err(ProtocolError::malformed("invalid JSON scalar")),
        }
        let token = &self.source[start..self.pos];
        if token.len() > 20 {
            return Err(ProtocolError::malformed("invalid JSON scalar"));
        }
        match token.parse::<i64>() {
            Ok(value) if (INT_MIN..=INT_MAX).contains(&value) => Ok(Json::Int(value)),
            _ => Err(ProtocolError::malformed("JSON integer is outside the 63-bit range")),
        }
    }

    fn container(&mut self, open: u8, depth: usize) -> Result<Json, ProtocolError> {
        self.pos += 1;
        self.whitespace();
        let close = if open == b'{' { b'}' } else { b']' };
        let mut items = Vec::new();
        let mut fields = Object::new();
        let finish = |items: Vec<Json>, fields: Object| if open == b'{' { Json::Object(fields) } else { Json::Array(items) };
        if self.peek() == Some(close) {
            self.pos += 1;
            return Ok(finish(items, fields));
        }
        while self.pos < self.text.len() {
            if open == b'[' {
                items.push(self.value(depth + 1)?);
            } else {
                if self.peek() != Some(b'"') {
                    return Err(ProtocolError::malformed("object key must be a string"));
                }
                let key = self.quoted()?;
                self.whitespace();
                if fields.contains_key(&key) {
                    return Err(ProtocolError::malformed("duplicate JSON field name"));
                }
                if self.peek() != Some(b':') {
                    return Err(ProtocolError::malformed("expected a colon"));
                }
                self.pos += 1;
                let value = self.value(depth + 1)?;
                fields.insert(key, value);
            }
            self.whitespace();
            if self.peek() == Some(close) {
                self.pos += 1;
                return Ok(finish(items, fields));
            }
            if self.peek() != Some(b',') {
                return Err(ProtocolError::malformed("expected a comma"));
            }
            self.pos += 1;
            self.whitespace();
        }
        Err(ProtocolError::malformed("unterminated JSON container"))
    }

    fn hex4(&mut self) -> Option<u32> {
        let digits = self.source.get(self.pos..self.pos + 4)?;
        let value = u32::from_str_radix(digits, 16).ok()?;
        if !digits.bytes().all(|byte| byte.is_ascii_hexdigit()) {
            return None;
        }
        self.pos += 4;
        Some(value)
    }

    fn quoted(&mut self) -> Result<String, ProtocolError> {
        self.pos += 1;
        let mut out = String::new();
        loop {
            let Some(byte) = self.peek() else {
                return Err(ProtocolError::malformed("unterminated JSON string"));
            };
            match byte {
                b'"' => {
                    self.pos += 1;
                    return Ok(out);
                }
                0x00..=0x1f => return Err(ProtocolError::malformed("invalid JSON string")),
                b'\\' => {
                    self.pos += 1;
                    let escape = self.peek().ok_or_else(|| ProtocolError::malformed("unterminated JSON string"))?;
                    self.pos += 1;
                    let simple = match escape {
                        b'"' => Some('"'),
                        b'\\' => Some('\\'),
                        b'/' => Some('/'),
                        b'b' => Some('\u{8}'),
                        b'f' => Some('\u{c}'),
                        b'n' => Some('\n'),
                        b'r' => Some('\r'),
                        b't' => Some('\t'),
                        b'u' => None,
                        _ => return Err(ProtocolError::malformed("invalid JSON string")),
                    };
                    if let Some(character) = simple {
                        out.push(character);
                        continue;
                    }
                    let unit = self.hex4().ok_or_else(|| ProtocolError::malformed("invalid JSON string"))?;
                    let scalar = if (0xd800..0xdc00).contains(&unit) {
                        if !self.source[self.pos..].starts_with("\\u") {
                            return Err(ProtocolError::malformed("a JSON string contains a lone surrogate"));
                        }
                        self.pos += 2;
                        let low = self.hex4().ok_or_else(|| ProtocolError::malformed("invalid JSON string"))?;
                        if !(0xdc00..0xe000).contains(&low) {
                            return Err(ProtocolError::malformed("a JSON string contains a lone surrogate"));
                        }
                        0x10000 + ((unit - 0xd800) << 10) + (low - 0xdc00)
                    } else if (0xdc00..0xe000).contains(&unit) {
                        return Err(ProtocolError::malformed("a JSON string contains a lone surrogate"));
                    } else {
                        unit
                    };
                    out.push(char::from_u32(scalar).ok_or_else(|| ProtocolError::malformed("invalid JSON string"))?);
                }
                _ => {
                    // Copy one UTF-8 scalar; the payload was validated as UTF-8.
                    let rest = &self.source[self.pos..];
                    let character = rest.chars().next().expect("nonempty");
                    out.push(character);
                    self.pos += character.len_utf8();
                }
            }
        }
    }
}

/// Encodes a JSON value canonically.
pub fn stringify(value: &Json, max_depth: usize) -> Result<String, ProtocolError> {
    let mut out = String::new();
    write(&mut out, value, max_depth, 1)?;
    Ok(out)
}

fn write(out: &mut String, value: &Json, max_depth: usize, depth: usize) -> Result<(), ProtocolError> {
    match value {
        Json::Null => out.push_str("null"),
        Json::Bool(flag) => out.push_str(if *flag { "true" } else { "false" }),
        Json::Int(number) => {
            if !(INT_MIN..=INT_MAX).contains(number) {
                return Err(ProtocolError::malformed("JSON integer is outside the 63-bit range"));
            }
            let _ = write!(out, "{number}");
        }
        Json::Str(text) => quote(out, text),
        Json::Array(items) => {
            if depth > max_depth {
                return Err(ProtocolError::exceeded("JSON nesting exceeds max_json_depth"));
            }
            out.push('[');
            for (index, item) in items.iter().enumerate() {
                if index > 0 {
                    out.push(',');
                }
                write(out, item, max_depth, depth + 1)?;
            }
            out.push(']');
        }
        Json::Object(fields) => {
            if depth > max_depth {
                return Err(ProtocolError::exceeded("JSON nesting exceeds max_json_depth"));
            }
            out.push('{');
            for (index, (key, item)) in fields.iter().enumerate() {
                if index > 0 {
                    out.push(',');
                }
                quote(out, key);
                out.push(':');
                write(out, item, max_depth, depth + 1)?;
            }
            out.push('}');
        }
    }
    Ok(())
}

fn quote(out: &mut String, text: &str) {
    out.push('"');
    for character in text.chars() {
        match character {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\u{8}' => out.push_str("\\b"),
            '\u{c}' => out.push_str("\\f"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            control if (control as u32) < 0x20 => {
                let _ = write!(out, "\\u{:04x}", control as u32);
            }
            other => out.push(other),
        }
    }
    out.push('"');
}

/// Encodes one carrier frame: a big-endian u32 length and the payload,
/// refusing payloads outside `1..=max_bytes`.
pub fn frame_bytes(frame: &Object, max_bytes: usize, max_depth: usize) -> Result<Vec<u8>, ProtocolError> {
    let payload = stringify(&Json::Object(frame.clone()), max_depth)?;
    if payload.is_empty() || payload.len() > max_bytes {
        return Err(ProtocolError::exceeded("payload exceeds max_frame_bytes"));
    }
    let mut bytes = (payload.len() as u32).to_be_bytes().to_vec();
    bytes.extend_from_slice(payload.as_bytes());
    Ok(bytes)
}
