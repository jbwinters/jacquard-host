import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, rm, writeFile, readFile, readdir, cp, appendFile } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { Runtime, Real, Some, BindingError, InvalidArgument, PreparationError, DeadlineExceeded, WorkerLost, OperationFailed, HostFailure } from '../dist/index.js';
import { stringify } from '../dist/protocol.js';
const binary = process.env.JACQUARD_BIN;
const prelude = process.env.JACQUARD_PRELUDE;
if (!binary || !prelude) throw new Error('live tests require JACQUARD_BIN and JACQUARD_PRELUDE');
await mkdir('../.scratch', { recursive: true });
const root = await mkdtemp(resolve('../.scratch/typescript-sdk-'));
const runtime = new Runtime({ binary, prelude });
const prepare = (project, instance = runtime) => instance.prepare(resolve(`../examples/${project}/rules.jac`), { cacheDir: join(root, 'cache') });
const quick = await prepare('quickstart');
const triage = await prepare('triage');
const planner = await prepare('planner');
const fulfillment = await prepare('fulfillment');
test.after(async () => { await rm(root, { recursive: true, force: true }); });
const data = triage.data;
const Policy = data.record('TriagePolicy', { page: data.int, stale: data.int });
const Incident = data.record('Incident', { id: data.text, service: data.text, severity: data.int, age: data.int, visible: data.bool, owner: data.option(data.text) });
const urgency = data.enumeration({ PAGE: 'PageNow', TODAY: 'WorkToday', WATCH: 'WatchOnly' });
const Routed = data.record('Routed', { id: data.text, team: data.text, urgency, reasons: data.list(data.text) });
const InvalidIncident = data.record('InvalidIncident', { id: data.text });
const report = data.union(Routed, InvalidIncident);
const route = triage.function('triage.batch', { parameters: [Policy, data.list(Incident)], result: data.list(report) });
const policy = Policy.create({ page: 4, stale: 60 });
const incident = (id, fields = {}) => Incident.create({ id, service: 'api', severity: 5, age: 90, visible: true, owner: null, ...fields });

