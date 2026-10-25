/** Frozen v0 values, envelopes, and a carrier-independent host state machine. */
import { CARRIER, PROTOCOL } from './compat.js';
import { INT_MAX, INT_MIN, ProtocolError, array, exceeded, exact, malformed, object, order, scalar, string, stringify, type Json, type JsonObject } from './json.js';
export { ProtocolError, decodeObject, frameBytes, stringify } from './json.js';
export type { Json, JsonObject } from './json.js';
export const HARD_LIMITS = Object.freeze({
  max_arguments: 64, max_collection_items: 1024, max_diagnostic_bytes: 65536,
  max_diagnostics: 32, max_effect_requests: 1024, max_effects: 64, max_frame_bytes: 1048576,
  max_host_message_bytes: 4096, max_json_depth: 64, max_operations: 256,
  max_stderr_bytes: 65536, max_text_bytes: 262144, max_value_nodes: 4096,
});
export type Limits = { -readonly [K in keyof typeof HARD_LIMITS]: number };
export function validateLimits(value: unknown): Limits {
  const fields = exact(value, Object.keys(HARD_LIMITS));
  for (const key of Object.keys(HARD_LIMITS) as (keyof Limits)[]) {
    if (typeof fields[key] !== 'number' || !Number.isInteger(fields[key])) malformed(`limit ${key} must be an integer`);
    if (Number(fields[key]) < 1 || Number(fields[key]) > HARD_LIMITS[key]) exceeded(`limit ${key} exceeds its bounds`);
  }
  return { ...fields } as Limits;
}
export class Hash {
  readonly hex: string;
  constructor(hex: string) { this.hex = hash(hex); Object.freeze(this); }
  toString(): string { return this.hex; }
}
export function hash(value: unknown): string {
  if (typeof value !== 'string' || !/^[0-9a-f]{64}$/.test(value)) malformed('HASH_V0 must be exactly 64 lowercase hexadecimal digits');
  return value as string;
}
/** Real preserves all binary64 bits, including NaN payloads and negative zero. */
export class Real {
  constructor(public readonly bits: string) {
    if (!/^[0-9a-f]{16}$/.test(bits)) malformed('Real must have exactly 16 lowercase hexadecimal digits');
    Object.freeze(this);
  }
  static from(value: number): Real { const bytes = Buffer.alloc(8); bytes.writeDoubleBE(value); return new Real(bytes.toString('hex')); }
  get number(): number { return Buffer.from(this.bits, 'hex').readDoubleBE(); }
}
export class Tuple {
  readonly items: readonly Value[];
  constructor(...items: Value[]) { this.items = Object.freeze([...items]); Object.freeze(this); }
}
export class Constructor {
  readonly identity: string;
  readonly arguments: readonly Value[];
  constructor(identity: string | Hash, ...args: Value[]) {
    this.identity = hash(String(identity)); this.arguments = Object.freeze([...args]); Object.freeze(this);
  }
}
export type Value = bigint | string | Hash | Real | Tuple | Constructor;
export type BoundaryType = { kind: 'nominal'; identity: string; arguments: BoundaryType[] } | { kind: 'tuple'; items: BoundaryType[] };
export const nominal = (identity: string | Hash, ...args: BoundaryType[]): BoundaryType => ({ kind: 'nominal', identity: hash(String(identity)), arguments: args });
export const tupleType = (...items: BoundaryType[]): BoundaryType => ({ kind: 'tuple', items });
export interface Interface { parameters: BoundaryType[]; result: BoundaryType; effects: string[] }
export interface OperationEntry { effect: string; operation: string }
export class Budget {
  nodes = 0;
  constructor(readonly limits: Limits = HARD_LIMITS) {}
  consume(): void { if (++this.nodes > this.limits.max_value_nodes) exceeded('frame exceeds max_value_nodes'); }
  count(count: number, args = false): void {
    if (count > this.limits.max_collection_items || (args && count > this.limits.max_arguments)) exceeded('collection exceeds selected limits');
  }
  text(text: string): void { scalar(text); if (Buffer.byteLength(text) > this.limits.max_text_bytes) exceeded('Text exceeds max_text_bytes'); }
}
export function decodeType(value: Json, budget = new Budget()): BoundaryType {
  const obj = object(value); budget.consume();
  if (obj.kind === 'nominal') {
    exact(obj, ['arguments', 'identity', 'kind']);
    const args = array(obj.arguments); budget.count(args.length);
    return nominal(hash(obj.identity), ...args.map(item => decodeType(item, budget)));
  }
  if (obj.kind === 'tuple') {
    exact(obj, ['items', 'kind']); const items = array(obj.items); budget.count(items.length);
    return tupleType(...items.map(item => decodeType(item, budget)));
  }
  throw new ProtocolError('E1604', 'unsupported boundary type kind');
}
export function encodeType(type: BoundaryType, budget = new Budget(), depth = 1): JsonObject {
  if (depth > budget.limits.max_json_depth) exceeded('type nesting exceeds max_json_depth');
  budget.consume();
  if (type.kind === 'nominal') {
    budget.count(type.arguments.length);
    return { kind: 'nominal', identity: hash(type.identity), arguments: type.arguments.map(item => encodeType(item, budget, depth + 2)) };
  }
  if (type.kind === 'tuple') {
    budget.count(type.items.length); return { kind: 'tuple', items: type.items.map(item => encodeType(item, budget, depth + 2)) };
  }
  throw new ProtocolError('E1604', 'unsupported boundary type');
}
export function decodeValue(value: Json, budget = new Budget()): Value {
  const obj = object(value); budget.consume();
  switch (obj.kind) {
    case 'int': {
      exact(obj, ['kind', 'value']); const text = string(obj.value);
      if (!/^(?:0|-[1-9][0-9]*|[1-9][0-9]*)$/.test(text) || text.length > 20) malformed('Int must use canonical 63-bit decimal text');
      const integer = BigInt(text); if (integer < INT_MIN || integer > INT_MAX) malformed('Int is outside the 63-bit range');
      return integer;
    }
    case 'real': exact(obj, ['bits', 'kind']); return new Real(string(obj.bits));
    case 'text': { exact(obj, ['kind', 'value']); const text = string(obj.value); budget.text(text); return text; }
    case 'hash': exact(obj, ['kind', 'value']); return new Hash(hash(obj.value));
    case 'tuple': {
      exact(obj, ['items', 'kind']); const items = array(obj.items); budget.count(items.length);
      return new Tuple(...items.map(item => decodeValue(item, budget)));
    }
    case 'constructor': {
      exact(obj, ['arguments', 'identity', 'kind']); const args = array(obj.arguments); budget.count(args.length, true);
      return new Constructor(hash(obj.identity), ...args.map(item => decodeValue(item, budget)));
    }
    default: throw new ProtocolError('E1604', 'unsupported boundary value kind');
  }
}
export function encodeValue(value: Value, budget = new Budget(), depth = 1): JsonObject {
  if (depth > budget.limits.max_json_depth) exceeded('value nesting exceeds max_json_depth');
  budget.consume();
  if (typeof value === 'bigint') {
    if (value < INT_MIN || value > INT_MAX) throw new ProtocolError('E1604', 'Int is outside the 63-bit range');
    return { kind: 'int', value: String(value) };
  }
  if (typeof value === 'string') { budget.text(value); return { kind: 'text', value }; }
  if (value instanceof Real) return { kind: 'real', bits: value.bits };
  if (value instanceof Hash) return { kind: 'hash', value: value.hex };
  if (value instanceof Tuple) {
    budget.count(value.items.length); return { kind: 'tuple', items: value.items.map(item => encodeValue(item, budget, depth + 2)) };
  }
  if (value instanceof Constructor) {
    budget.count(value.arguments.length, true);
    return { kind: 'constructor', identity: value.identity, arguments: value.arguments.map(item => encodeValue(item, budget, depth + 2)) };
  }
  throw new ProtocolError('E1604', 'value has no v0 encoding');
}
export const INVOCATION_ID = '0000000000000000';
export const FAILURE_PAIRS = Object.freeze([
  'unsupported_operation/not_started', 'refused_authority/not_started', 'outside_failure/failed', 'completion_unknown/unknown',
]);
export type FailureCategory = 'unsupported_operation' | 'refused_authority' | 'outside_failure' | 'completion_unknown';
export type Completion = 'not_started' | 'failed' | 'unknown';
export type CancelReason = 'cancelled' | 'timeout' | 'host_shutdown';
export const TERMINALS = ['ok', 'diagnostic', 'cancelled', 'host_failure', 'completion_unknown'];
export interface Diagnostic {
  schema: string; domain: string; code: string | null; severity: string;
  summary: string; cause: string; next_step: string; span: Json;
  contrast?: Json;
}
export interface EffectRequest { kind: 'effect_request'; ordinal: number; effect: string; operation: string; arguments: Value[]; raw: JsonObject }
export interface Outcome { kind: 'outcome'; ok: boolean; value?: Value; diagnostics: Diagnostic[]; terminal: string; evidence: JsonObject; raw: JsonObject }
export interface Fatal { kind: 'fatal'; diagnostics: Diagnostic[]; raw: JsonObject }
export type Terminal = Outcome | Fatal | { kind: 'shutdown_ack'; raw: JsonObject };
export type CoreFrame = EffectRequest | Terminal | { kind: 'core_hello'; raw: JsonObject };
function stringsBytes(value: Json): number {
  if (typeof value === 'string') return Buffer.byteLength(value);
  if (value && typeof value === 'object') return Object.values(value).reduce<number>((sum, item) => sum + stringsBytes(item), 0);
  return 0;
}
function diagnostics(value: Json, limits: Limits): Diagnostic[] {
  const entries = array(value);
  if (!entries.length) malformed('diagnostics must be nonempty');
  if (entries.length > limits.max_diagnostics || stringsBytes(value) > limits.max_diagnostic_bytes) exceeded('diagnostics exceed selected limits');
  return entries.map(entry => {
    const obj = object(entry);
    exact(obj, ['cause', 'code', 'domain', 'next_step', 'schema', 'severity', 'span', 'summary', ...(Object.hasOwn(obj, 'contrast') ? ['contrast'] : [])]);
    if (obj.schema !== 'jacquard-diagnostic-v1') malformed('unknown diagnostic schema');
    if (obj.code !== null) string(obj.code);
    for (const key of ['cause', 'domain', 'next_step', 'severity', 'summary']) string(obj[key]);
    if (obj.span !== null) object(obj.span);
    if (obj.contrast !== undefined && obj.contrast !== null) {
      const contrast = exact(obj.contrast, ['intended', 'mistaken']); string(contrast.intended); string(contrast.mistaken);
    }
    return obj as unknown as Diagnostic;
  });
}
function sortedHashes(value: Json, limit: number): string[] {
  const values = array(value).map(hash);
  if (values.length > limit) exceeded('effect set exceeds selected limits');
  if (values.some((item, i) => i > 0 && item <= values[i - 1])) malformed('effects must be a strictly sorted set');
  return values;
}
export function invokeFrame(target: string | Hash, spec: Interface, args: readonly Value[], operations: readonly OperationEntry[] = [], limits: Limits = HARD_LIMITS): JsonObject {
  if (args.length !== spec.parameters.length) malformed('argument count differs from parameter count');
  const budget = new Budget(limits); budget.count(args.length, true);
  sortedHashes(spec.effects, limits.max_effects);
  if (operations.length > limits.max_operations) exceeded('registry exceeds max_operations');
  const entries = [...operations].sort((a, b) => {
    const left = a.effect + a.operation; const right = b.effect + b.operation;
    return left < right ? -1 : left > right ? 1 : 0;
  });
  for (let i = 0; i < entries.length; i++) {
    const entry = entries[i]; hash(entry.effect); hash(entry.operation);
    if (!spec.effects.includes(entry.effect)) malformed('operation effect is outside the interface row');
    if (i && entries[i - 1].effect === entry.effect && entries[i - 1].operation === entry.operation) malformed('duplicate operation entry');
  }
  return {
    arguments: args.map(value => encodeValue(value, budget)),
    capabilities: { effects: spec.effects, operations: entries.map(entry => ({ ...entry, mode: 'once' })) },
    interface: { effects: spec.effects, parameters: spec.parameters.map(type => encodeType(type, budget)), result: encodeType(spec.result, budget) },
    invocation_id: INVOCATION_ID, kind: 'invoke', protocol: PROTOCOL,
    target: { callable: hash(String(target)), kind: 'store-term-v0' },
  };
}
function validateEvidence(value: Json, limits: Limits, budget: Budget): JsonObject {
  const evidence = exact(value, ['core', 'host_observations']);
  const core = exact(evidence.core, ['capabilities', 'effect_requests', 'interface', 'invocation_id', 'schema', 'target', 'terminal']);
  if (core.schema !== 'jacquard-host-core-evidence-v0' || !TERMINALS.includes(String(core.terminal))) malformed('unknown core evidence schema or terminal');
  if (core.invocation_id !== INVOCATION_ID) order('wrong evidence invocation');
  const target = exact(core.target, ['callable', 'kind']); hash(target.callable);
  if (target.kind !== 'store-term-v0') malformed('unknown target kind');
  const spec = exact(core.interface, ['effects', 'parameters', 'result']);
  sortedHashes(spec.effects, limits.max_effects);
  const parameters = array(spec.parameters); budget.count(parameters.length, true);
  parameters.forEach(item => decodeType(item, budget)); decodeType(spec.result, budget);
  const capabilities = exact(core.capabilities, ['effects', 'operations']); sortedHashes(capabilities.effects, limits.max_effects);
  const operations = array(capabilities.operations); if (operations.length > limits.max_operations) exceeded('too many evidence operations');
  for (const operation of operations) {
    const entry = exact(operation, ['effect', 'mode', 'operation']); hash(entry.effect); hash(entry.operation);
    if (entry.mode !== 'once') malformed('unknown operation mode');
  }
  const requests = array(core.effect_requests);
  if (requests.length > limits.max_effect_requests) exceeded('too many evidence requests');
  requests.forEach((request, index) => {
    const entry = exact(request, ['effect', 'operation', 'ordinal']); hash(entry.effect); hash(entry.operation);
    if (entry.ordinal !== index + 1) malformed('evidence ordinals must be consecutive');
  });
  const observations = exact(evidence.host_observations, ['responses', 'schema']);
  if (observations.schema !== 'jacquard-host-observations-v0') malformed('unknown host observations schema');
  for (const observation of array(observations.responses)) {
    const entry = exact(observation, ['category', 'completion', 'ordinal']);
    if (!['ok', 'unsupported_operation', 'refused_authority', 'outside_failure', 'completion_unknown', 'cancelled', 'timeout', 'host_shutdown'].includes(String(entry.category))) malformed('unknown observation category');
    if (!['completed', 'not_started', 'failed', 'unknown'].includes(String(entry.completion))) malformed('unknown observation completion');
    if (typeof entry.ordinal !== 'number' || !Number.isInteger(entry.ordinal) || entry.ordinal < 1 || entry.ordinal > requests.length) malformed('invalid observation ordinal');
  }
  return evidence;
}
export function parseCoreFrame(frame: JsonObject, limits: Limits = HARD_LIMITS): Exclude<CoreFrame, {kind: 'core_hello'}> {
  if (frame.kind === 'core_hello') order('core_hello may only be the first frame');
  if (frame.protocol !== PROTOCOL) malformed('unknown protocol');
  if (frame.kind === 'fatal') { exact(frame, ['diagnostics', 'kind', 'protocol']); return { kind: 'fatal', diagnostics: diagnostics(frame.diagnostics, limits), raw: frame }; }
  if (frame.kind === 'shutdown_ack') { exact(frame, ['kind', 'protocol']); return { kind: 'shutdown_ack', raw: frame }; }
  if (frame.kind !== 'effect_request' && frame.kind !== 'outcome') malformed('unknown Core frame kind');
  exact(frame, frame.kind === 'effect_request' ? ['arguments', 'effect', 'invocation_id', 'kind', 'mode', 'operation', 'protocol', 'request_id'] : ['evidence', 'invocation_id', 'kind', 'protocol', 'result']);
  string(frame.invocation_id);
  if (frame.invocation_id !== INVOCATION_ID) order('frame names the wrong invocation');
  if (frame.kind === 'effect_request') {
    exact(frame, ['arguments', 'effect', 'invocation_id', 'kind', 'mode', 'operation', 'protocol', 'request_id']);
    if (frame.mode !== 'once') malformed('unknown operation mode');
    if (!/^[0-9a-f]{16}$/.test(string(frame.request_id))) malformed('request_id must have 16 lowercase hexadecimal digits');
    const ordinal = BigInt('0x' + String(frame.request_id));
    if (ordinal < 1n) order('request ordinals start at one');
    if (ordinal > BigInt(Number.MAX_SAFE_INTEGER)) order('request ordinal is not consecutive');
    const budget = new Budget(limits); const args = array(frame.arguments); budget.count(args.length, true);
    return { kind: 'effect_request', ordinal: Number(ordinal), effect: hash(frame.effect), operation: hash(frame.operation), arguments: args.map(item => decodeValue(item, budget)), raw: frame };
  }
  exact(frame, ['evidence', 'invocation_id', 'kind', 'protocol', 'result']);
  // Core encodes evidence types and the result value against one frame budget.
  const budget = new Budget(limits);
  const evidence = validateEvidence(frame.evidence, limits, budget); const terminal = String(object(evidence.core).terminal);
  const result = object(frame.result);
  if (result.kind === 'ok') {
    exact(result, ['kind', 'value']); if (terminal !== 'ok') malformed('ok result has a non-ok terminal');
    return { kind: 'outcome', ok: true, value: decodeValue(result.value, budget), terminal, evidence, diagnostics: [], raw: frame };
  }
  if (result.kind !== 'error') malformed('unknown result kind');
  exact(result, ['diagnostics', 'kind']); if (terminal === 'ok') malformed('error result has an ok terminal');
  return { kind: 'outcome', ok: false, diagnostics: diagnostics(result.diagnostics, limits), terminal, evidence, raw: frame };
}
export type State = 'hello' | 'negotiating' | 'selected' | 'shutting_down' | 'running' | 'waiting' | 'finishing' | 'closed';
export class HostSession {
  state: State = 'hello';
  limits: Limits = { ...HARD_LIMITS };
  readonly requests: EffectRequest[] = [];
  readonly responses: JsonObject[] = [];
  terminal?: Terminal;
  pending?: EffectRequest;
  receive(frame: JsonObject): CoreFrame {
    if (this.state === 'closed') order('Core sent a frame after its terminal');
    if (this.state === 'hello') {
      if (frame.kind !== 'core_hello') order('first Core frame must be core_hello');
      exact(frame, ['carrier', 'kind', 'limits', 'versions']);
      if (frame.carrier !== CARRIER) malformed('unknown carrier');
      const versions = array(frame.versions).map(string);
      if (!versions.length) malformed('versions must be nonempty');
      if (stringify(validateLimits(frame.limits)) !== stringify(HARD_LIMITS)) malformed('hello must advertise exactly the frozen limits');
      if (!versions.includes(PROTOCOL)) throw new ProtocolError('E1600', 'Core does not offer jacquard-host-v0');
      this.state = 'negotiating'; return { kind: 'core_hello', raw: frame };
    }
    const parsed = parseCoreFrame(frame, this.limits);
    if (parsed.kind === 'effect_request') {
      if (this.state !== 'running') order('effect request without an available invocation slot');
      if (parsed.ordinal !== this.requests.length + 1) order('request ordinal is not consecutive');
      if (parsed.ordinal > this.limits.max_effect_requests) exceeded('Core exceeded max_effect_requests');
      this.requests.push(parsed); this.pending = parsed; this.state = 'waiting'; return parsed;
    }
    if (parsed.kind === 'outcome' && !['running', 'waiting', 'finishing'].includes(this.state)) order('outcome without a live invocation');
    if (parsed.kind === 'fatal' && !['negotiating', 'selected', 'shutting_down', 'running', 'finishing'].includes(this.state)) order('fatal while a response is outstanding');
    if (parsed.kind === 'shutdown_ack' && this.state !== 'shutting_down') order('shutdown_ack without shutdown');
    this.pending = undefined; this.terminal = parsed; this.state = 'closed'; return parsed;
  }
  private require(...states: State[]): void { if (!states.includes(this.state)) order(`message is not permitted in state ${this.state}`); }
  select(limits: Limits = HARD_LIMITS): JsonObject {
    this.require('negotiating'); this.limits = validateLimits(limits); this.state = 'selected';
    return { kind: 'host_select', limits: this.limits, protocol: PROTOCOL };
  }
  invoke(target: string | Hash, spec: Interface, args: readonly Value[], operations: readonly OperationEntry[] = []): JsonObject {
    this.require('selected'); const frame = invokeFrame(target, spec, args, operations, this.limits); this.state = 'running'; return frame;
  }
  shutdown(): JsonObject { this.require('selected'); this.state = 'shutting_down'; return { kind: 'shutdown', protocol: PROTOCOL }; }
  private respond(fields: JsonObject, terminal: boolean): JsonObject {
    const frame = { ...fields, protocol: PROTOCOL, invocation_id: INVOCATION_ID, request_id: this.pending!.ordinal.toString(16).padStart(16, '0') };
    this.responses.push(frame); this.pending = undefined; this.state = terminal ? 'finishing' : 'running'; return frame;
  }
  respondOk(value: Value): JsonObject {
    this.require('waiting'); return this.respond({ kind: 'effect_ok', value: encodeValue(value, new Budget(this.limits)) }, false);
  }
  respondFailure(category: FailureCategory, completion: Completion, message: string): JsonObject {
    this.require('waiting');
    if (!FAILURE_PAIRS.includes(`${category}/${completion}`)) malformed('invalid effect_failure pair');
    scalar(message); if (Buffer.byteLength(message) > this.limits.max_host_message_bytes) exceeded('host message exceeds max_host_message_bytes');
    return this.respond({ kind: 'effect_failure', category, completion, message }, true);
  }
  cancel(reason: CancelReason, completion: Completion): JsonObject {
    this.require('waiting');
    if (!['cancelled', 'timeout', 'host_shutdown'].includes(reason) || !['not_started', 'failed', 'unknown'].includes(completion)) malformed('unknown cancellation enum');
    return this.respond({ kind: 'cancel', reason, completion }, true);
  }
}
