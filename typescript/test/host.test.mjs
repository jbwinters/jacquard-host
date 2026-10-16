import test from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import { Host } from '../dist/host.js';
import { tupleType, nominal } from '../dist/protocol.js';
import { loadKit } from '../dist/kit.js';
const kit = await loadKit(); const ids = kit.manifest.identities;
const spec = { parameters: [], result: tupleType(), effects: [] };
const options = mode => ({ command: [process.execPath, fileURLToPath(new URL('./fault-worker.mjs', import.meta.url))], deadlineMs: 300,
  env: { ...process.env, MODE: mode, TRANSCRIPTS: join(kit.directory, 'kit/transcripts.json') } });

test('malformed, extra, and noncanonical Core frames fail closed', async () => {
  for (const [mode, expected] of [['unknown-field', 'E1601'], ['invalid-hash', 'E1601'], ['extra', 'E1608']]) {
    const result = await new Host('unused', options(mode)).invoke(ids['kit-ready'], spec);
    assert.equal(result.kind, 'protocol_error', mode); assert.equal(result.code, expected, mode);
  }
});
test('EOF, partial prefix, silent worker, and post-terminal stall retain host-owned failure evidence', async () => {
  for (const mode of ['exit', 'truncated', 'silent', 'stall', 'nonzero']) {
    const result = await new Host('unused', options(mode)).invoke(ids['kit-ready'], spec);
    assert.equal(result.kind, 'carrier_loss', mode); assert.equal(result.code, 'E1611', mode);
    assert.equal(result.hostEvidence.schema, 'jacquard-host-typescript-evidence-v0');
    if (mode === 'stall' || mode === 'nonzero') assert.ok(result.coreFrames.some(frame => frame.kind === 'outcome'));
  }
});
test('stderr is drained without blocking and retention obeys the negotiated ceiling', async () => {
  const result = await new Host('unused', { ...options('stderr'), deadlineMs: 3000, limits: { max_stderr_bytes: 17 } }).invoke(ids['kit-ready'], spec);
  assert.ok(result.ok, result.message); assert.equal(result.stderr.length, 17);
});
test('backpressure on a large invoke spends the same bounded worker lifetime', async () => {
  const start = performance.now();
  const result = await new Host('unused', options('no-read')).invoke(ids['kit-ready'], { parameters: [nominal(ids.text)], result: nominal(ids.text), effects: [] }, ['x'.repeat(220000)]);
  assert.equal(result.hostEvidence.reason, 'deadline'); assert.ok(performance.now() - start < 2000);
});
test('local frame refusal starts no worker and invalid configuration is rejected', async () => {
  const result = await new Host('unused', { command: ['/no/such/executable'], limits: { max_frame_bytes: 20 } }).invoke(ids['kit-ready'], spec);
  assert.equal(result.kind, 'host_error'); assert.equal(result.code, 'E1602'); assert.equal(result.exitStatus, null);
  for (const deadlineMs of [0, -1, NaN, Infinity, 2147483648]) assert.throws(() => new Host('unused', { deadlineMs }));
});
