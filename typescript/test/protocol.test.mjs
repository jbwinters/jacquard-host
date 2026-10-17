import test from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import { loadKit, replayOne } from '../dist/kit.js';
import { decodeObject, frameBytes, stringify, Hash, Real, Tuple, Constructor, Budget, decodeValue, encodeValue, decodeType, HARD_LIMITS, validateLimits, HostSession, tupleType, invokeFrame } from '../dist/protocol.js';

const code = expected => error => error.code === expected;
test('strict JSON rejects duplicates, invalid Unicode, noninteger numbers, and trailing data', () => {
  for (const text of ['{"a":1,"a":2}', '{"x":{"a":1,"a":2}}', '{"x":1.0}', '{"x":1e0}', '{"x":NaN}', '{"x":Infinity}', '{"x":"\\ud800"}', '{"x":"\\udc00"}', '{}{}', '[]', '{"x":4611686018427387904}', '{"x":01}', '{"x":true,}', '{"x":[1,]}']) {
    assert.throws(() => decodeObject(Buffer.from(text)), code('E1601'), text);
  }
  assert.throws(() => decodeObject(Buffer.from([0x7b, 0x22, 0xff, 0x22, 0x3a, 0x30, 0x7d])), code('E1601'));
  assert.equal(decodeObject(Buffer.from('{"a":4611686018427387903,"b":-4611686018427387904}')).a, (1n << 62n) - 1n);
  assert.equal(stringify(decodeObject(Buffer.from('{"__proto__":{"x":1},"a":"😀"}'))), '{"__proto__":{"x":1},"a":"😀"}');
  assert.equal({}.x, undefined);
});
test('depth, frame bytes, and exact positive limits are enforced', () => {
  assert.throws(() => decodeObject(Buffer.from('{"a":[[]]}'), 2), code('E1602'));
  assert.throws(() => frameBytes({ a: 'xx' }, 4), code('E1602'));
  assert.throws(() => validateLimits({ ...HARD_LIMITS, max_arguments: 0 }), code('E1602'));
  assert.throws(() => validateLimits({ ...HARD_LIMITS, extra: 1 }), code('E1601'));
  assert.throws(() => validateLimits({ ...HARD_LIMITS, max_arguments: true }), code('E1601'));
});
test('all v0 values roundtrip exactly, including the complete Int range and Real NaN bits', () => {
  for (const value of [0n, -(1n << 62n), (1n << 62n) - 1n, 'hé😀', new Hash('a'.repeat(64)), new Tuple(3n, 'x'), new Constructor('b'.repeat(64), 3n), new Real('7ff0000000000001'), Real.from(-0), Real.from(Infinity)]) {
    assert.equal(stringify(encodeValue(decodeValue(encodeValue(value)))), stringify(encodeValue(value)));
  }
  for (const value of ['-0', '00', '+1', '4611686018427387904', '-4611686018427387905']) assert.throws(() => decodeValue({ kind: 'int', value }), code('E1601'));
  assert.throws(() => decodeValue({ kind: 'secret', value: 'x' }), code('E1604'));
  assert.throws(() => encodeValue(true), code('E1604'));
  assert.throws(() => decodeType({ kind: 'nominal', identity: 'A'.repeat(64), arguments: [] }), code('E1601'));
  assert.throws(() => decodeValue({ kind: 'tuple', items: [{ kind: 'int', value: '1' }] }, new Budget({ ...HARD_LIMITS, max_value_nodes: 1 })), code('E1602'));
});
test('session enforces one invocation, exact ordinals, and local terminal refusal', async () => {
  const kit = await loadKit(); const session = new HostSession();
  session.receive(kit.transcripts[0].core_output[0]); session.select();
  session.invoke(kit.manifest.identities['kit-ready'], { parameters: [], effects: [], result: tupleType() }, []);
  assert.throws(() => session.invoke('a'.repeat(64), { parameters: [], effects: [], result: tupleType() }, []), code('E1608'));
  session.receive(kit.transcripts[0].core_output[1]);
  assert.throws(() => session.shutdown(), code('E1608'));
  assert.throws(() => session.receive(kit.transcripts[0].core_output[1]), code('E1608'));
  const withExtra = { ...kit.transcripts[0].core_output[0], extra: 1 };
  assert.throws(() => new HostSession().receive(withExtra), code('E1601'));
});
test('request identity types, ordinal order, and one response slot fail with the frozen codes', async () => {
  const kit = await loadKit(); const transcript = kit.transcripts.find(item => item.name === 'one-once-effect-success');
  const session = new HostSession(); session.receive(transcript.core_output[0]); session.select();
  const input = transcript.host_input.find(step => step.frame?.kind === 'invoke').frame;
  const { decodeType } = await import('../dist/protocol.js');
  session.invoke(input.target.callable, { parameters: input.interface.parameters.map(item => decodeType(item)), result: decodeType(input.interface.result), effects: input.interface.effects }, ['ping'], input.capabilities.operations.map(({effect, operation}) => ({effect, operation})));
  const request = transcript.core_output[1];
  assert.throws(() => session.receive({ ...request, invocation_id: 0 }), code('E1601'));
  assert.throws(() => session.receive({ ...request, invocation_id: 'ffffffffffffffff' }), code('E1608'));
  assert.throws(() => session.receive({ ...request, request_id: '0000000000000002' }), code('E1608'));
  session.receive(request);
  assert.throws(() => session.receive(request), code('E1608'));
  assert.throws(() => session.respondFailure('outside_failure', 'unknown', ''), code('E1601'));
  session.respondOk('pong');
  assert.throws(() => session.respondOk('again'), code('E1608'));
});
test('all 21 pinned recordings replay through the independent Node carrier', async () => {
  const kit = await loadKit();
  for (const transcript of kit.transcripts) {
    const result = await replayOne(transcript, 'unused', { command: [process.execPath, fileURLToPath(new URL('./replay-worker.mjs', import.meta.url))], env: { ...process.env, TRANSCRIPTS: join(kit.directory, 'kit/transcripts.json'), TRANSCRIPT_CASE: transcript.name } });
    assert.deepEqual(result.differences, [], transcript.name);
    assert.deepEqual(result.proseDrift, [], transcript.name);
  }
});
test('replayer reports diagnostic prose drift while retaining the original Core text', async () => {
  const kit = await loadKit(); const original = kit.transcripts.find(item => item.name === 'refused-authority');
  const modified = structuredClone(original); modified.core_output.at(-1).result.diagnostics[0].summary += ' changed';
  const result = await replayOne(modified, 'unused', { command: [process.execPath, fileURLToPath(new URL('./replay-worker.mjs', import.meta.url))], env: { ...process.env, TRANSCRIPTS: join(kit.directory, 'kit/transcripts.json'), TRANSCRIPT_CASE: original.name } });
  assert.deepEqual(result.differences, []); assert.equal(result.proseDrift.length, 1);
  assert.equal(result.coreFrames.at(-1).result.diagnostics[0].summary, original.core_output.at(-1).result.diagnostics[0].summary);
});
