//! Offline SDK tests with synthetic identities and a fake Core.

use std::collections::BTreeMap;

use crate::host::{HandlerContext, HandlerError};
use crate::protocol::{BoundaryType, FailureCategory, HARD_LIMITS, Hash, Limits, Value};
use crate::{record, variants};

use super::data::Data;
use super::identities::{Entry, check_groups, display_to_store, hash_groups, normal, parse_signature};
use super::{Dynamic, Error, Operations, PrepareOptions, Runtime, RuntimeOptions};

fn id(letter: char) -> Hash {
    Hash::parse(&letter.to_string().repeat(64)).unwrap()
}

fn numbered(n: u32) -> Hash {
    Hash::parse(&format!("{n:064x}")).unwrap()
}

fn nominal(identity: Hash, arguments: Vec<BoundaryType>) -> BoundaryType {
    BoundaryType::Nominal(identity, arguments)
}

/// Data with synthetic identities for the builtins, an Incident record, an
/// Urgency enum, and a Triage union.
fn fixture(limits: Limits) -> Data {
    let names: BTreeMap<String, Hash> = [
        ("int", id('1')),
        ("real", id('2')),
        ("text", id('3')),
        ("bool", id('4')),
        ("hash", id('5')),
        ("list", id('6')),
        ("option", id('7')),
        ("incident", id('8')),
        ("urgency", id('9')),
        ("triage", numbered(10)),
    ]
    .into_iter()
    .map(|(name, hash)| (name.to_owned(), hash))
    .collect();
    let constructors: BTreeMap<String, Hash> = [
        ("true", id('a')),
        ("false", id('b')),
        ("some", id('c')),
        ("none", id('d')),
        ("cons", id('e')),
        ("nil", id('f')),
        ("incident", id('0')),
        ("page-now", numbered(1)),
        ("watch-only", numbered(2)),
        ("routed", numbered(3)),
        ("invalid-incident", numbered(4)),
    ]
    .into_iter()
    .map(|(name, hash)| (name.to_owned(), hash))
    .collect();
    let group = |head: &str, members: &[&str]| {
        let mut group = vec![Entry { name: head.into(), hash: names[head].clone() }];
        group.extend(members.iter().map(|name| Entry { name: (*name).into(), hash: constructors[*name].clone() }));
        group
    };
    let groups =
        [group("incident", &["incident"]), group("urgency", &["page-now", "watch-only"]), group("triage", &["routed", "invalid-incident"])];
    Data::new(names, constructors, &groups, limits)
}

#[derive(Clone, Debug, PartialEq)]
struct Incident {
    id: String,
    severity: i64,
    owner: Option<String>,
}
record!(Incident = "Incident" { id, severity, owner });

#[derive(Clone, Copy, Debug, PartialEq)]
enum Urgency {
    Page,
    Watch,
}
variants!(Urgency { Page = "PageNow", Watch = "WatchOnly" });

#[derive(Debug, PartialEq)]
enum Triage {
    Routed { id: String, urgency: Urgency },
    InvalidIncident(String),
}
variants!(Triage { Routed { id, urgency } = "Routed", InvalidIncident(id) = "InvalidIncident" });

