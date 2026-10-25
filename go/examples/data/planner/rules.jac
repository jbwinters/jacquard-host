app.any(items, predicate) = match items {
  | Nil -> False
  | Cons(item, rest) -> if predicate(item) then True else app.any(rest, predicate)
}
app.empty?(items) = match items { | Nil -> True | Cons(_, _) -> False }

-- Exhaustive, bounded work allocation. No host operation is needed.
type Job = | Job Text Int Int Int
-- id, effort, value, latest slot (one-based)
type Assignment = | Assignment Text Int
type Plan = | Plan Int (List Assignment)
type Planning = | Planned Plan | InvalidPlan Text

planner.consume(caps, index, effort) = match caps {
  | Nil -> []
  | Cons(cap, rest) -> if eq(index, 1) then Cons(sub(cap, effort), rest)
                        else Cons(cap, planner.consume(rest, sub(index, 1), effort))
}

planner.score(candidate) = match candidate { | Plan(score, _) -> score }
planner.better(left, right) = if lt(planner.score(left), planner.score(right)) then right else left

planner.try-slots(task, rest, all-caps, caps, index) = match (task, caps) {
  | (_, Nil) -> planner.search(rest, all-caps)
  | (Job(id, effort, value, latest), Cons(capacity, tail)) -> {
    let other = planner.try-slots(task, rest, all-caps, tail, add(index, 1))
    if bool.or(lt(capacity, effort), lt(latest, index)) then other
    else {
      let updated = planner.consume(all-caps, index, effort)
      let candidate = match planner.search(rest, updated) {
        | Plan(score, assignments) -> Plan(add(value, score), Cons(Assignment(id, index), assignments))
      }
      planner.better(other, candidate)
    }
  }
}

planner.search(jobs, caps) = match jobs {
  | Nil -> Plan(0, [])
  | Cons(task, rest) -> planner.try-slots(task, rest, caps, caps, 1)
}

planner.valid-jobs(jobs, seen) = match jobs {
  | Nil -> True
  | Cons(Job(id, effort, value, latest), rest) ->
    if bool.or(text.empty?(id), bool.or(lt(effort, 1), bool.or(lt(value, 0), lt(latest, 1)))) then False
    else if app.any(seen, fn (old) -> text.eq?(id, old)) then False
    else planner.valid-jobs(rest, Cons(id, seen))
}

planner.plan : (List Job, List Int) ->{} Planning
planner.plan(jobs, caps) =
  if bool.or(lt(6, list.length(jobs)), bool.or(app.empty?(caps), lt(3, list.length(caps))))
  then InvalidPlan("at most six jobs and one to three slots")
  else if bool.or(bool.not(planner.valid-jobs(jobs, [])), app.any(caps, fn (n) -> lt(n, 0)))
  then InvalidPlan("invalid job or capacity")
  else Planned(planner.search(jobs, caps))
