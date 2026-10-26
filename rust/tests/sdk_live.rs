//! Live SDK tests against the real Core named by `JACQUARD_BIN` and
//! `JACQUARD_PRELUDE`; each test skips without them.

use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};

use jacquard_host::carrier::Cancel;
use jacquard_host::host::{Failure, HandlerContext, HandlerError};
use jacquard_host::json::{INT_MAX, INT_MIN, Json, decode_object};
use jacquard_host::sdk::{Dynamic, Error, Function, Operations, PrepareOptions, Program, Runtime, RuntimeOptions};
use jacquard_host::{record, variants};

/// A runtime and a scratch directory, removed on drop.
struct Live {
    runtime: Runtime,
    root: PathBuf,
}

impl Drop for Live {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

static NEXT: AtomicUsize = AtomicUsize::new(0);

fn live() -> Option<Live> {
    if std::env::var_os("JACQUARD_BIN").is_none() || std::env::var_os("JACQUARD_PRELUDE").is_none() {
        // CI sets JACQUARD_REQUIRE_LIVE so a misconfigured live job fails.
        assert!(std::env::var_os("JACQUARD_REQUIRE_LIVE").is_none(), "live tests need JACQUARD_BIN and JACQUARD_PRELUDE");
        eprintln!("skipped: live tests need JACQUARD_BIN and JACQUARD_PRELUDE");
        return None;
    }
    let root = std::env::temp_dir().join(format!("jacquard-host-rust-sdk-{}-{}", std::process::id(), NEXT.fetch_add(1, Ordering::SeqCst)));
    std::fs::create_dir_all(&root).unwrap();
    Some(Live { runtime: Runtime::new(RuntimeOptions::default()).unwrap(), root })
}

fn example(project: &str) -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../examples").join(project).join("rules.jac")
}

impl Live {
    fn options(&self) -> PrepareOptions {
        PrepareOptions { cache_dir: Some(self.root.join("cache")) }
    }

    fn prepare(&self, source: &Path) -> Program {
        self.runtime.prepare(&[source], &self.options()).unwrap()
    }

    fn source(&self, name: &str, text: &str) -> PathBuf {
        let path = self.root.join(name);
        std::fs::write(&path, text).unwrap();
        path
    }
}

#[test]
fn live_quickstart_calls_invocations_and_saved_frames() {
    let Some(s) = live() else { return };
    let shipping = s.prepare(&example("quickstart")).function("shipping-cost").unwrap();
    for (total, expected) in [(35, 5), (75, 0)] {
        assert_eq!(shipping.call::<i64>((total,)).unwrap(), expected);
    }
    let invocation = shipping.invoke((35,)).unwrap();
    assert_eq!(invocation.value().unwrap(), Dynamic::Int(5));
    assert_eq!(invocation.render(), "shipping-cost: ok");
    let path = s.root.join("inspection.json");
    invocation.save(&path).unwrap();
    let raw = std::fs::read(&path).unwrap();
    let saved = decode_object(&raw[..raw.len() - 1], 128).unwrap();
    assert_eq!(saved["schema"], Json::from("jacquard-host-inspection-v1"));
    assert_eq!(saved["core_frames"].as_array().unwrap().len(), invocation.raw.core_frames.len());
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        assert_eq!(std::fs::metadata(&path).unwrap().permissions().mode() & 0o777, 0o600);
    }
}

struct TriagePolicy {
    page: i64,
    stale: i64,
}
record!(TriagePolicy = "TriagePolicy" { page, stale });

#[derive(Clone)]
struct Incident {
    id: String,
    service: String,
    severity: i64,
    age: i64,
    visible: bool,
    owner: Option<String>,
}
record!(Incident = "Incident" { id, service, severity, age, visible, owner });

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Urgency {
    Page,
    Today,
    Watch,
}
variants!(Urgency { Page = "PageNow", Today = "WorkToday", Watch = "WatchOnly" });

#[derive(Debug, PartialEq, Eq)]
enum Triage {
    Routed { id: String, team: String, urgency: Urgency, reasons: Vec<String> },
    InvalidIncident(String),
}
variants!(Triage {
    Routed { id, team, urgency, reasons } = "Routed",
    InvalidIncident(id) = "InvalidIncident",
});

