//! Native Rust values and the prepared program's Jacquard data.
//!
//! [`Encode`] and [`Decode`] are implemented for `bool`, the integer types,
//! `f64`, text, [`Hash`], [`Value`], [`Dynamic`], `()`, tuples, `Vec` (a
//! `List`), and `Option`. Application types implement them with the
//! [`record!`](crate::record) and [`variants!`](crate::variants) macros, or
//! by hand with an [`Encoder`] and a [`Decoder`].

use std::collections::BTreeMap;
use std::fmt;

use crate::json::{INT_MAX, INT_MIN};
use crate::protocol::{BoundaryType, Hash, Limits, Value};

use super::identities::{Group, display_to_store};
use super::{Error, InvalidArgument, MappingError};

const BUILTINS: [&str; 6] = ["true", "false", "some", "none", "cons", "nil"];

/// A native value with no application type: Jacquard data by public name.
#[derive(Clone, Debug, PartialEq)]
pub enum Dynamic {
    Int(i64),
    Real(f64),
    Text(String),
    Bool(bool),
    Hash(Hash),
    /// A positional product; the empty tuple is unit.
    Tuple(Vec<Dynamic>),
    List(Vec<Dynamic>),
    Option(Option<Box<Dynamic>>),
    /// A constructor by public name (store spelling such as `stock`, or
    /// display spelling such as `Stock`) and its positional fields.
    Variant(String, Vec<Dynamic>),
}

impl Dynamic {
    /// A constructor value by public name.
    pub fn variant(name: impl Into<String>, fields: impl IntoIterator<Item = Dynamic>) -> Dynamic {
        Dynamic::Variant(name.into(), fields.into_iter().collect())
    }

    /// Unit, the empty tuple.
    pub fn unit() -> Dynamic {
        Dynamic::Tuple(Vec::new())
    }
}

impl From<i64> for Dynamic {
    fn from(value: i64) -> Self {
        Dynamic::Int(value)
    }
}

impl From<f64> for Dynamic {
    fn from(value: f64) -> Self {
        Dynamic::Real(value)
    }
}

impl From<bool> for Dynamic {
    fn from(value: bool) -> Self {
        Dynamic::Bool(value)
    }
}

impl From<&str> for Dynamic {
    fn from(value: &str) -> Self {
        Dynamic::Text(value.to_owned())
    }
}

impl From<String> for Dynamic {
    fn from(value: String) -> Self {
        Dynamic::Text(value)
    }
}

/// Converts a native value to a boundary value.
pub trait Encode {
    /// Encodes `self`; `cx` names the value's location for error messages.
    fn encode(&self, cx: &Encoder<'_>) -> Result<Value, InvalidArgument>;
}

/// Converts a boundary value to a native value.
pub trait Decode: Sized {
    /// Decodes `value`.
    fn decode(cx: &Decoder<'_>, value: &Value) -> Result<Self, MappingError>;
}

/// The prepared program's public names and identities, used to map native
/// values. It is immutable once the program is prepared.
#[derive(Debug)]
pub struct Data {
    names: BTreeMap<String, Hash>,
    constructors: BTreeMap<String, Hash>,
    labels: BTreeMap<Hash, String>,
    primitives: BTreeMap<Hash, &'static str>,
    /// Each declaration group's head identity and its member identities.
    members: BTreeMap<Hash, Vec<Hash>>,
    limits: Limits,
}

impl Data {
    pub(crate) fn new(names: BTreeMap<String, Hash>, constructors: BTreeMap<String, Hash>, groups: &[Group], limits: Limits) -> Data {
        let labels = constructors.iter().map(|(name, id)| (id.clone(), name.clone())).collect();
        let primitives = ["int", "real", "text", "bool", "hash", "list", "option"]
            .into_iter()
            .filter_map(|name| names.get(name).map(|id| (id.clone(), name)))
            .collect();
        let members = groups
            .iter()
            .filter_map(|group| group.split_first())
            .map(|(head, rest)| (head.hash.clone(), rest.iter().map(|entry| entry.hash.clone()).collect()))
            .collect();
        Data { names, constructors, labels, primitives, members, limits }
    }