test('quickstart native numbers, typed calls, inspectable raw results, and saved frames', async () => {
  const shipping = quick.function('shipping-cost', { parameters: [quick.data.int], result: quick.data.int });
  assert.equal(await shipping.call(35), 5); assert.equal(await shipping.call(75), 0);
  const invocation = await shipping.invoke(35); assert.equal(invocation.unwrap(), 5);
  assert.equal(invocation.render(), 'shipping-cost: ok');
  await invocation.save(join(root, 'inspection.json'));
  const saved = JSON.parse(await readFile(join(root, 'inspection.json'), 'utf8'));
  assert.equal(saved.schema, 'jacquard-host-inspection-v1'); assert.equal(stringify(saved.core_frames), stringify(invocation.raw.coreFrames));
});
test('typed records, enum cases, options, and 64 rows in explicit pure batches', async () => {
  const rows = Array.from({ length: 64 }, (_, i) => incident(String(i), { severity: i % 5 + 1, age: i * 3, visible: i % 2 === 0, owner: i % 3 ? 'ops' : null }));
  const result = await route.mapBatches([policy, rows]);
  assert.equal(result.length, rows.length);
  for (let i = 0; i < rows.length; i++) {
    const row = rows[i]; const page = row.visible && row.severity >= 4;
    assert.deepEqual(result[i], Routed.create({ id: row.id, team: row.owner ?? 'api-oncall', urgency: page ? 'PAGE' : row.age >= 60 || row.severity === 5 ? 'TODAY' : 'WATCH', reasons: [...(row.owner === null ? ['unowned'] : []), ...(row.age >= 60 ? ['ageing'] : []), ...(page ? ['customer-impact'] : [])] }));
  }
  assert.deepEqual(await route.mapBatches([policy, []]), []);
});
test('late invalid fields and nesting are refused before any batch or worker starts', async () => {
  const original = triage.host.invoke; let calls = 0;
  triage.host.invoke = async () => { calls++; throw new Error('must not spawn'); };
  try {
    await assert.rejects(route.mapBatches([policy, [...Array.from({ length: 16 }, (_, i) => incident(String(i))), incident('bad', { severity: true })]]), error => error instanceof InvalidArgument && error.message.includes('[0].severity'));
    await assert.rejects(route.call(policy, Array.from({ length: 64 }, (_, i) => incident(String(i)))), InvalidArgument);
    assert.equal(calls, 0);
  } finally { triage.host.invoke = original; }
});
test('missing later handlers refuse binding before earlier side effects and pure calls receive no services', async () => {
  let calls = 0;
  const inventory = { bookingStatus() { calls++; }, lookupStock() {}, reserveOrder() {} };
  assert.throws(() => fulfillment.function('fulfillment.place', { effects: { Inventory: inventory } }), BindingError);
  assert.equal(calls, 0);
  fulfillment.provide({ Inventory: inventory });
  assert.equal(await fulfillment.function('order.valid-lines').call([], []), true);
});
test('async services preserve method context; unknown completion occurs once and has raw evidence', async () => {
  let calls = 0;
  const warehouse = {
    prefix: 'receipt',
    async bookingStatus() { return fulfillment.data.make('BookingPending'); },
    async lookupStock() { return fulfillment.data.make('Stock', 20); },
    async reserveOrder() { calls++; assert.equal(this.prefix, 'receipt'); return fulfillment.data.make('Booked', 'receipt-1'); },
    async publishBooking() { throw new Error('synthetic after commit'); },
  };
  const place = fulfillment.function('fulfillment.place', { effects: { Inventory: warehouse, Dispatch: warehouse } });
  await assert.rejects(place.call(fulfillment.data.make('Order', 'one', [fulfillment.data.make('Line', 'sensor', 2)])), error => error instanceof OperationFailed && error.code === 'E1612' && error.result.hostFrames.some(frame => frame.kind === 'effect_ok'));
  assert.equal(calls, 1);
});
test('planner matches independent exhaustive choices and returned assignments are feasible', async () => {
  const d = planner.data;
  const Job = d.record('Job', { id: d.text, effort: d.int, value: d.int, latest: d.int });
  const Assignment = d.record('Assignment', { id: d.text, slot: d.int });
  const Plan = d.record('Plan', { score: d.int, assignments: d.list(Assignment) });
  const Planned = d.record('Planned', { plan: Plan });
  const InvalidPlan = d.record('InvalidPlan', { reason: d.text });
  const plan = planner.function('planner.plan', { parameters: [d.list(Job), d.list(d.int)], result: d.union(Planned, InvalidPlan) });
  const optimum = (jobs, caps, i = 0) => {
    if (i === jobs.length) return 0;
    let best = optimum(jobs, caps, i + 1); const job = jobs[i];
    for (let slot = 0; slot < Math.min(job.latest, caps.length); slot++) if (caps[slot] >= job.effort) { const next = [...caps]; next[slot] -= job.effort; best = Math.max(best, job.value + optimum(jobs, next, i + 1)); }
    return best;
  };
  for (let seed = 0; seed < 25; seed++) {
    const jobs = Array.from({ length: seed % 6 + 1 }, (_, i) => Job.create({ id: `job-${i}`, effort: (seed + i * 3) % 4 + 1, value: (seed * 7 + i * 11) % 15, latest: (seed + i) % 3 + 1 }));
    const caps = [seed % 5 + 1, seed % 3 + 2, 3].slice(0, seed % 3 + 1);
    const result = await plan.call(jobs, caps); assert.equal(result.$tag, 'Planned'); assert.equal(result.plan.score, optimum(jobs, caps));
    const seen = new Set(); const loads = caps.map(() => 0); let score = 0;
    for (const assignment of result.plan.assignments) { const job = jobs.find(item => item.id === assignment.id); assert.ok(!seen.has(job.id)); seen.add(job.id); assert.ok(assignment.slot >= 1 && assignment.slot <= Math.min(job.latest, caps.length)); loads[assignment.slot - 1] += job.effort; score += job.value; }
    assert.equal(score, result.plan.score); assert.ok(loads.every((load, i) => load <= caps[i]));
  }
});
test('cache warm reuse avoids Core commands, source changes invalidate, corruption fails closed', async () => {
  const original = runtime.command; runtime.command = async () => { throw new Error('unexpected Core command'); };
  try { const repeated = await prepare('quickstart'); assert.equal(repeated.directory, quick.directory); }
  finally { runtime.command = original; }
  const source = join(root, 'changed.jac'); await writeFile(source, (await readFile(resolve('../examples/quickstart/rules.jac'), 'utf8')).replaceAll('\n', '\r\n') + '\r\n-- changed\r\n');
  const changed = await runtime.prepare(source, { cacheDir: join(root, 'cache') });
  assert.notEqual(changed.directory, quick.directory); assert.ok((await readFile(join(changed.directory, 'source.jac'))).includes(Buffer.from('\r\n')));
  await writeFile(join(changed.directory, 'store', 'unexpected'), 'corrupt');
  await assert.rejects(runtime.prepare(source, { cacheDir: join(root, 'cache') }), PreparationError);
});
test('full Int range, fractional and integer-valued Reals, and nested options remain distinct', async () => {
  const source = join(root, 'values.jac');
  await writeFile(source, 'value.int : (Int) ->{} Int\nvalue.int(x) = x\nvalue.real : (Real) ->{} Real\nvalue.real(x) = x\nvalue.option : (Option (Option Int)) ->{} Option (Option Int)\nvalue.option(x) = x\n');
  const p = await runtime.prepare(source, { cacheDir: join(root, 'cache') });
  const integer = p.function('value.int', { parameters: [p.data.bigint], result: p.data.bigint });
  for (const value of [-(1n << 62n), (1n << 62n) - 1n]) assert.equal(await integer.call(value), value);
  await assert.rejects(p.function('value.int').call(Number.MAX_SAFE_INTEGER + 1), InvalidArgument);
  const real = p.function('value.real', { parameters: [p.data.real], result: p.data.real });
  for (const value of [1, -0, Infinity, NaN]) assert.ok(Object.is(await real.call(value), value));
  const option = p.function('value.option');
  assert.equal(await option.call(null), null); assert.deepEqual(await option.call(new Some(null)), new Some(null)); assert.equal(await option.call(new Some(new Some(3))), 3);
});
test('spawn failures, deadlines, and aborts share the auditable failure family', async () => {
  const original = quick.host.options.command;
  quick.host.options.command = ['/no/such/jacquard'];
  try { await assert.rejects(quick.function('shipping-cost').call(10), error => error instanceof WorkerLost && error.result.hostEvidence.reason === 'worker_spawn_failed'); }
  finally { quick.host.options.command = original; }
  const controller = new AbortController(); controller.abort();
  const result = await quick.function('shipping-cost').invokeWith({ signal: controller.signal }, 10);
  assert.equal(result.raw.hostEvidence.reason, 'aborted');
});
test('output overflow returns E1602 once without adaptive batching', async () => {
  const original = triage.host.invoke; let calls = 0;
  triage.host.invoke = async (...args) => { calls++; return original.apply(triage.host, args); };
  try {
    const rows = Array.from({ length: 29 }, (_, i) => incident(String(i)));
    await assert.rejects(route.mapBatches([policy, rows], { batchSize: 29 }), error => error.code === 'E1602' && error.batchIndex === 0 && error.completedValues.length === 0);
    assert.equal(calls, 1);
  } finally { triage.host.invoke = original; }
});
test('result mapping failures retain the successful Core result without a second invocation', async () => {
  const wrong = quick.function('shipping-cost', { parameters: [quick.data.int], result: quick.data.text });
  const invocation = await wrong.invoke(35);
  assert.equal(invocation.ok, true);
  assert.throws(() => invocation.unwrap(), error => error instanceof BindingError && error.result === invocation.raw && error.result.ok);
});
test('prelude metadata participates in the cache key and concurrent preparation publishes one complete artifact', async () => {
  const copy = join(root, 'prelude'); await cp(prelude, copy, { recursive: true });
  const copiedRuntime = new Runtime({ binary, prelude: copy });
  const before = await prepare('quickstart', copiedRuntime); assert.equal(before.directory, quick.directory);
  await appendFile(join(copy, 'LICENSE.md'), '\n');
  const after = await prepare('quickstart', copiedRuntime); assert.notEqual(after.directory, before.directory);
  const source = join(root, 'concurrent.jac'); await writeFile(source, 'shipping-cost : (Int) ->{} Int\nshipping-cost(total) = if lt(total, 50) then 5 else 0\n-- concurrent\n');
  const both = await Promise.all([runtime.prepare(source, { cacheDir: join(root, 'cache') }), runtime.prepare(source, { cacheDir: join(root, 'cache') })]);
  assert.equal(both[0].directory, both[1].directory);
  assert.equal(await both[1].function('shipping-cost').call(35), 5);
  assert.ok(!(await readdir(join(root, 'cache'))).some(name => name.startsWith('prepare-')));
});