fn incident(i: i64) -> Incident {
    Incident {
        id: i.to_string(),
        service: "api".into(),
        severity: i % 5 + 1,
        age: i * 3,
        visible: i % 2 == 0,
        owner: (i % 3 != 0).then(|| "ops".into()),
    }
}

fn triage(s: &Live) -> (Program, Function) {
    let program = s.prepare(&example("triage"));
    let route = program.function("triage.batch").unwrap();
    (program, route)
}

#[test]
fn live_records_enums_options_and_explicit_batches() {
    let Some(s) = live() else { return };
    let (_, route) = triage(&s);
    let policy = TriagePolicy { page: 4, stale: 60 };
    let rows: Vec<Incident> = (0..64).map(incident).collect();
    let results: Vec<Triage> = route.map_batches((&policy,), &rows, 8).unwrap();
    assert_eq!(results.len(), rows.len());
    for (row, got) in rows.iter().zip(&results) {
        let page = row.visible && row.severity >= 4;
        let mut reasons = Vec::new();
        if row.owner.is_none() {
            reasons.push("unowned".to_owned());
        }
        if row.age >= 60 {
            reasons.push("ageing".to_owned());
        }
        if page {
            reasons.push("customer-impact".to_owned());
        }
        let urgency = if page {
            Urgency::Page
        } else if row.age >= 60 || row.severity == 5 {
            Urgency::Today
        } else {
            Urgency::Watch
        };
        let team = row.owner.clone().unwrap_or_else(|| "api-oncall".into());
        assert_eq!(*got, Triage::Routed { id: row.id.clone(), team, urgency, reasons }, "row {}", row.id);
    }
    let empty: Vec<Triage> = route.map_batches((&policy,), &[] as &[Incident], 8).unwrap();
    assert!(empty.is_empty());
}

#[test]
fn live_invalid_input_is_refused_before_any_worker() {
    let Some(s) = live() else { return };
    let mut program = s.prepare(&example("triage"));
    // A spawn would now fail as worker_spawn_failed, not as a local refusal.
    program.set_worker_command(vec!["/no/such/jacquard".into()]);
    let route = program.function("triage.batch").unwrap();
    let policy = TriagePolicy { page: 4, stale: 60 };
    let mut rows: Vec<Incident> = (0..17).map(incident).collect();
    rows[16].severity = INT_MAX + 1;
    let error = route.map_batches::<Triage, _>((&policy,), &rows, 8).unwrap_err();
    assert!(error.index.is_none() && error.completed.is_empty());
    assert!(
        matches!(&error.error, Error::InvalidArgument(argument) if argument.message.contains("argument[1][0].severity")),
        "late invalid row: {error}"
    );
    let many = vec![incident(1); 64];
    let error = route.call::<Vec<Triage>>((&policy, &many)).unwrap_err();
    assert!(matches!(error, Error::InvalidArgument(_)), "over-deep single call: {error}");
    let error = route.call::<Vec<Triage>>((&policy, "not a list")).unwrap_err();
    assert!(matches!(&error, Error::InvalidArgument(argument) if argument.message.contains("argument[1]")), "{error}");
    let error = route.call::<Vec<Triage>>((&policy,)).unwrap_err();
    assert!(matches!(&error, Error::InvalidArgument(argument) if argument.message.contains("expected 2 arguments")), "{error}");
}

#[test]
fn live_missing_handlers_refuse_binding_and_pure_calls_need_no_services() {
    let Some(s) = live() else { return };
    let mut fulfillment = s.prepare(&example("fulfillment"));
    let calls = Arc::new(AtomicUsize::new(0));
    let counted = calls.clone();
    let inventory = Operations::new().on("booking-status", move |_: Dynamic| {
        counted.fetch_add(1, Ordering::SeqCst);
        Ok(Dynamic::unit())
    });
    let error = fulfillment.function_with("fulfillment.place", [("Inventory", inventory.clone())]).unwrap_err();
    let message = error.to_string();
    assert!(matches!(error, Error::Binding { .. }), "{message}");
    assert!(message.contains("inventory.lookup-stock") && message.contains("dispatch.publish-booking"), "{message}");
    assert_eq!(calls.load(Ordering::SeqCst), 0, "a handler ran during binding");
    let typo = Operations::new().on("lookup_stok", |_: String| Ok(()));
    let error = fulfillment.function_with("fulfillment.place", [("Inventory", typo)]).unwrap_err();
    assert!(error.to_string().contains("inventory has no operation lookup-stok"), "{error}");
    fulfillment.provide("Inventory", inventory).unwrap();
    let valid = fulfillment.function("order.valid-lines").unwrap();
    assert!(valid.call::<bool>((Vec::<Dynamic>::new(), Vec::<String>::new())).unwrap());
    assert!(matches!(fulfillment.provide("Unknown", Operations::new()), Err(Error::Binding { .. })));
    assert!(matches!(fulfillment.function("no-such-function"), Err(Error::Binding { .. })));
}

