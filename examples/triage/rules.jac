-- Synthetic operational incidents. Ordinary business outcomes are typed data.
type Incident = | Incident(id: Text, service: Text, severity: Int, age: Int, visible: Bool, owner: Option Text)
-- id, service, severity 1..5, age in minutes, customer-visible, owner
type Urgency = | PageNow | WorkToday | WatchOnly
type Triage = | Routed Text Text Urgency (List Text) | InvalidIncident Text

type TriagePolicy = | TriagePolicy Int Int
-- page threshold (severity), ageing threshold (minutes)
triage.one : (TriagePolicy, Incident) ->{} Triage
triage.one(policy, event) = match (policy, event) {
  | (TriagePolicy(page, stale), Incident(id: id, service: service, severity: severity, age: age, visible: visible, owner: owner)) ->
    if bool.or(text.empty?(id), bool.or(lt(severity, 1), bool.or(lt(5, severity), lt(age, 0))))
    then InvalidIncident(id)
    else {
      let reasons = if bool.and(visible, bool.not(lt(severity, page))) then ["customer-impact"] else []
      let reasons = if bool.not(lt(age, stale)) then Cons("ageing", reasons) else reasons
      let reasons = match owner { | None -> Cons("unowned", reasons) | Some(_) -> reasons }
      let urgency = if bool.and(visible, bool.not(lt(severity, page))) then PageNow
                    else if bool.or(bool.not(lt(age, stale)), eq(severity, 5)) then WorkToday
                    else WatchOnly
      let team = match owner { | Some(name) -> name | None -> text.concat(service, "-oncall") }
      Routed(id, team, urgency, reasons)
    }
}

triage.batch : (TriagePolicy, List Incident) ->{} List Triage
triage.batch(policy, incidents) = list.map(incidents, fn (event) -> triage.one(policy, event))

-- Smaller boundary payload for testing the practical list-depth ceiling.
triage.count : (List Int) ->{} Int
triage.count(items) = list.length(items)

-- A control for separating worker overhead from the domain computation.
triage.ready : () ->{} ()
triage.ready() = ()