    /// The session limits that bound mapped values.
    pub fn limits(&self) -> &Limits {
        &self.limits
    }

    /// The exact identity of a public type, constructor, effect, or term name
    /// in the prepared program and its discovered prelude.
    pub fn identity(&self, name: &str) -> Result<Hash, Error> {
        self.names.get(&display_to_store(name)).cloned().ok_or_else(|| Error::binding(format!("unknown name {name}")))
    }

    /// The public name of a constructor or operation identity.
    pub fn label(&self, identity: &Hash) -> Option<&str> {
        self.labels.get(identity).map(String::as_str)
    }

    pub(crate) fn name(&self, name: &str) -> Option<&Hash> {
        self.names.get(name)
    }

    fn constructor_id(&self, name: &str) -> Option<&Hash> {
        self.constructors.get(&display_to_store(name))
    }

    /// A [`Dynamic::Variant`] after checking that the constructor exists.
    pub fn make(&self, name: &str, fields: impl IntoIterator<Item = Dynamic>) -> Result<Dynamic, Error> {
        if self.constructor_id(name).is_none() {
            return Err(Error::binding(format!("unknown constructor {name}")));
        }
        Ok(Dynamic::Variant(display_to_store(name), fields.into_iter().collect()))
    }

    /// Encodes a native value.
    pub fn encode<T: Encode + ?Sized>(&self, value: &T) -> Result<Value, Error> {
        Ok(value.encode(&Encoder::root(self, "value".into(), 3)?)?)
    }

    /// Decodes a boundary value into a native value.
    pub fn decode<T: Decode>(&self, value: &Value) -> Result<T, Error> {
        T::decode(&Decoder { data: self }, value).map_err(|error| Error::binding(error.message))
    }

    pub(crate) fn primitive(&self, expected: &BoundaryType) -> Option<&'static str> {
        match expected {
            BoundaryType::Nominal(identity, _) => self.primitives.get(identity).copied(),
            BoundaryType::Tuple(_) => None,
        }
    }

    fn is_nullary(&self, value: &Value, name: &str) -> bool {
        matches!(value, Value::Constructor(id, fields) if fields.is_empty() && self.label(id) == Some(name))
    }

    /// Walks a proper List, bounded by the collection limit.
    fn list_items<'v>(&self, value: &'v Value) -> Result<Vec<&'v Value>, MappingError> {
        let mut items = Vec::new();
        let mut cursor = value;
        loop {
            match cursor {
                Value::Constructor(id, fields) if fields.len() == 2 && self.label(id) == Some("cons") => {
                    if items.len() >= self.limits.max_collection_items {
                        return Err(MappingError::new("list exceeds the selected limit"));
                    }
                    items.push(&fields[0]);
                    cursor = &fields[1];
                }
                Value::Constructor(id, fields) if fields.is_empty() && self.label(id) == Some("nil") => return Ok(items),
                _ => return Err(MappingError::new("expected a proper List")),
            }
        }
    }

    /// Checks that an encoded value has the checked boundary type, as far as
    /// the prepared metadata describes it: primitives, Lists, Options, and
    /// tuples exactly, and other data by its constructor's declaration group.
    pub(crate) fn conform(&self, value: &Value, expected: &BoundaryType, path: &str) -> Result<(), InvalidArgument> {
        let refuse = |what: &str| Err(InvalidArgument::at(path, format!("expected {what}")));
        let child = |expected: &BoundaryType| match expected {
            BoundaryType::Nominal(_, arguments) => arguments.first().cloned(),
            BoundaryType::Tuple(_) => None,
        };
        match (self.primitive(expected), expected) {
            (_, BoundaryType::Tuple(items)) => match value {
                Value::Tuple(values) if values.len() == items.len() => {
                    for (index, (value, item)) in values.iter().zip(items).enumerate() {
                        self.conform(value, item, &format!("{path}[{index}]"))?;
                    }
                    Ok(())
                }
                _ => refuse(&format!("a {}-item tuple", items.len())),
            },
            (Some("int"), _) => {
                if matches!(value, Value::Int(_)) {
                    Ok(())
                } else {
                    refuse("an integer")
                }
            }
            (Some("real"), _) => {
                if matches!(value, Value::Real(_)) {
                    Ok(())
                } else {
                    refuse("a Real (f64)")
                }
            }
            (Some("text"), _) => {
                if matches!(value, Value::Text(_)) {
                    Ok(())
                } else {
                    refuse("text")
                }
            }
            (Some("hash"), _) => {
                if matches!(value, Value::Hash(_)) {
                    Ok(())
                } else {
                    refuse("a Hash")
                }
            }
            (Some("bool"), _) => {
                if self.is_nullary(value, "true") || self.is_nullary(value, "false") {
                    Ok(())
                } else {
                    refuse("a bool")
                }
            }
            (Some("list"), _) => {
                let items = self.list_items(value).map_err(|_| InvalidArgument::at(path, "expected a List (Vec)"))?;
                match child(expected) {
                    Some(item) => {
                        for (index, value) in items.into_iter().enumerate() {
                            self.conform(value, &item, &format!("{path}[{index}]"))?;
                        }
                        Ok(())
                    }
                    None => Ok(()),
                }
            }
            (Some("option"), _) => match value {
                _ if self.is_nullary(value, "none") => Ok(()),
                Value::Constructor(id, fields) if fields.len() == 1 && self.label(id) == Some("some") => match child(expected) {
                    Some(inner) => self.conform(&fields[0], &inner, path),
                    None => Ok(()),
                },
                _ => refuse("an Option"),
            },
            (_, BoundaryType::Nominal(identity, _)) => match (value, self.members.get(identity)) {
                (Value::Constructor(id, _), Some(members)) if members.contains(id) => Ok(()),
                (Value::Constructor(_, _), None) => Ok(()),
                _ => refuse(&format!("a {} value", self.label_of_type(identity))),
            },
        }
    }

    fn label_of_type(&self, identity: &Hash) -> String {
        self.names.iter().find(|(_, id)| *id == identity).map_or_else(|| identity.to_string(), |(name, _)| name.clone())
    }
}