#[test]
fn signatures_and_groups() {
    let names: BTreeMap<String, Hash> =
        [("list", '6'), ("line", '8'), ("int", '1'), ("option", '7'), ("text", '3'), ("inventory", '9'), ("dispatch", 'a')]
            .into_iter()
            .map(|(name, letter)| (name.to_owned(), id(letter)))
            .collect();
    let spec = parse_signature("(List Line, Int) ->{Inventory, Dispatch} Option Text", &names).unwrap();
    assert_eq!(spec.parameters, [nominal(id('6'), vec![nominal(id('8'), vec![])]), nominal(id('1'), vec![])]);
    assert_eq!(spec.effects, [id('9'), id('a')]);
    assert_eq!(spec.result, nominal(id('7'), vec![nominal(id('3'), vec![])]));
    assert_eq!(parse_signature("() ->{} ()", &names).unwrap().result, BoundaryType::Tuple(vec![]));
    assert_eq!(parse_signature("((Int, Text)) ->{} (Int)", &names).unwrap().result, nominal(id('1'), vec![]));
    assert_eq!(
        parse_signature("((Int, Text)) ->{} Int", &names).unwrap().parameters,
        [BoundaryType::Tuple(vec![nominal(id('1'), vec![]), nominal(id('3'), vec![])])]
    );
    for text in ["forall a | e. (List a) ->{| e} Bool", "(Missing) ->{} Int", "(Int) ->{} Int extra)", "(Int) ->{} Int $"] {
        assert!(parse_signature(text, &names).is_err(), "accepted {text:?}");
    }
    let output = format!("0 {}\n0:line {}\n0:line {}\nnoise\n1:other {}\n", id('1'), id('1'), id('2'), id('3'));
    let groups = hash_groups(&output).unwrap();
    assert_eq!(groups.len(), 2);
    assert_eq!(groups[0][1].hash, id('2'));
    assert!(matches!(hash_groups("nothing here"), Err(Error::Binding { .. })));
    let entry = |name: &str, hash| Entry { name: name.into(), hash };
    assert!(check_groups(&[vec![entry("x", id('1'))], vec![entry("x", id('2'))]]).is_err());
    assert!(check_groups(&[vec![entry("x", id('1'))], vec![entry("x", id('1'))]]).is_ok());
    for (input, output) in [("WorkToday", "work-today"), ("PageNow", "page-now"), ("text", "text"), ("Inventory", "inventory")] {
        assert_eq!(display_to_store(input), output);
    }
    assert_eq!(normal("lookup_stock"), "lookup-stock");
}

#[test]
fn native_values_roundtrip_and_conform_to_checked_types() {
    let data = fixture(HARD_LIMITS);
    let rows =
        vec![Incident { id: "a".into(), severity: 5, owner: None }, Incident { id: "b".into(), severity: 2, owner: Some("ops".into()) }];
    let list = nominal(id('6'), vec![nominal(id('8'), vec![])]);
    let value = data.encode(&rows).unwrap();
    data.conform(&value, &list, "rows").unwrap();
    assert_eq!(data.decode::<Vec<Incident>>(&value).unwrap(), rows);
    let real = nominal(id('2'), vec![]);
    for number in [1.0, -0.0, f64::INFINITY, f64::NAN] {
        let value = data.encode(&number).unwrap();
        data.conform(&value, &real, "real").unwrap();
        assert_eq!(data.decode::<f64>(&value).unwrap().to_bits(), number.to_bits());
    }
    let integer = nominal(id('1'), vec![]);
    assert!(matches!(data.encode(&(1u64 << 63)), Err(Error::InvalidArgument(_))));
    assert!(matches!(data.encode(&i64::MIN), Err(Error::InvalidArgument(_))));
    assert_eq!(data.encode(&crate::json::INT_MAX).unwrap(), Value::Int(crate::json::INT_MAX));
    assert!(data.decode::<i8>(&Value::Int(300)).is_err());
    assert!(data.decode::<u8>(&Value::Int(-1)).is_err());
    let refused = |value: Value, expected: &BoundaryType| data.conform(&value, expected, "argument[0]").unwrap_err().message;
    assert_eq!(refused(data.encode("x").unwrap(), &integer), "argument[0]: expected an integer");
    assert_eq!(refused(data.encode(&3).unwrap(), &real), "argument[0]: expected a Real (f64)");
    assert!(refused(data.encode(&Urgency::Page).unwrap(), &nominal(id('8'), vec![])).contains("expected a incident value"));
    let late = vec!["ok".to_owned(), "ok".to_owned()];
    let texts = data.encode(&late).unwrap();
    assert_eq!(refused(texts, &nominal(id('6'), vec![integer.clone()])), "argument[0][0]: expected an integer");
    let unit = BoundaryType::Tuple(vec![]);
    data.conform(&data.encode(&()).unwrap(), &unit, "unit").unwrap();
    assert!(data.conform(&data.encode(&(1, 2)).unwrap(), &unit, "unit").is_err());
    // A record with an unknown constructor is refused at its location.
    struct Unknown {
        field: i64,
    }
    record!(Unknown = "Unknown" { field });
    let error = data.encode(&Unknown { field: 1 }).unwrap_err().to_string();
    assert_eq!(error, "value: unknown constructor Unknown");
}

