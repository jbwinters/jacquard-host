import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, rm } from 'node:fs/promises';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { resolve, join } from 'node:path';
import { Host, HostFailure, OperationRegistry } from '../dist/host.js';
import { nominal, tupleType, Tuple } from '../dist/protocol.js';
import { loadKit, replayKit } from '../dist/kit.js';
const binary = process.env.JACQUARD_BIN;
const prelude = process.env.JACQUARD_PRELUDE;
if (!binary || !prelude) throw new Error('live tests require JACQUARD_BIN and JACQUARD_PRELUDE');
const run = promisify(execFile);
const options = { command: [binary], env: { ...process.env, JACQUARD_PRELUDE: prelude } };
await mkdir('../.scratch', { recursive: true });
const root = await mkdtemp(resolve('../.scratch/typescript-host-'));
const store = join(root, 'store');
const kit = await loadKit();
const ids = kit.manifest.identities;
await run(binary, ['run', join(kit.directory, 'kit/fixtures.jac'), '--store', store], { env: options.env, timeout: 30000 });
const echo = { parameters: [nominal(ids.text)], result: nominal(ids.text), effects: [ids['kit-world']] };
test.after(async () => { await rm(root, { recursive: true, force: true }); });

test('real Core independently matches all 21 pinned recordings', async () => {
  for (const result of await replayKit(store, options, kit)) {
    assert.deepEqual(result.differences, [], result.name);
    assert.deepEqual(result.proseDrift, [], result.name);
  }
});
test('host invokes pure and effectful terms, retaining exact evidence', async () => {
  const host = new Host(store, options);
  const pure = await host.invoke(ids['kit-ready'], { parameters: [], result: tupleType(), effects: [] });
  assert.deepEqual(pure.unwrap(), new Tuple());
  assert.equal(pure.exitStatus, 0);
  let calls = 0;
  const registry = new OperationRegistry().bind(ids['kit-world'], ids.send, async ([text]) => { calls++; return `${text}!`; });
  const result = await host.invoke(ids['kit-echo'], echo, ['ping'], registry);
  assert.equal(result.unwrap(), 'ping!'); assert.equal(calls, 1);
  assert.equal(result.requests.length, 1); assert.equal(result.hostFrames[2].kind, 'effect_ok');
  assert.equal(result.evidence.core.terminal, 'ok');
  assert.equal((await host.shutdownProbe()).kind, 'shutdown_ack');
});
test('all four outside failure pairs and unexpected exceptions are never retried', async () => {
  for (const [category, completion, code] of [
    ['unsupported_operation', 'not_started', 'E1606'], ['refused_authority', 'not_started', 'E1607'],
    ['outside_failure', 'failed', 'E1613'], ['completion_unknown', 'unknown', 'E1612'],
  ]) {
    let calls = 0;
    const registry = new OperationRegistry().bind(ids['kit-world'], ids.send, () => { calls++; throw new HostFailure(category, completion, 'synthetic failure'); });
    const result = await new Host(store, options).invoke(ids['kit-echo'], echo, ['ping'], registry);
    assert.equal(result.code, code); assert.equal(calls, 1);
    assert.ok(result.diagnostics[0].cause.endsWith('synthetic failure'));
  }
  const registry = new OperationRegistry().bind(ids['kit-world'], ids.send, () => { throw new Error('private detail'); });
  const result = await new Host(store, options).invoke(ids['kit-echo'], echo, ['ping'], registry);
  assert.equal(result.code, 'E1612'); assert.ok(!result.message.includes('private detail'));
});
test('awaited handler deadline kills the worker without a late response or retry', async () => {
  let calls = 0; let context;
  const registry = new OperationRegistry().bind(ids['kit-world'], ids.send, (_args, received) => { calls++; context = received; return new Promise(() => {}); });
  const start = performance.now();
  const result = await new Host(store, { ...options, deadlineMs: 250 }).invoke(ids['kit-echo'], echo, ['ping'], registry);
  assert.equal(result.hostEvidence.reason, 'deadline'); assert.equal(result.code, 'E1611');
  assert.equal(calls, 1); assert.ok(context.signal.aborted);
  assert.equal(result.hostFrames.length, 2); assert.ok(performance.now() - start < 2000);
});
test('blocking JavaScript cannot be preempted but cannot send a response after its deadline', async () => {
  let calls = 0;
  const registry = new OperationRegistry().bind(ids['kit-world'], ids.send, () => {
    calls++; const until = performance.now() + 300;
    while (performance.now() < until) { /* synthetic CPU-bound handler */ }
    return 'late';
  });
  const result = await new Host(store, { ...options, deadlineMs: 250 }).invoke(ids['kit-echo'], echo, ['ping'], registry);
  assert.equal(calls, 1); assert.equal(result.hostEvidence.reason, 'deadline'); assert.equal(result.hostFrames.length, 2);
});