/// Encodes one value at a location, bounded by the selected JSON depth.
pub struct Encoder<'a> {
    data: &'a Data,
    path: String,
    depth: usize,
}

impl<'a> Encoder<'a> {
    pub(crate) fn root(data: &'a Data, path: String, depth: usize) -> Result<Encoder<'a>, InvalidArgument> {
        let encoder = Encoder { data, path, depth };
        encoder.check_depth(depth)?;
        Ok(encoder)
    }

    fn check_depth(&self, depth: usize) -> Result<(), InvalidArgument> {
        if depth > self.data.limits.max_json_depth {
            return Err(self.invalid("value exceeds the v0 JSON depth; use smaller independent batches"));
        }
        Ok(())
    }

    /// The program's data.
    pub fn data(&self) -> &'a Data {
        self.data
    }

    /// The location being encoded, such as `triage.batch.argument[1][0].service`.
    pub fn path(&self) -> &str {
        &self.path
    }

    /// An error at this location.
    pub fn invalid(&self, message: impl fmt::Display) -> InvalidArgument {
        InvalidArgument::at(&self.path, message)
    }

    fn child(&self, segment: fmt::Arguments<'_>, extra: usize) -> Result<Encoder<'a>, InvalidArgument> {
        let child = Encoder { data: self.data, path: format!("{}{segment}", self.path), depth: self.depth + extra };
        child.check_depth(child.depth)?;
        Ok(child)
    }

    /// Encodes a named field of a record or variant.
    pub fn field<T: Encode + ?Sized>(&self, name: &str, value: &T) -> Result<Value, InvalidArgument> {
        value.encode(&self.child(format_args!(".{name}"), 2)?)
    }

    /// Encodes a positional item of a tuple.
    pub fn item<T: Encode + ?Sized>(&self, index: usize, value: &T) -> Result<Value, InvalidArgument> {
        value.encode(&self.child(format_args!("[{index}]"), 2)?)
    }

    /// A constructor value by public name, such as `"Job"` or `"job"`.
    /// Bool, List, and Option constructors are refused: use the native
    /// `bool`, `Vec`, and `Option` forms.
    pub fn constructor(&self, name: &str, fields: Vec<Value>) -> Result<Value, InvalidArgument> {
        if BUILTINS.contains(&display_to_store(name).as_str()) {
            return Err(self.invalid(format!("use the native form of builtin constructor {name}")));
        }
        self.builtin(name, fields)
    }

    fn builtin(&self, name: &str, fields: Vec<Value>) -> Result<Value, InvalidArgument> {
        match self.data.constructor_id(name) {
            Some(id) => Ok(Value::Constructor(id.clone(), fields)),
            None => Err(self.invalid(format!("unknown constructor {name}"))),
        }
    }

    fn list<T: Encode>(&self, items: &[T]) -> Result<Value, InvalidArgument> {
        if items.len() > self.data.limits.max_collection_items {
            return Err(self.invalid("list exceeds max_collection_items"));
        }
        // A linked list nests one constructor per element on the wire.
        self.check_depth(self.depth.saturating_add(2 * items.len()))?;
        let mut result = self.builtin("nil", Vec::new())?;
        for (index, item) in items.iter().enumerate().rev() {
            let value = item.encode(&self.child(format_args!("[{index}]"), 2 * (index + 1))?)?;
            result = self.builtin("cons", vec![value, result])?;
        }
        Ok(result)
    }

    fn option<T: Encode + ?Sized>(&self, value: Option<&T>) -> Result<Value, InvalidArgument> {
        match value {
            None => self.builtin("none", Vec::new()),
            Some(inner) => {
                let inner = inner.encode(&self.child(format_args!(""), 2)?)?;
                self.builtin("some", vec![inner])
            }
        }
    }

    fn text(&self, text: &str) -> Result<Value, InvalidArgument> {
        if text.len() > self.data.limits.max_text_bytes {
            return Err(self.invalid("text exceeds max_text_bytes"));
        }
        Ok(Value::Text(text.to_owned()))
    }

    fn int(&self, value: i128) -> Result<Value, InvalidArgument> {
        if value < i128::from(INT_MIN) || value > i128::from(INT_MAX) {
            return Err(self.invalid("integer is outside Core's 63-bit Int range"));
        }
        Ok(Value::Int(value as i64))
    }
}

/// Decodes boundary values with the program's data.
pub struct Decoder<'a> {
    data: &'a Data,
}