#[test]
fn live_unknown_completion_happens_once_with_raw_evidence() {
    let Some(s) = live() else { return };
    let fulfillment = s.prepare(&example("fulfillment"));
    let calls = Arc::new(AtomicUsize::new(0));
    let counted = calls.clone();
    let warehouse = Operations::new()
        .on("booking-status", |_: Dynamic| Ok(Dynamic::variant("BookingPending", [])))
        .on("lookup_stock", |_: String| Ok(Dynamic::variant("Stock", [20.into()])))
        .on("reserve-order", move |context: &HandlerContext, _: Dynamic| {
            assert!(!context.is_cancelled());
            counted.fetch_add(1, Ordering::SeqCst);
            Ok(Dynamic::variant("Booked", ["receipt-1".into()]))
        })
        .on("publish-booking", |_: String, _: String| -> Result<(), HandlerError> {
            Err(std::io::Error::other("synthetic after commit"))?
        });
    let place = fulfillment.function_with("fulfillment.place", [("Inventory", warehouse.clone()), ("Dispatch", warehouse)]).unwrap();
    let data = fulfillment.data();
    let line = data.make("Line", ["sensor".into(), 2.into()]).unwrap();
    let order = data.make("Order", ["one".into(), Dynamic::List(vec![line])]).unwrap();
    let error = place.call::<Dynamic>((&order,)).unwrap_err();
    assert_eq!(error.failure(), Some(Failure::OperationFailed), "{error}");
    let result = error.result().unwrap();
    assert_eq!(result.code(), "E1612");
    assert!(result.host_frames.iter().any(|frame| frame.get("kind") == Some(&Json::from("effect_ok"))));
    assert_eq!(calls.load(Ordering::SeqCst), 1);
    let invocation = place.invoke((&order,)).unwrap();
    let rendered = invocation.render();
    assert!(rendered.starts_with("fulfillment.place: E1612") && rendered.contains(". publish-booking"), "{rendered}");
}

struct Job {
    id: String,
    effort: i64,
    value: i64,
    latest: i64,
}
record!(Job = "Job" { id, effort, value, latest });

#[derive(Debug)]
struct Assignment {
    id: String,
    slot: i64,
}
record!(Assignment = "Assignment" { id, slot });

#[derive(Debug)]
struct Plan {
    score: i64,
    assignments: Vec<Assignment>,
}
record!(Plan = "Plan" { score, assignments });

#[derive(Debug)]
enum Planning {
    Planned { plan: Plan },
    InvalidPlan(String),
}
variants!(Planning { Planned { plan } = "Planned", InvalidPlan(reason) = "InvalidPlan" });

fn optimum(jobs: &[Job], caps: &[i64]) -> i64 {
    let Some((job, rest)) = jobs.split_first() else { return 0 };
    let mut best = optimum(rest, caps);
    for slot in 0..(job.latest as usize).min(caps.len()) {
        if caps[slot] >= job.effort {
            let mut next = caps.to_vec();
            next[slot] -= job.effort;
            best = best.max(job.value + optimum(rest, &next));
        }
    }
    best
}