#[test]
fn options_enums_unions_and_dynamic_values() {
    let data = fixture(HARD_LIMITS);
    let routed = Triage::Routed { id: "r".into(), urgency: Urgency::Page };
    let value = data.encode(&routed).unwrap();
    assert_eq!(value, Value::Constructor(numbered(3), vec![Value::Text("r".into()), Value::Constructor(numbered(1), vec![])]));
    assert_eq!(data.decode::<Triage>(&value).unwrap(), routed);
    let invalid = data.encode(&Triage::InvalidIncident("x".into())).unwrap();
    assert_eq!(data.decode::<Triage>(&invalid).unwrap(), Triage::InvalidIncident("x".into()));
    assert!(data.decode::<Urgency>(&invalid).unwrap_err().to_string().contains("unknown Urgency case"));
    let short = Value::Constructor(numbered(3), vec![Value::Text("r".into())]);
    assert!(data.decode::<Triage>(&short).unwrap_err().to_string().contains("wrong constructor or field count"));
    let option = nominal(id('7'), vec![nominal(id('7'), vec![nominal(id('1'), vec![])])]);
    for nested in [None, Some(None), Some(Some(3i64))] {
        let value = data.encode(&nested).unwrap();
        data.conform(&value, &option, "option").unwrap();
        assert_eq!(data.decode::<Option<Option<i64>>>(&value).unwrap(), nested);
        let expected = match nested {
            None => Dynamic::Option(None),
            Some(None) => Dynamic::Option(Some(Box::new(Dynamic::Option(None)))),
            Some(Some(n)) => Dynamic::Option(Some(Box::new(Dynamic::Option(Some(Box::new(Dynamic::Int(n))))))),
        };
        assert_eq!(data.decode::<Dynamic>(&value).unwrap(), expected);
    }
    let watch = data.make("WatchOnly", []).unwrap();
    assert_eq!(watch, Dynamic::variant("watch-only", []));
    assert_eq!(data.decode::<Urgency>(&data.encode(&watch).unwrap()).unwrap(), Urgency::Watch);
    assert!(matches!(data.make("Missing", []), Err(Error::Binding { .. })));
    assert!(data.encode(&Dynamic::variant("Some", [1.into()])).is_err(), "a builtin constructor by name");
    let dynamic = Dynamic::List(vec![Dynamic::Tuple(vec![true.into(), "x".into(), 2.5.into()]), Dynamic::Hash(id('5'))]);
    assert_eq!(data.decode::<Dynamic>(&data.encode(&dynamic).unwrap()).unwrap(), dynamic);
    assert!(data.decode::<Dynamic>(&Value::Constructor(id('9'), vec![])).unwrap_err().to_string().contains("unmapped constructor"));
    assert!(data.encode(&vec![(); 40]).unwrap_err().to_string().contains("JSON depth"), "a list deeper than the JSON depth");
    assert_eq!(data.identity("Urgency").unwrap(), id('9'));
    assert!(data.identity("missing").is_err());
}

#[test]
fn selected_limits_bound_mapped_values() {
    let limits = Limits { max_collection_items: 2, max_text_bytes: 3, ..HARD_LIMITS };
    let data = fixture(limits);
    assert!(data.encode(&vec![1, 2, 3]).unwrap_err().to_string().contains("max_collection_items"));
    assert!(data.encode("four").unwrap_err().to_string().contains("max_text_bytes"));
    let long = HARD_LIMITS_DATA.with(|data| data.encode(&vec![1, 2, 3]).unwrap());
    assert!(data.decode::<Vec<i64>>(&long).unwrap_err().to_string().contains("selected limit"));
    let nested = Incident { id: "abcd".into(), severity: 1, owner: None };
    assert_eq!(data.encode(&nested).unwrap_err().to_string(), "value.id: text exceeds max_text_bytes");
}

thread_local! {
    static HARD_LIMITS_DATA: Data = fixture(HARD_LIMITS);
}

fn failure(error: HandlerError) -> Option<FailureCategory> {
    match error {
        HandlerError::Failure(category, _) => Some(category),
        HandlerError::Unknown => None,
    }
}

#[test]
fn arguments_are_tuples_or_dynamic_slices() {
    use super::{Arguments, Encode};
    let data = fixture(HARD_LIMITS);
    let tuple = (35, "x", Some(true)).encode_all(&data, "f", 0).unwrap();
    let values: [&dyn Encode; 3] = [&35, &"x", &Some(true)];
    assert_eq!(values.encode_all(&data, "f", 0).unwrap(), tuple);
    assert_eq!(values[..].encode_all(&data, "f", 0).unwrap(), tuple);
    assert!(().encode_all(&data, "f", 0).unwrap().is_empty());
    let error = (1, u64::MAX).encode_all(&data, "f", 2).unwrap_err();
    assert_eq!(error.message, "f.argument[3]: integer is outside Core's 63-bit Int range");
}