/// The positional fields of one constructor value, taken in order.
pub struct Fields<'a, 'v> {
    decoder: &'a Decoder<'a>,
    fields: std::slice::Iter<'v, Value>,
}

impl<'a> Decoder<'a> {
    /// The program's data.
    pub fn data(&self) -> &'a Data {
        self.data
    }

    /// Decodes a nested value.
    pub fn decode<T: Decode>(&self, value: &Value) -> Result<T, MappingError> {
        T::decode(self, value)
    }

    /// Whether `value` is built by the named constructor.
    pub fn is(&self, value: &Value, constructor: &str) -> bool {
        matches!((value, self.data.constructor_id(constructor)), (Value::Constructor(id, _), Some(expected)) if id == expected)
    }

    /// The fields of a value built by the named constructor with `count`
    /// fields.
    pub fn fields<'v>(&'a self, value: &'v Value, constructor: &str, count: usize) -> Result<Fields<'a, 'v>, MappingError> {
        match value {
            Value::Constructor(_, fields) if self.is(value, constructor) && fields.len() == count => {
                Ok(Fields { decoder: self, fields: fields.iter() })
            }
            _ => Err(MappingError::new(format!("{constructor} result has the wrong constructor or field count"))),
        }
    }

    /// An error for a value that is none of a type's constructors.
    pub fn mismatch(&self, type_name: &str) -> MappingError {
        MappingError::new(format!("unknown {type_name} case"))
    }
}

impl Fields<'_, '_> {
    /// Decodes the next field.
    pub fn take<T: Decode>(&mut self) -> Result<T, MappingError> {
        match self.fields.next() {
            Some(value) => T::decode(self.decoder, value),
            None => Err(MappingError::new("constructor has too few fields")),
        }
    }
}

impl<T: Encode + ?Sized> Encode for &T {
    fn encode(&self, cx: &Encoder<'_>) -> Result<Value, InvalidArgument> {
        (**self).encode(cx)
    }
}

