//! Runs the repository's example Jacquard projects through the Rust SDK:
//! quickstart, planner, triage, and fulfillment. The same rules run through
//! the Python, TypeScript, and Go SDKs.
//!
//! ```text
//! cargo run --example jacquard-examples -- [--workdir DIR] [--inspect PATH] [--fault NAME] [--deliver-pending] PROJECT [INPUT.json]
//! ```
//!
//! Core is found through `JACQUARD_BIN` and `JACQUARD_PRELUDE`. The exit
//! status is 1 for a Jacquard or SDK failure and 2 for usage or other errors.

use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, MutexGuard};

use jacquard_host::host::HandlerError;
use jacquard_host::json::{Json, Object, decode, stringify};
use jacquard_host::sdk::{BatchError, Error, Function, Invocation, Operations, PrepareOptions, Program, Runtime, RuntimeOptions};
use jacquard_host::{record, variants};

/// The example projects in presentation order.
pub const PROJECTS: [&str; 4] = ["quickstart", "planner", "triage", "fulfillment"];

/// The repository's example projects.
fn examples() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../examples")
}

/// A failure, classified for the exit status.
#[derive(Debug)]
pub enum Failure {
    Sdk(Error),
    Other(String),
}

impl std::fmt::Display for Failure {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Failure::Sdk(error) => error.fmt(f),
            Failure::Other(message) => f.write_str(message),
        }
    }
}

impl From<Error> for Failure {
    fn from(error: Error) -> Self {
        Failure::Sdk(error)
    }
}

impl<R> From<BatchError<R>> for Failure {
    fn from(error: BatchError<R>) -> Self {
        Failure::Sdk(error.error)
    }
}

impl From<std::io::Error> for Failure {
    fn from(error: std::io::Error) -> Self {
        Failure::Other(error.to_string())
    }
}

fn other(message: impl Into<String>) -> Failure {
    Failure::Other(message.into())
}

// JSON input and output.

fn field<'a>(object: &'a Object, key: &str) -> Result<&'a Json, Failure> {
    object.get(key).ok_or_else(|| other(format!("input: missing {key}")))
}

fn text(object: &Object, key: &str) -> Result<String, Failure> {
    field(object, key)?.as_str().map(str::to_owned).ok_or_else(|| other(format!("input: {key} must be text")))
}

fn int(object: &Object, key: &str) -> Result<i64, Failure> {
    field(object, key)?.as_int().ok_or_else(|| other(format!("input: {key} must be an integer")))
}

fn objects<'a>(object: &'a Object, key: &str) -> Result<Vec<&'a Object>, Failure> {
    let items = field(object, key)?.as_array().ok_or_else(|| other(format!("input: {key} must be a list")))?;
    items.iter().map(|item| item.as_object().ok_or_else(|| other(format!("input: {key} must hold objects")))).collect()
}

