//! Public names and identities printed by Core's `hash` and `check
//! --print-sigs` commands.

use std::collections::{BTreeMap, BTreeSet};

use crate::protocol::{BoundaryType, Hash, Interface};

use super::Error;

/// One public name and identity printed by `jacquard hash`.
#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) struct Entry {
    pub name: String,
    pub hash: Hash,
}

/// One declaration group: a type or effect identity followed by its
/// constructors or operations, in Core's printed order.
pub(crate) type Group = Vec<Entry>;

/// Parses one `N:name hash` line.
fn hash_line(line: &str) -> Option<(&str, Entry)> {
    let (index, rest) = line.split_once(':')?;
    let (name, hash) = rest.split_once(' ')?;
    if index.is_empty() || !index.bytes().all(|byte| byte.is_ascii_digit()) || name.is_empty() || name.contains(char::is_whitespace) {
        return None;
    }
    let hash = Hash::parse(hash).ok()?;
    Some((index, Entry { name: name.to_owned(), hash }))
}

/// Parses `jacquard hash` output into declaration groups.
pub(crate) fn hash_groups(output: &str) -> Result<Vec<Group>, Error> {
    let mut order: Vec<&str> = Vec::new();
    let mut groups: BTreeMap<&str, Group> = BTreeMap::new();
    for line in output.split('\n') {
        let Some((index, entry)) = hash_line(line.trim()) else { continue };
        if !groups.contains_key(index) {
            order.push(index);
        }
        groups.entry(index).or_default().push(entry);
    }
    if order.is_empty() {
        return Err(Error::binding("Core hash command returned no public identities"));
    }
    Ok(order.into_iter().map(|index| groups.remove(index).unwrap_or_default()).collect())
}

/// Refuses a public name that denotes different identities in different
/// declaration groups.
pub(crate) fn check_groups(groups: &[Group]) -> Result<(), Error> {
    let mut seen: BTreeMap<&str, BTreeSet<&Hash>> = BTreeMap::new();
    for group in groups {
        let mut current: BTreeMap<&str, BTreeSet<&Hash>> = BTreeMap::new();
        for entry in group {
            current.entry(&entry.name).or_default().insert(&entry.hash);
        }
        for (name, hashes) in current {
            let previous = seen.entry(name).or_default();
            if !previous.is_empty() && !hashes.is_subset(previous) {
                return Err(Error::binding(format!(
                    "ambiguous public name {name} occurs in different declaration groups; use distinct names or explicit low-level identities"
                )));
            }
            previous.extend(hashes);
        }
    }
    Ok(())
}

/// Converts a display name such as `WorkToday` to its store spelling,
/// `work-today`; lowercase names are unchanged.
pub(crate) fn display_to_store(name: &str) -> String {
    if !name.starts_with(|c: char| c.is_uppercase()) {
        return name.to_owned();
    }
    let mut out = String::new();
    for (index, c) in name.char_indices() {
        if index > 0 && c.is_uppercase() {
            out.push('-');
        }
        out.extend(c.to_lowercase());
    }
    out
}

/// The form used to look up effects and operations: store spelling, with
/// Rust's `_` accepted for `-`.
pub(crate) fn normal(name: &str) -> String {
    display_to_store(name).replace('_', "-")
}

/// Tokens of a printed signature.
fn tokens(text: &str) -> Result<Vec<&str>, String> {
    let mut tokens = Vec::new();
    let mut rest = text.trim_start();
    while !rest.is_empty() {
        let length = if rest.starts_with("->{") {
            3
        } else if rest.starts_with(['(', ')', ',', '{', '}']) {
            1
        } else if rest.starts_with(|c: char| c.is_ascii_alphabetic()) {
            rest.find(|c: char| !(c.is_ascii_alphanumeric() || matches!(c, '.' | '?' | '!' | '-'))).unwrap_or(rest.len())
        } else {
            return Err(format!("unsupported signature near {:?}", rest.chars().take(12).collect::<String>()));
        };
        tokens.push(&rest[..length]);
        rest = rest[length..].trim_start();
    }
    Ok(tokens)
}

/// Reads a closed v0 signature printed by `check --print-sigs`, such as
/// `(List Line, Int) ->{Inventory} Option Text`. Polymorphic and
/// effect-polymorphic signatures are refused.
pub(crate) fn parse_signature(text: &str, names: &BTreeMap<String, Hash>) -> Result<Interface, String> {
    let mut parser = Parser { tokens: tokens(text)?, index: 0, names };
    parser.parse()
}

struct Parser<'a> {
    tokens: Vec<&'a str>,
    index: usize,
    names: &'a BTreeMap<String, Hash>,
}

impl Parser<'_> {
    fn peek(&self) -> &str {
        self.tokens.get(self.index).copied().unwrap_or("")
    }

    fn take(&mut self, expected: &str) -> Result<String, String> {
        let token = self.peek().to_owned();
        if token.is_empty() || (!expected.is_empty() && token != expected) {
            return Err(format!("expected {expected:?}, found {token:?}"));
        }
        self.index += 1;
        Ok(token)
    }

    fn identity(&self, display: &str) -> Result<Hash, String> {
        self.names
            .get(&display_to_store(display))
            .or_else(|| self.names.get(display))
            .cloned()
            .ok_or_else(|| format!("no identity for {display}"))
    }

    fn list(&mut self, close: &str) -> Result<Vec<BoundaryType>, String> {
        let mut out = Vec::new();
        if self.peek() == close {
            self.index += 1;
            return Ok(out);
        }
        loop {
            out.push(self.typ()?);
            if self.peek() == "," {
                self.index += 1;
                if self.peek() == close {
                    self.index += 1;
                    return Ok(out);
                }
                continue;
            }
            self.take(close)?;
            return Ok(out);
        }
    }

    fn atom(&mut self) -> Result<BoundaryType, String> {
        let token = self.take("")?;
        if token == "(" {
            let items = self.list(")")?;
            if items.len() == 1 && self.tokens[self.index - 2] != "," {
                return Ok(items.into_iter().next().expect("one item"));
            }
            return Ok(BoundaryType::Tuple(items));
        }
        if !token.starts_with(|c: char| c.is_alphabetic()) {
            return Err(format!("unsupported type {token:?}"));
        }
        Ok(BoundaryType::Nominal(self.identity(&token)?, Vec::new()))
    }

    fn typ(&mut self) -> Result<BoundaryType, String> {
        let head = self.atom()?;
        let BoundaryType::Nominal(identity, mut arguments) = head else { return Ok(head) };
        while !matches!(self.peek(), "" | "," | ")" | "}" | "->{") {
            arguments.push(self.atom()?);
        }
        Ok(BoundaryType::Nominal(identity, arguments))
    }

    fn parse(&mut self) -> Result<Interface, String> {
        self.take("(")?;
        let parameters = self.list(")")?;
        self.take("->{")?;
        let mut effects = Vec::new();
        if self.peek() != "}" {
            loop {
                let name = self.take("")?;
                let identity = self.identity(&name)?;
                if !effects.contains(&identity) {
                    effects.push(identity);
                }
                if self.peek() != "," {
                    break;
                }
                self.index += 1;
            }
        }
        self.take("}")?;
        let result = self.typ()?;
        if !self.peek().is_empty() {
            return Err("trailing signature tokens".into());
        }
        effects.sort();
        Ok(Interface { parameters, result, effects })
    }
}