impl<T: Encode + ?Sized> Encode for Box<T> {
    fn encode(&self, cx: &Encoder<'_>) -> Result<Value, InvalidArgument> {
        (**self).encode(cx)
    }
}

impl<T: Decode> Decode for Box<T> {
    fn decode(cx: &Decoder<'_>, value: &Value) -> Result<Self, MappingError> {
        T::decode(cx, value).map(Box::new)
    }
}

impl Encode for bool {
    fn encode(&self, cx: &Encoder<'_>) -> Result<Value, InvalidArgument> {
        cx.builtin(if *self { "true" } else { "false" }, Vec::new())
    }
}

impl Decode for bool {
    fn decode(cx: &Decoder<'_>, value: &Value) -> Result<Self, MappingError> {
        match value {
            _ if cx.data.is_nullary(value, "true") => Ok(true),
            _ if cx.data.is_nullary(value, "false") => Ok(false),
            _ => Err(MappingError::new("expected Bool")),
        }
    }
}

macro_rules! integers {
    ($($t:ty),*) => {$(
        impl Encode for $t {
            fn encode(&self, cx: &Encoder<'_>) -> Result<Value, InvalidArgument> {
                cx.int(*self as i128)
            }
        }

        impl Decode for $t {
            fn decode(_: &Decoder<'_>, value: &Value) -> Result<Self, MappingError> {
                match value {
                    Value::Int(n) => <$t>::try_from(*n).map_err(|_| MappingError::new(concat!("expected Int that fits ", stringify!($t)))),
                    _ => Err(MappingError::new(concat!("expected Int that fits ", stringify!($t)))),
                }
            }
        }
    )*};
}

integers!(i8, i16, i32, i64, isize, u8, u16, u32, u64, usize);

impl Encode for f64 {
    fn encode(&self, _: &Encoder<'_>) -> Result<Value, InvalidArgument> {
        Ok(Value::real(*self))
    }
}

impl Encode for f32 {
    fn encode(&self, _: &Encoder<'_>) -> Result<Value, InvalidArgument> {
        Ok(Value::real(f64::from(*self)))
    }
}

impl Decode for f64 {
    fn decode(_: &Decoder<'_>, value: &Value) -> Result<Self, MappingError> {
        match value {
            Value::Real(bits) => Ok(f64::from_bits(*bits)),
            _ => Err(MappingError::new("expected Real")),
        }
    }
}

impl Encode for str {
    fn encode(&self, cx: &Encoder<'_>) -> Result<Value, InvalidArgument> {
        cx.text(self)
    }
}

impl Encode for String {
    fn encode(&self, cx: &Encoder<'_>) -> Result<Value, InvalidArgument> {
        cx.text(self)
    }
}

impl Decode for String {
    fn decode(_: &Decoder<'_>, value: &Value) -> Result<Self, MappingError> {
        match value {
            Value::Text(text) => Ok(text.clone()),
            _ => Err(MappingError::new("expected Text")),
        }
    }
}

impl Encode for Hash {
    fn encode(&self, _: &Encoder<'_>) -> Result<Value, InvalidArgument> {
        Ok(Value::Hash(self.clone()))
    }
}

impl Decode for Hash {
    fn decode(_: &Decoder<'_>, value: &Value) -> Result<Self, MappingError> {
        match value {
            Value::Hash(hash) => Ok(hash.clone()),
            _ => Err(MappingError::new("expected Hash")),
        }
    }
}

/// A raw boundary value passes through unchanged.
impl Encode for Value {
    fn encode(&self, _: &Encoder<'_>) -> Result<Value, InvalidArgument> {
        Ok(self.clone())
    }
}

impl Decode for Value {
    fn decode(_: &Decoder<'_>, value: &Value) -> Result<Self, MappingError> {
        Ok(value.clone())
    }
}

impl<T: Encode> Encode for [T] {
    fn encode(&self, cx: &Encoder<'_>) -> Result<Value, InvalidArgument> {
        cx.list(self)
    }
}

impl<T: Encode> Encode for Vec<T> {
    fn encode(&self, cx: &Encoder<'_>) -> Result<Value, InvalidArgument> {
        cx.list(self)
    }
}