fn tagged(tag: &str, fields: impl IntoIterator<Item = (&'static str, Json)>) -> Json {
    let mut object: Object = fields.into_iter().map(|(key, value)| (key.to_owned(), value)).collect();
    object.insert("$tag".into(), tag.into());
    Json::Object(object)
}

fn pretty(value: &Json, indent: usize, out: &mut String) {
    let pad = |depth: usize| "  ".repeat(depth);
    match value {
        Json::Array(items) if !items.is_empty() => {
            out.push_str("[\n");
            for (index, item) in items.iter().enumerate() {
                out.push_str(&pad(indent + 1));
                pretty(item, indent + 1, out);
                out.push_str(if index + 1 < items.len() { ",\n" } else { "\n" });
            }
            out.push_str(&pad(indent));
            out.push(']');
        }
        Json::Object(object) if !object.is_empty() => {
            out.push_str("{\n");
            for (index, (key, item)) in object.iter().enumerate() {
                out.push_str(&pad(indent + 1));
                out.push_str(&stringify(&Json::Str(key.clone()), 2).expect("a key"));
                out.push_str(": ");
                pretty(item, indent + 1, out);
                out.push_str(if index + 1 < object.len() { ",\n" } else { "\n" });
            }
            out.push_str(&pad(indent));
            out.push('}');
        }
        _ => out.push_str(&stringify(value, 2).expect("a scalar")),
    }
}

fn print_json(out: &mut impl Write, value: &Json) -> Result<(), Failure> {
    let mut text = String::new();
    pretty(value, 0, &mut text);
    writeln!(out, "{text}")?;
    Ok(())
}

// Planner.

pub struct Job {
    pub id: String,
    pub effort: i64,
    pub value: i64,
    pub latest: i64,
}
record!(Job = "Job" { id, effort, value, latest });

pub struct Assignment {
    pub id: String,
    pub slot: i64,
}
record!(Assignment = "Assignment" { id, slot });

pub struct Plan {
    pub score: i64,
    pub assignments: Vec<Assignment>,
}
record!(Plan = "Plan" { score, assignments });

pub enum Planning {
    Planned { plan: Plan },
    InvalidPlan { reason: String },
}
variants!(Planning { Planned { plan } = "Planned", InvalidPlan { reason } = "InvalidPlan" });

impl Planning {
    fn to_json(&self) -> Json {
        match self {
            Planning::Planned { plan } => {
                let assignments = plan
                    .assignments
                    .iter()
                    .map(|a| Json::Object(Object::from([("id".into(), a.id.as_str().into()), ("slot".into(), a.slot.into())])))
                    .collect();
                let plan = Object::from([("assignments".into(), Json::Array(assignments)), ("score".into(), plan.score.into())]);
                tagged("Planned", [("plan", Json::Object(plan))])
            }
            Planning::InvalidPlan { reason } => tagged("InvalidPlan", [("reason", reason.as_str().into())]),
        }
    }
}

// Triage.

pub struct TriagePolicy {
    pub page: i64,
    pub stale: i64,
}
record!(TriagePolicy = "TriagePolicy" { page, stale });

pub struct Incident {
    pub id: String,
    pub service: String,
    pub severity: i64,
    pub age: i64,
    pub visible: bool,
    pub owner: Option<String>,
}
record!(Incident = "Incident" { id, service, severity, age, visible, owner });

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Urgency {
    Page,
    Today,
    Watch,
}
variants!(Urgency { Page = "PageNow", Today = "WorkToday", Watch = "WatchOnly" });

pub enum Triage {
    Routed { id: String, team: String, urgency: Urgency, reasons: Vec<String> },
    InvalidIncident { id: String },
}
variants!(Triage { Routed { id, team, urgency, reasons } = "Routed", InvalidIncident { id } = "InvalidIncident" });

impl Triage {
    fn to_json(&self) -> Json {
        match self {
            Triage::Routed { id, team, urgency, reasons } => {
                let urgency = match urgency {
                    Urgency::Page => "PAGE",
                    Urgency::Today => "TODAY",
                    Urgency::Watch => "WATCH",
                };
                tagged(
                    "Routed",
                    [
                        ("id", id.as_str().into()),
                        ("team", team.as_str().into()),
                        ("urgency", urgency.into()),
                        ("reasons", Json::Array(reasons.iter().map(|reason| reason.as_str().into()).collect())),
                    ],
                )
            }
            Triage::InvalidIncident { id } => tagged("InvalidIncident", [("id", id.as_str().into())]),
        }
    }
}

// Fulfillment.

#[derive(Clone)]
pub struct Line {
    pub sku: String,
    pub units: i64,
}
record!(Line = "Line" { sku, units });

#[derive(Clone)]
pub struct Order {
    pub id: String,
    pub lines: Vec<Line>,
}
record!(Order = "Order" { id, lines });

pub enum Stock {
    Stock(i64),
    UnknownSku,
}
variants!(Stock { Stock(available) = "Stock", UnknownSku = "UnknownSku" });

pub enum Reservation {
    BookingPending,
    Booked(String),
    RepeatBooking(String),
    BookingConflict,
    StockChanged,
}
variants!(Reservation {
    BookingPending = "BookingPending",
    Booked(receipt) = "Booked",
    RepeatBooking(receipt) = "RepeatBooking",
    BookingConflict = "BookingConflict",
    StockChanged = "StockChanged",
});

pub enum Fulfillment {
    Fulfilled { receipt: String },
    DuplicateOrder { receipt: String },
    OrderRejected { reason: String },
}
variants!(Fulfillment {
    Fulfilled { receipt } = "Fulfilled",
    DuplicateOrder { receipt } = "DuplicateOrder",
    OrderRejected { reason } = "OrderRejected",
});

impl Fulfillment {
    fn to_json(&self) -> Json {
        match self {
            Fulfillment::Fulfilled { receipt } => tagged("Fulfilled", [("receipt", receipt.as_str().into())]),
            Fulfillment::DuplicateOrder { receipt } => tagged("DuplicateOrder", [("receipt", receipt.as_str().into())]),
            Fulfillment::OrderRejected { reason } => tagged("OrderRejected", [("reason", reason.as_str().into())]),
        }
    }
}

const INVENTORY_SCHEMA: &str = "jacquard-example-inventory-v1";

#[derive(Clone)]
struct Booking {
    id: String,
    payload: String,
    receipt: String,
}

#[derive(Clone)]
struct Outbox {
    id: String,
    receipt: String,
    delivered: bool,
}

#[derive(Clone)]
struct Inventory {
    stock: Vec<(String, i64)>,
    bookings: Vec<Booking>,
    outbox: Vec<Outbox>,
    deliveries: Vec<String>,
}

impl Inventory {
    fn to_json(&self) -> Json {
        let object = |pairs: Vec<(&str, Json)>| Json::Object(pairs.into_iter().map(|(key, value)| (key.to_owned(), value)).collect());
        object(vec![
            ("schema", INVENTORY_SCHEMA.into()),
            (
                "stock",
                Json::Array(
                    self.stock
                        .iter()
                        .map(|(sku, available)| object(vec![("sku", sku.as_str().into()), ("available", (*available).into())]))
                        .collect(),
                ),
            ),
            (
                "bookings",
                Json::Array(
                    self.bookings
                        .iter()
                        .map(|b| {
                            object(vec![
                                ("id", b.id.as_str().into()),
                                ("payload", b.payload.as_str().into()),
                                ("receipt", b.receipt.as_str().into()),
                            ])
                        })
                        .collect(),
                ),
            ),
            (
                "outbox",
                Json::Array(
                    self.outbox
                        .iter()
                        .map(|o| {
                            object(vec![
                                ("id", o.id.as_str().into()),
                                ("receipt", o.receipt.as_str().into()),
                                ("delivered", Json::Bool(o.delivered)),
                            ])
                        })
                        .collect(),
                ),
            ),
            ("deliveries", Json::Array(self.deliveries.iter().map(|d| d.as_str().into()).collect())),
        ])
    }

    fn from_json(value: &Json) -> Option<Inventory> {
        let object = value.as_object()?;
        if object.get("schema")?.as_str()? != INVENTORY_SCHEMA {
            return None;
        }
        let list = |key: &str| object.get(key)?.as_array();
        let get = |item: &Json, key: &str| -> Option<String> { Some(item.as_object()?.get(key)?.as_str()?.to_owned()) };
        Some(Inventory {
            stock: list("stock")?
                .iter()
                .map(|item| Some((get(item, "sku")?, item.as_object()?.get("available")?.as_int()?)))
                .collect::<Option<_>>()?,
            bookings: list("bookings")?
                .iter()
                .map(|item| Some(Booking { id: get(item, "id")?, payload: get(item, "payload")?, receipt: get(item, "receipt")? }))
                .collect::<Option<_>>()?,
            outbox: list("outbox")?
                .iter()
                .map(|item| {
                    let delivered = matches!(item.as_object()?.get("delivered")?, Json::Bool(true));
                    Some(Outbox { id: get(item, "id")?, receipt: get(item, "receipt")?, delivered })
                })
                .collect::<Option<_>>()?,
            deliveries: list("deliveries")?.iter().map(|item| item.as_str().map(str::to_owned)).collect::<Option<_>>()?,
        })
    }
}

/// A single-process synthetic Inventory and Dispatch service. Bookings and
/// outbox entries are committed together as one atomic JSON snapshot;
/// deliveries are deduplicated by receipt. It has no cross-process locking
/// or power-loss durability claim.
pub struct Warehouse {
    /// Injects `publish-failed`, `after-commit`, or `after-delivery`.
    pub fault: String,
    path: PathBuf,
    state: Inventory,
}

/// The canonical payload of an order: its lines sorted by SKU.
fn payload(order: &Order) -> String {
    let mut lines = order.lines.clone();
    lines.sort_by(|a, b| a.sku.cmp(&b.sku));
    let pairs = lines.iter().map(|line| Json::Array(vec![line.sku.as_str().into(), line.units.into()])).collect();
    stringify(&Json::Array(pairs), 4).expect("a flat payload")
}

impl Warehouse {
    /// Loads the snapshot at `path` or starts synthetic stock.
    pub fn open(path: PathBuf) -> Result<Warehouse, Failure> {
        let state = match std::fs::read(&path) {
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => Inventory {
                stock: vec![("sensor".into(), 20), ("cable".into(), 30), ("battery".into(), 15)],
                bookings: Vec::new(),
                outbox: Vec::new(),
                deliveries: Vec::new(),
            },
            Err(error) => return Err(error.into()),
            Ok(raw) => {
                decode(&raw, 16).ok().as_ref().and_then(Inventory::from_json).ok_or_else(|| other("inventory snapshot version mismatch"))?
            }
        };
        Ok(Warehouse { fault: String::new(), path, state })
    }

    fn commit(&mut self, next: Inventory) -> std::io::Result<()> {
        if let Some(parent) = self.path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let temporary = self.path.with_extension(format!("{}.tmp", std::process::id()));
        let text = stringify(&next.to_json(), 8).map_err(std::io::Error::other)?;
        std::fs::write(&temporary, format!("{text}\n"))?;
        let renamed = std::fs::rename(&temporary, &self.path);
        let _ = std::fs::remove_file(&temporary);
        renamed?;
        self.state = next;
        Ok(())
    }

    fn find(&self, id: &str) -> Option<&Booking> {
        self.state.bookings.iter().find(|booking| booking.id == id)
    }

    /// Serves `Inventory.booking-status`.
    pub fn booking_status(&self, order: &Order) -> Reservation {
        match self.find(&order.id) {
            None => Reservation::BookingPending,
            Some(previous) if previous.payload == payload(order) => Reservation::RepeatBooking(previous.receipt.clone()),
            Some(_) => Reservation::BookingConflict,
        }
    }

    /// Serves `Inventory.lookup-stock`.
    pub fn lookup_stock(&self, sku: &str) -> Stock {
        self.state.stock.iter().find(|(name, _)| name == sku).map_or(Stock::UnknownSku, |(_, available)| Stock::Stock(*available))
    }

    /// Serves `Inventory.reserve-order`, committing booking and outbox together.
    pub fn reserve_order(&mut self, order: &Order) -> Result<Reservation, HandlerError> {
        let mut seen = std::collections::BTreeSet::new();
        let valid = !order.id.is_empty()
            && (1..=20).contains(&order.lines.len())
            && order.lines.iter().all(|line| seen.insert(line.sku.clone()) && (1..=20).contains(&line.units));
        if !valid {
            return Err(HandlerError::refused("invalid synthetic reservation"));
        }
        if self.find(&order.id).is_some() {
            return Ok(self.booking_status(order));
        }
        let mut next = self.state.clone();
        for line in &order.lines {
            match next.stock.iter_mut().find(|(sku, _)| *sku == line.sku) {
                Some((_, available)) if *available >= line.units => *available -= line.units,
                _ => return Ok(Reservation::StockChanged),
            }
        }
        let body = payload(order);
        let receipt = jacquard_host::sha256::hex_digest(format!("{}\0{body}", order.id).as_bytes());
        next.bookings.push(Booking { id: order.id.clone(), payload: body, receipt: receipt.clone() });
        next.outbox.push(Outbox { id: order.id.clone(), receipt: receipt.clone(), delivered: false });
        self.commit(next)?;
        if self.fault == "after-commit" {
            return Err(HandlerError::Unknown);
        }
        Ok(Reservation::Booked(receipt))
    }

    /// Serves `Dispatch.publish-booking`.
    pub fn publish_booking(&mut self, id: &str, receipt: &str) -> Result<(), HandlerError> {
        if self.fault == "publish-failed" {
            return Err(HandlerError::outside_failed("synthetic delivery unavailable; outbox retained"));
        }
        let mut next = self.state.clone();
        let Some(entry) = next.outbox.iter_mut().find(|entry| entry.receipt == receipt && entry.id == id) else {
            return Err(HandlerError::refused("no matching outbox entry"));
        };
        entry.delivered = true;
        if !next.deliveries.iter().any(|delivery| delivery == receipt) {
            next.deliveries.push(receipt.to_owned());
        }
        self.commit(next)?;
        if self.fault == "after-delivery" {
            return Err(HandlerError::Unknown);
        }
        Ok(())
    }

    /// Publishes every undelivered outbox entry explicitly, returning how
    /// many were delivered, and the error that stopped it.
    pub fn deliver_pending(&mut self) -> (usize, Result<(), HandlerError>) {
        let pending: Vec<Outbox> = self.state.outbox.iter().filter(|entry| !entry.delivered).cloned().collect();
        for (delivered, entry) in pending.iter().enumerate() {
            if let Err(error) = self.publish_booking(&entry.id, &entry.receipt) {
                return (delivered, Err(error));
            }
        }
        (pending.len(), Ok(()))
    }

    /// Counts bookings, pending deliveries, and deliveries.
    pub fn summary(&self) -> Json {
        let pending = self.state.outbox.iter().filter(|entry| !entry.delivered).count();
        Json::Object(Object::from([
            ("bookings".into(), Json::Int(self.state.bookings.len() as i64)),
            ("deliveries".into(), Json::Int(self.state.deliveries.len() as i64)),
            ("pendingDeliveries".into(), Json::Int(pending as i64)),
        ]))
    }
}

fn locked(warehouse: &Mutex<Warehouse>) -> MutexGuard<'_, Warehouse> {
    warehouse.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// Binds `fulfillment.place` to a shared warehouse serving both effects.
pub fn fulfillment(program: &Program, warehouse: &Arc<Mutex<Warehouse>>) -> Result<Function, Error> {
    let (a, b, c, d) = (warehouse.clone(), warehouse.clone(), warehouse.clone(), warehouse.clone());
    let service = Operations::new()
        .on("booking-status", move |order: Order| Ok(locked(&a).booking_status(&order)))
        .on("lookup-stock", move |sku: String| Ok(locked(&b).lookup_stock(&sku)))
        .on("reserve-order", move |order: Order| locked(&c).reserve_order(&order))
        .on("publish-booking", move |id: String, receipt: String| locked(&d).publish_booking(&id, &receipt));
    program.function_with("fulfillment.place", [("Inventory", service.clone()), ("Dispatch", service)])
}

/// Runs one command line, writing its output to `out`.
pub fn run(arguments: &[String], out: &mut impl Write) -> Result<(), Failure> {
    let mut workdir = PathBuf::from(".scratch/rust-examples");
    let (mut inspect, mut fault, mut deliver_pending) = (None, String::new(), false);
    let mut positional = Vec::new();
    let mut arguments = arguments.iter();
    while let Some(argument) = arguments.next() {
        let mut value = || arguments.next().cloned().ok_or_else(|| other(format!("{argument} needs a value")));
        match argument.as_str() {
            "--workdir" => workdir = PathBuf::from(value()?),
            "--inspect" => inspect = Some(value()?),
            "--fault" => fault = value()?,
            "--deliver-pending" => deliver_pending = true,
            flag if flag.starts_with("--") => return Err(other(format!("unknown flag {flag}"))),
            _ => positional.push(argument.clone()),
        }
    }
    let project = positional.first().map(String::as_str).unwrap_or_default();
    if !PROJECTS.contains(&project) || positional.len() > 2 {
        return Err(other("choose quickstart, triage, planner, or fulfillment"));
    }
    if !["", "publish-failed", "after-commit", "after-delivery"].contains(&fault.as_str()) {
        return Err(other("unknown fault"));
    }
    let root = std::path::absolute(&workdir)?;
    let rules = examples().join(project).join("rules.jac");
    let runtime = Runtime::new(RuntimeOptions::default())?;
    let program = runtime.prepare(&[&rules], &PrepareOptions { cache_dir: Some(root.join("programs")) })?;
    if project == "quickstart" {
        let shipping = program.function("shipping-cost")?;
        for total in [35, 75] {
            let cost: i64 = shipping.call((total,))?;
            writeln!(out, "Order total {total}: shipping {cost}")?;
        }
        return Ok(());
    }
    let input = positional.get(1).map_or_else(|| examples().join(project).join("input.json"), PathBuf::from);
    let raw = std::fs::read(&input)?;
    let input = decode(&raw, 16).map_err(|error| other(format!("input: {error}")))?;
    let input = input.as_object().ok_or_else(|| other("input must be a JSON object"))?;
    let save = |invocation: &Invocation, path: Option<String>| -> Result<(), Failure> {
        if let Some(path) = path {
            invocation.save(path)?;
        }
        Ok(())
    };
    match project {
        "planner" => {
            let jobs = objects(input, "jobs")?
                .into_iter()
                .map(|job| {
                    Ok(Job { id: text(job, "id")?, effort: int(job, "effort")?, value: int(job, "value")?, latest: int(job, "latest")? })
                })
                .collect::<Result<Vec<_>, Failure>>()?;
            let capacities = field(input, "capacities")?.as_array().ok_or_else(|| other("input: capacities must be a list"))?;
            let capacities = capacities
                .iter()
                .map(|cap| cap.as_int().ok_or_else(|| other("input: capacities must be integers")))
                .collect::<Result<Vec<_>, _>>()?;
            let invocation = program.function("planner.plan")?.invoke((&jobs, &capacities))?;
            save(&invocation, inspect)?;
            print_json(out, &invocation.decode::<Planning>()?.to_json())?;
            writeln!(out, "{}", invocation.render())?;
        }
        "triage" => {
            let policy = field(input, "policy")?.as_object().ok_or_else(|| other("input: policy must be an object"))?;
            let policy = TriagePolicy { page: int(policy, "page")?, stale: int(policy, "stale")? };
            let incidents = objects(input, "incidents")?
                .into_iter()
                .map(|incident| {
                    let owner = match field(incident, "owner")? {
                        Json::Null => None,
                        owner => Some(owner.as_str().ok_or_else(|| other("input: owner must be text or null"))?.to_owned()),
                    };
                    Ok(Incident {
                        id: text(incident, "id")?,
                        service: text(incident, "service")?,
                        severity: int(incident, "severity")?,
                        age: int(incident, "age")?,
                        visible: matches!(field(incident, "visible")?, Json::Bool(true)),
                        owner,
                    })
                })
                .collect::<Result<Vec<_>, Failure>>()?;
            let route = program.function("triage.batch")?;
            let results: Vec<Triage> = match inspect {
                None => route.map_batches((&policy,), &incidents, 8)?,
                Some(inspect) => {
                    let mut results = Vec::new();
                    for (index, chunk) in incidents.chunks(8).enumerate() {
                        let invocation = route.invoke((&policy, chunk))?;
                        save(&invocation, Some(format!("{inspect}.{index}.json")))?;
                        results.extend(invocation.decode::<Vec<Triage>>()?);
                    }
                    results
                }
            };
            print_json(out, &Json::Array(results.iter().map(Triage::to_json).collect()))?;
        }
        _ => {
            let lines = objects(input, "lines")?
                .into_iter()
                .map(|line| Ok(Line { sku: text(line, "sku")?, units: int(line, "units")? }))
                .collect::<Result<Vec<_>, Failure>>()?;
            let order = Order { id: text(input, "id")?, lines };
            let mut warehouse = Warehouse::open(root.join("inventory.json"))?;
            warehouse.fault = fault;
            let warehouse = Arc::new(Mutex::new(warehouse));
            let outcome = (|| {
                if deliver_pending {
                    let (delivered, result) = locked(&warehouse).deliver_pending();
                    writeln!(
                        out,
                        "{}",
                        stringify(&Json::Object(Object::from([("delivered".into(), Json::Int(delivered as i64))])), 2).expect("flat")
                    )?;
                    return result.map_err(|error| other(format!("delivery failed: {error:?}")));
                }
                let invocation = fulfillment(&program, &warehouse)?.invoke((&order,))?;
                save(&invocation, inspect)?;
                let result = invocation.decode::<Fulfillment>();
                if let Ok(result) = &result {
                    print_json(out, &result.to_json())?;
                }
                writeln!(out, "{}", invocation.render())?;
                result.map(drop).map_err(Failure::from)
            })();
            writeln!(out, "{}", stringify(&locked(&warehouse).summary(), 2).expect("flat"))?;
            outcome?;
        }
    }
    Ok(())
}

#[allow(dead_code)]
fn main() {
    let arguments: Vec<String> = std::env::args().skip(1).collect();
    if let Err(failure) = run(&arguments, &mut std::io::stdout()) {
        eprintln!("{failure}");
        std::process::exit(match failure {
            Failure::Sdk(_) => 1,
            Failure::Other(_) => 2,
        });
    }
}