#[test]
fn closures_adapt_to_handlers() {
    let data = fixture(HARD_LIMITS);
    let context = HandlerContext::for_test();
    let calls = std::sync::Arc::new(std::sync::atomic::AtomicUsize::new(0));
    let counted = calls.clone();
    let service = Operations::new()
        .on("read-document", move |context: &HandlerContext, key: String| {
            counted.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
            match key.as_str() {
                _ if context.is_cancelled() => Err(HandlerError::completion_unknown("cancelled")),
                "example" => Ok("text".to_owned()),
                _ => Err(HandlerError::refused("unknown document")),
            }
        })
        .on("forget", |_: String| -> Result<(), HandlerError> { Err(std::io::Error::other("private detail"))? })
        .on("lookup_stock", |sku: String| Ok(sku.len()))
        .on("pair", || Ok((3, 4)))
        .on("route", |incident: Incident| Ok(Triage::Routed { id: incident.id, urgency: Urgency::Watch }))
        .on("huge", || Ok(u64::MAX));
    let handler = |name: &str| service.handlers[name].clone();
    let text = |text: &str| Value::Text(text.into());
    assert_eq!(handler("read-document")(&data, &context, &[text("example")]).unwrap(), text("text"));
    assert_eq!(calls.load(std::sync::atomic::Ordering::SeqCst), 1);
    let refusal = handler("read-document")(&data, &context, &[text("other")]).unwrap_err();
    assert_eq!(failure(refusal), Some(FailureCategory::RefusedAuthority));
    let arity = handler("read-document")(&data, &context, &[]).unwrap_err();
    assert_eq!(arity, HandlerError::outside_failed("handler parameter count differs from the request"));
    let mapping = handler("read-document")(&data, &context, &[Value::Int(1)]).unwrap_err();
    assert_eq!(mapping, HandlerError::outside_failed("handler argument cannot be mapped"));
    assert_eq!(calls.load(std::sync::atomic::Ordering::SeqCst), 2, "a handler ran with unmappable arguments");
    assert_eq!(handler("forget")(&data, &context, &[text("x")]).unwrap_err(), HandlerError::Unknown, "a plain error stays unclassified");
    assert_eq!(handler("lookup-stock")(&data, &context, &[text("abc")]).unwrap(), Value::Int(3));
    assert_eq!(handler("pair")(&data, &context, &[]).unwrap(), Value::Tuple(vec![Value::Int(3), Value::Int(4)]));
    let incident = data.encode(&Incident { id: "i".into(), severity: 1, owner: None }).unwrap();
    assert_eq!(
        data.decode::<Triage>(&handler("route")(&data, &context, &[incident]).unwrap()).unwrap(),
        Triage::Routed { id: "i".into(), urgency: Urgency::Watch }
    );
    let huge = handler("huge")(&data, &context, &[]).unwrap_err();
    assert_eq!(huge, HandlerError::outside_failed("handler result cannot be represented"));
    let replaced = Operations::new().on("op", || Ok(1)).on("op", || Ok(2));
    assert_eq!(replaced.handlers["op"](&data, &context, &[]).unwrap(), Value::Int(2));
}

#[test]
fn runtime_options_are_validated_before_core_runs() {
    let prelude = Some(std::env::temp_dir());
    let deadline =
        Runtime::new(RuntimeOptions { prelude: prelude.clone(), deadline: Some(std::time::Duration::ZERO), ..Default::default() });
    assert!(matches!(deadline, Err(Error::Binding { .. })));
    let limits = Limits { max_frame_bytes: 0, ..HARD_LIMITS };
    let invalid = Runtime::new(RuntimeOptions { prelude: prelude.clone(), limits: Some(limits), ..Default::default() });
    assert!(matches!(invalid, Err(Error::Binding { message, .. }) if message.starts_with("invalid runtime limits")));
    let runtime = Runtime::new(RuntimeOptions { prelude, binary: Some("/no/such/jacquard".into()), ..Default::default() }).unwrap();
    for sources in [vec!["rules.txt"], vec![], vec!["a.jac", "b.jqd"]] {
        assert!(matches!(runtime.prepare(&sources, &PrepareOptions::default()), Err(Error::Preparation(_))), "{sources:?}");
    }
    let missing = runtime.prepare(&["rules.jac"], &PrepareOptions::default()).err().unwrap().to_string();
    assert!(missing.contains("cannot find Core executable /no/such/jacquard"), "{missing}");
    assert_eq!(runtime.core_commands(), 0);
}