impl<T: Decode> Decode for Vec<T> {
    fn decode(cx: &Decoder<'_>, value: &Value) -> Result<Self, MappingError> {
        cx.data.list_items(value)?.into_iter().map(|item| T::decode(cx, item)).collect()
    }
}

impl<T: Encode> Encode for Option<T> {
    fn encode(&self, cx: &Encoder<'_>) -> Result<Value, InvalidArgument> {
        cx.option(self.as_ref())
    }
}

impl<T: Decode> Decode for Option<T> {
    fn decode(cx: &Decoder<'_>, value: &Value) -> Result<Self, MappingError> {
        match value {
            _ if cx.data.is_nullary(value, "none") => Ok(None),
            Value::Constructor(id, fields) if fields.len() == 1 && cx.data.label(id) == Some("some") => T::decode(cx, &fields[0]).map(Some),
            _ => Err(MappingError::new("expected Option")),
        }
    }
}

macro_rules! tuples {
    ($(($($name:ident $index:tt),*)),*) => {$(
        impl<$($name: Encode),*> Encode for ($($name,)*) {
            #[allow(unused_variables)]
            fn encode(&self, cx: &Encoder<'_>) -> Result<Value, InvalidArgument> {
                Ok(Value::Tuple(vec![$(cx.item($index, &self.$index)?),*]))
            }
        }

        impl<$($name: Decode),*> Decode for ($($name,)*) {
            #[allow(unused_variables, unused_mut)]
            fn decode(cx: &Decoder<'_>, value: &Value) -> Result<Self, MappingError> {
                const COUNT: usize = <[&str]>::len(&[$(stringify!($name)),*]);
                match value {
                    Value::Tuple(items) if items.len() == COUNT => {
                        let mut items = items.iter();
                        Ok(($($name::decode(cx, items.next().expect("counted"))?,)*))
                    }
                    _ => Err(MappingError::new("tuple shape mismatch")),
                }
            }
        }
    )*};
}

tuples!((), (A 0), (A 0, B 1), (A 0, B 1, C 2), (A 0, B 1, C 2, D 3), (A 0, B 1, C 2, D 3, E 4), (A 0, B 1, C 2, D 3, E 4, F 5));

impl Encode for Dynamic {
    fn encode(&self, cx: &Encoder<'_>) -> Result<Value, InvalidArgument> {
        match self {
            Dynamic::Int(n) => n.encode(cx),
            Dynamic::Real(r) => r.encode(cx),
            Dynamic::Text(text) => text.encode(cx),
            Dynamic::Bool(b) => b.encode(cx),
            Dynamic::Hash(hash) => hash.encode(cx),
            Dynamic::Tuple(items) => {
                Ok(Value::Tuple(items.iter().enumerate().map(|(index, item)| cx.item(index, item)).collect::<Result<_, _>>()?))
            }
            Dynamic::List(items) => cx.list(items),
            Dynamic::Option(inner) => cx.option(inner.as_deref()),
            Dynamic::Variant(name, fields) => {
                let fields = fields.iter().enumerate().map(|(index, field)| field.encode(&cx.child(format_args!(".{index}"), 2)?));
                let fields = fields.collect::<Result<_, _>>()?;
                cx.constructor(name, fields)
            }
        }
    }
}

impl Decode for Dynamic {
    fn decode(cx: &Decoder<'_>, value: &Value) -> Result<Self, MappingError> {
        Ok(match value {
            Value::Int(n) => Dynamic::Int(*n),
            Value::Real(bits) => Dynamic::Real(f64::from_bits(*bits)),
            Value::Text(text) => Dynamic::Text(text.clone()),
            Value::Hash(hash) => Dynamic::Hash(hash.clone()),
            Value::Tuple(items) => Dynamic::Tuple(items.iter().map(|item| Dynamic::decode(cx, item)).collect::<Result<_, _>>()?),
            Value::Constructor(id, fields) => {
                let Some(label) = cx.data.label(id) else {
                    return Err(MappingError::new(format!("unmapped constructor {id}")));
                };
                match label {
                    "true" | "false" => Dynamic::Bool(bool::decode(cx, value)?),
                    "cons" | "nil" => Dynamic::List(Vec::decode(cx, value)?),
                    "some" | "none" => Dynamic::Option(Option::<Dynamic>::decode(cx, value)?.map(Box::new)),
                    _ => {
                        Dynamic::Variant(label.to_owned(), fields.iter().map(|field| Dynamic::decode(cx, field)).collect::<Result<_, _>>()?)
                    }
                }
            }
        })
    }
}