#[test]
fn live_planner_matches_an_independent_exhaustive_oracle() {
    let Some(s) = live() else { return };
    let solve = s.prepare(&example("planner")).function("planner.plan").unwrap();
    for seed in 0..25i64 {
        let jobs: Vec<Job> = (0..seed % 6 + 1)
            .map(|i| Job {
                id: format!("job-{i}"),
                effort: (seed + i * 3) % 4 + 1,
                value: (seed * 7 + i * 11) % 15,
                latest: (seed + i) % 3 + 1,
            })
            .collect();
        let caps = [seed % 5 + 1, seed % 3 + 2, 3][..(seed % 3 + 1) as usize].to_vec();
        let Planning::Planned { plan } = solve.call::<Planning>((&jobs, &caps)).unwrap() else { panic!("seed {seed}: not planned") };
        assert_eq!(plan.score, optimum(&jobs, &caps), "seed {seed}");
        let mut loads = vec![0; caps.len()];
        let mut seen = std::collections::BTreeSet::new();
        let mut score = 0;
        for assignment in &plan.assignments {
            let job = jobs.iter().find(|job| job.id == assignment.id).expect("a known job");
            assert!(
                seen.insert(&assignment.id) && assignment.slot >= 1 && assignment.slot <= job.latest.min(caps.len() as i64),
                "{assignment:?}"
            );
            loads[assignment.slot as usize - 1] += job.effort;
            score += job.value;
        }
        assert!(loads.iter().zip(&caps).all(|(load, cap)| load <= cap), "seed {seed}: overloaded");
        assert_eq!(score, plan.score, "seed {seed}");
    }
}

#[test]
fn live_cache_reuse_invalidation_and_corruption() {
    let Some(s) = live() else { return };
    let quick = s.prepare(&example("quickstart"));
    let commands = s.runtime.core_commands();
    let repeated = s.prepare(&example("quickstart"));
    assert_eq!(s.runtime.core_commands(), commands, "a warm preparation ran Core");
    assert_eq!(repeated.directory(), quick.directory());
    let original = std::fs::read_to_string(example("quickstart")).unwrap();
    let changed = s.source("changed.jac", &format!("{}\r\n-- changed\r\n", original.replace('\n', "\r\n")));
    let program = s.prepare(&changed);
    let snapshot = std::fs::read_to_string(program.directory().join("source.jac")).unwrap();
    assert!(program.directory() != quick.directory() && snapshot.contains("\r\n"), "not a byte-exact new entry");
    std::fs::write(program.directory().join("store").join("unexpected"), "corrupt").unwrap();
    let error = s.runtime.prepare(&[&changed], &s.options()).expect_err("a corrupt cache was accepted");
    assert!(matches!(error, Error::Preparation(_)), "{error}");
    assert!(matches!(s.runtime.prepare(&[s.source("rules.txt", "")], &s.options()), Err(Error::Preparation(_))));
}

#[test]
fn live_full_int_range_reals_and_nested_options() {
    let Some(s) = live() else { return };
    let program = s.prepare(&s.source(
        "values.jac",
        "value.int : (Int) ->{} Int\nvalue.int(x) = x\nvalue.real : (Real) ->{} Real\nvalue.real(x) = x\nvalue.option : (Option (Option Int)) ->{} Option (Option Int)\nvalue.option(x) = x\n",
    ));
    let integer = program.function("value.int").unwrap();
    for value in [INT_MIN, INT_MAX] {
        assert_eq!(integer.call::<i64>((value,)).unwrap(), value);
    }
    assert!(matches!(integer.call::<i64>((INT_MAX + 1,)), Err(Error::InvalidArgument(_))));
    assert!(matches!(integer.call::<i64>((1.5,)), Err(Error::InvalidArgument(_))));
    assert!(matches!(integer.call::<i8>((300,)), Err(Error::Binding { result: Some(_), .. })));
    let real = program.function("value.real").unwrap();
    for value in [1.0, -0.0, f64::INFINITY, f64::NAN] {
        assert_eq!(real.call::<f64>((value,)).unwrap().to_bits(), value.to_bits());
    }
    let option = program.function("value.option").unwrap();
    assert_eq!(option.call::<Option<Option<i64>>>((None::<Option<i64>>,)).unwrap(), None);
    assert_eq!(option.call::<Option<Option<i64>>>((Some(None::<i64>),)).unwrap(), Some(None));
    assert_eq!(option.call::<Option<Option<i64>>>((Some(Some(3)),)).unwrap(), Some(Some(3)));
    let nested = Dynamic::Option(Some(Box::new(Dynamic::Option(None))));
    assert_eq!(option.call::<Dynamic>((&nested,)).unwrap(), nested);
}