/// A fake Core: a shell script that logs its arguments, answers
/// `--version`, leaves a descendant holding its output, and fails `check`.
#[cfg(unix)]
mod fake {
    use std::os::unix::fs::PermissionsExt;
    use std::path::PathBuf;
    use std::time::{Duration, Instant};

    use super::super::{Error, PrepareOptions, Runtime, RuntimeOptions};

    pub struct Fake {
        pub root: PathBuf,
        pub runtime: Runtime,
    }

    impl Drop for Fake {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.root);
        }
    }

    impl Fake {
        pub fn new(name: &str, version: &str) -> Fake {
            let root = std::env::temp_dir().join(format!("jacquard-host-rust-fake-{name}-{}", std::process::id()));
            let _ = std::fs::remove_dir_all(&root);
            std::fs::create_dir_all(root.join("prelude")).unwrap();
            std::fs::write(root.join("prelude/01-prim.jqd"), "prelude\n").unwrap();
            std::fs::write(root.join("rules.jac"), "rules\n").unwrap();
            let log = root.join("log");
            let script = format!(
                "#!/bin/sh\necho \"$1\" >> '{}'\ncase \"$1\" in\n--version) echo 'jacquard {version}'; sleep 30 & ;;\ncheck) echo 'boom' >&2; exit 3 ;;\nesac\n",
                log.display()
            );
            let binary = root.join("jacquard");
            std::fs::write(&binary, script).unwrap();
            std::fs::set_permissions(&binary, std::fs::Permissions::from_mode(0o755)).unwrap();
            let options =
                RuntimeOptions { binary: Some(binary.to_string_lossy().into()), prelude: Some(root.join("prelude")), ..Default::default() };
            Fake { runtime: Runtime::new(options).unwrap(), root }
        }

        pub fn prepare(&self) -> (Error, Duration) {
            let started = Instant::now();
            let options = PrepareOptions { cache_dir: Some(self.root.join("cache")) };
            let error = self.runtime.prepare(&[self.root.join("rules.jac")], &options).expect_err("the fake Core fails");
            (error, started.elapsed())
        }

        pub fn calls(&self, command: &str) -> usize {
            std::fs::read_to_string(self.root.join("log")).unwrap_or_default().lines().filter(|line| *line == command).count()
        }

        pub fn staging_left(&self) -> bool {
            let entries = std::fs::read_dir(self.root.join("cache"));
            entries.is_ok_and(|entries| entries.flatten().any(|entry| entry.file_name().to_string_lossy().starts_with("prepare-")))
        }
    }
}

#[cfg(unix)]
#[test]
fn version_is_checked_once_and_failures_are_reported_without_waiting_for_descendants() {
    let fake = fake::Fake::new("once", crate::compat::CORE_VERSION);
    let errors: Vec<_> = std::thread::scope(|scope| {
        let workers: Vec<_> = (0..4).map(|_| scope.spawn(|| fake.prepare())).collect();
        workers.into_iter().map(|worker| worker.join().unwrap()).collect()
    });
    for (error, elapsed) in &errors {
        assert!(matches!(error, Error::Preparation(message) if message == "Core check failed: boom"), "{error}");
        assert!(elapsed.as_secs() < 10, "a descendant holding Core's output delayed preparation: {elapsed:?}");
    }
    assert_eq!(fake.calls("--version"), 1);
    assert_eq!(fake.calls("check"), 4);
    assert!(!fake.staging_left(), "a staging directory was left behind");
}

#[cfg(unix)]
#[test]
fn a_version_mismatch_is_refused_and_checked_again() {
    let fake = fake::Fake::new("mismatch", "9.9.9");
    for _ in 0..2 {
        let (error, _) = fake.prepare();
        let message = error.to_string();
        assert!(message.contains("expected pinned Core version") && message.contains("jacquard 9.9.9"), "{message}");
    }
    assert_eq!(fake.calls("--version"), 2);
    assert_eq!(fake.calls("check"), 0);
}