/// Implements [`Encode`] and [`Decode`] for a struct as one Jacquard
/// constructor. The listed fields, in order, are the constructor's
/// positional fields.
///
/// ```
/// struct Job {
///     id: String,
///     effort: i64,
/// }
///
/// jacquard_host::record!(Job = "Job" { id, effort });
/// ```
#[macro_export]
macro_rules! record {
    ($ty:ty = $constructor:literal { $($field:ident),* $(,)? }) => {
        impl $crate::sdk::Encode for $ty {
            fn encode(&self, cx: &$crate::sdk::Encoder<'_>) -> ::std::result::Result<$crate::protocol::Value, $crate::sdk::InvalidArgument> {
                cx.constructor($constructor, ::std::vec![$(cx.field(::std::stringify!($field), &self.$field)?),*])
            }
        }

        impl $crate::sdk::Decode for $ty {
            #[allow(unused_mut)]
            fn decode(cx: &$crate::sdk::Decoder<'_>, value: &$crate::protocol::Value) -> ::std::result::Result<Self, $crate::sdk::MappingError> {
                let count = <[&str]>::len(&[$(::std::stringify!($field)),*]);
                let mut fields = cx.fields(value, $constructor, count)?;
                ::std::result::Result::Ok(Self { $($field: fields.take()?),* })
            }
        }
    };
}

/// Implements [`Encode`] and [`Decode`] for an enum whose variants are the
/// constructors of one Jacquard data type. Unit variants are nullary
/// constructors; tuple and struct variants list their fields in order.
///
/// ```
/// struct Plan {
///     score: i64,
/// }
/// jacquard_host::record!(Plan = "Plan" { score });
///
/// enum Planning {
///     Planned { plan: Plan },
///     InvalidPlan(String),
/// }
///
/// jacquard_host::variants!(Planning {
///     Planned { plan } = "Planned",
///     InvalidPlan(reason) = "InvalidPlan",
/// });
///
/// enum Urgency {
///     Page,
///     Today,
/// }
///
/// jacquard_host::variants!(Urgency { Page = "PageNow", Today = "WorkToday" });
/// ```
#[macro_export]
macro_rules! variants {
    ($ty:ty { $($variant:ident $(( $($positional:ident),* $(,)? ))? $({ $($named:ident),* $(,)? })? = $constructor:literal),* $(,)? }) => {
        impl $crate::sdk::Encode for $ty {
            fn encode(&self, cx: &$crate::sdk::Encoder<'_>) -> ::std::result::Result<$crate::protocol::Value, $crate::sdk::InvalidArgument> {
                match self {
                    $(Self::$variant $(( $($positional),* ))? $({ $($named),* })? => cx.constructor(
                        $constructor,
                        ::std::vec![
                            $($(cx.field(::std::stringify!($positional), $positional)?),*)?
                            $($(cx.field(::std::stringify!($named), $named)?),*)?
                        ],
                    ),)*
                }
            }
        }

        impl $crate::sdk::Decode for $ty {
            #[allow(unused_mut, unused_variables)]
            fn decode(cx: &$crate::sdk::Decoder<'_>, value: &$crate::protocol::Value) -> ::std::result::Result<Self, $crate::sdk::MappingError> {
                $(if cx.is(value, $constructor) {
                    let count = <[&str]>::len(&[$($(::std::stringify!($positional)),*)? $($(::std::stringify!($named)),*)?]);
                    let mut fields = cx.fields(value, $constructor, count)?;
                    return ::std::result::Result::Ok(
                        Self::$variant $(( $({ let _ = ::std::stringify!($positional); fields.take()? }),* ))? $({ $($named: fields.take()?),* })?
                    );
                })*
                ::std::result::Result::Err(cx.mismatch(::std::stringify!($ty)))
            }
        }
    };
}