#[test]
fn live_spawn_failures_and_cancellation() {
    let Some(s) = live() else { return };
    let mut quick = s.prepare(&example("quickstart"));
    let shipping = quick.function("shipping-cost").unwrap();
    quick.set_worker_command(vec!["/no/such/jacquard".into()]);
    let error = quick.function("shipping-cost").unwrap().call::<i64>((10,)).unwrap_err();
    assert_eq!(error.failure(), Some(Failure::WorkerLost), "{error}");
    assert_eq!(error.result().unwrap().host_evidence.get("reason"), Some(&Json::from("worker_spawn_failed")));
    let cancel = Cancel::new();
    cancel.cancel();
    let invocation = shipping.invoke_with((10,), &cancel).unwrap();
    assert_eq!(invocation.raw.host_evidence.get("reason"), Some(&Json::from("aborted")));
    assert!(invocation.error().is_some());
}

#[test]
fn live_output_overflow_fails_once_without_adaptive_batching() {
    let Some(s) = live() else { return };
    let (_, route) = triage(&s);
    let rows: Vec<Incident> =
        (0..29).map(|i| Incident { id: i.to_string(), service: "api".into(), severity: 5, age: 90, visible: true, owner: None }).collect();
    let error = route.map_batches::<Triage, _>((TriagePolicy { page: 4, stale: 60 },), &rows, 29).unwrap_err();
    assert_eq!(error.index, Some(0), "{error}");
    assert!(error.completed.is_empty());
    assert_eq!(error.error.result().map(|result| result.code()), Some("E1602".into()));
}

#[test]
fn live_result_mapping_failure_retains_the_successful_result() {
    let Some(s) = live() else { return };
    let shipping = s.prepare(&example("quickstart")).function("shipping-cost").unwrap();
    let invocation = shipping.invoke((35,)).unwrap();
    assert!(invocation.ok());
    let error = invocation.decode::<String>().unwrap_err();
    assert!(matches!(&error, Error::Binding { result: Some(result), .. } if result.ok()), "{error}");
}

fn copy_tree(from: &Path, to: &Path) {
    std::fs::create_dir_all(to).unwrap();
    for entry in std::fs::read_dir(from).unwrap() {
        let entry = entry.unwrap();
        let target = to.join(entry.file_name());
        if entry.file_type().unwrap().is_dir() {
            copy_tree(&entry.path(), &target);
        } else {
            std::fs::copy(entry.path(), target).unwrap();
        }
    }
}

#[test]
fn live_prelude_contents_key_the_cache_and_concurrent_preparation_publishes_once() {
    let Some(s) = live() else { return };
    let quick = s.prepare(&example("quickstart"));
    let copy = s.root.join("prelude");
    copy_tree(Path::new(&std::env::var_os("JACQUARD_PRELUDE").unwrap()), &copy);
    let copied = Runtime::new(RuntimeOptions { prelude: Some(copy.clone()), ..RuntimeOptions::default() }).unwrap();
    let before = copied.prepare(&[example("quickstart")], &s.options()).unwrap();
    assert_eq!(before.directory(), quick.directory(), "an identical prelude copy should share the entry");
    let license = copy.join("LICENSE.md");
    let mut text = std::fs::read(&license).unwrap();
    text.push(b'\n');
    std::fs::write(&license, text).unwrap();
    let after = copied.prepare(&[example("quickstart")], &s.options()).unwrap();
    assert_ne!(after.directory(), before.directory(), "a prelude change did not change the cache key");
    let source = s
        .source("concurrent.jac", "shipping-cost : (Int) ->{} Int\nshipping-cost(total) = if lt(total, 50) then 5 else 0\n-- concurrent\n");
    let programs: Vec<Program> = std::thread::scope(|scope| {
        let workers: Vec<_> = (0..2).map(|_| scope.spawn(|| s.runtime.prepare(&[&source], &s.options()).unwrap())).collect();
        workers.into_iter().map(|worker| worker.join().unwrap()).collect()
    });
    assert_eq!(programs[0].directory(), programs[1].directory());
    assert_eq!(programs[1].function("shipping-cost").unwrap().call::<i64>((35,)).unwrap(), 5);
    for entry in std::fs::read_dir(s.root.join("cache")).unwrap() {
        let name = entry.unwrap().file_name();
        assert!(!name.to_string_lossy().starts_with("prepare-"), "staging directory left behind: {name:?}");
    }
}
