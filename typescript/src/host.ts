import { PROTOCOL } from './compat.js';
import { invocationError } from './errors.js';
import { ProtocolError, frameBytes, type JsonObject } from './json.js';
import { FAILURE_PAIRS, HARD_LIMITS, HostSession, hash, invokeFrame, type Completion, type Diagnostic, type EffectRequest, type FailureCategory, type Interface, type Limits, type OperationEntry, type Value, validateLimits, Budget, encodeValue } from './protocol.js';
import { CarrierLoss, Worker, WorkerTimeout, type WorkerOptions } from './worker.js';

export class HostFailure extends Error {
  constructor(readonly category: FailureCategory, readonly completion: Completion, message = '') {
    super(message); this.name = 'HostFailure';
    if (!FAILURE_PAIRS.includes(`${category}/${completion}`)) throw new TypeError('invalid effect_failure pair');
  }
}
export interface HandlerContext { readonly signal: AbortSignal }
export type Handler = (arguments_: readonly Value[], context: HandlerContext) => Value | PromiseLike<Value>;
export class OperationRegistry {
  private readonly handlers = new Map<string, Handler>();
  bind(effect: string, operation: string, handler: Handler): this {
    const key = `${hash(effect)}/${hash(operation)}`;
    if (this.handlers.has(key)) throw new TypeError('operation identity is already bound');
    if (typeof handler !== 'function') throw new TypeError('handler must be a function');
    this.handlers.set(key, handler); return this;
  }
  entries(): OperationEntry[] { return [...this.handlers.keys()].sort().map(key => { const [effect, operation] = key.split('/'); return { effect, operation }; }); }
  async dispatch(request: EffectRequest, context: HandlerContext): Promise<Value> {
    const handler = this.handlers.get(`${request.effect}/${request.operation}`);
    if (!handler) throw new HostFailure('unsupported_operation', 'not_started', 'operation is not configured');
    try { return await handler(request.arguments, context); }
    catch (error) {
      if (error instanceof HostFailure) throw error;
      throw new HostFailure('completion_unknown', 'unknown', `handler raised ${error instanceof Error ? error.name : 'an exception'}`);
    }
  }
}
export class InvocationResult {
  kind: 'ok' | 'error' | 'fatal' | 'shutdown_ack' | 'carrier_loss' | 'protocol_error' | 'host_error' = 'carrier_loss';
  value?: Value;
  diagnostics: Diagnostic[] = [];
  terminal: string | null = null;
  evidence: JsonObject | null = null;
  coreFrames: JsonObject[] = [];
  hostFrames: JsonObject[] = [];
  requests: EffectRequest[] = [];
  exitStatus: number | null = null;
  signal: NodeJS.Signals | null = null;
  stderr: Buffer = Buffer.alloc(0);
  hostEvidence: JsonObject = {};
  get ok(): boolean { return this.kind === 'ok'; }
  get primaryCode(): string | null { return this.diagnostics[0]?.code ?? null; }
  get code(): string | null { return this.primaryCode ?? (typeof this.hostEvidence.code === 'string' ? this.hostEvidence.code : null); }
  get message(): string {
    if (this.ok) return 'Jacquard invocation succeeded';
    const diagnostic = this.diagnostics[0];
    const detail = diagnostic ? `${diagnostic.summary} ${diagnostic.cause}` : String(this.hostEvidence.detail ?? this.hostEvidence.reason ?? this.kind);
    return `${this.code ?? this.kind}: ${detail}`;
  }
  unwrap(): Value { if (!this.ok) throw invocationError(this); return this.value!; }
}
export interface HostOptions extends WorkerOptions { limits?: Partial<Limits> }
function bounded(text: string, limit: number): string {
  const bytes = Buffer.from(text);
  let end = Math.min(bytes.length, limit);
  while (end) {
    try { return new TextDecoder('utf-8', { fatal: true }).decode(bytes.subarray(0, end)); }
    catch { end--; }
  }
  return '';
}
export class Host {
  readonly limits: Limits;
  constructor(readonly store: string, readonly options: HostOptions = {}) {
    this.limits = validateLimits({ ...HARD_LIMITS, ...options.limits });
    const deadline = options.deadlineMs ?? 30000;
    if (!Number.isFinite(deadline) || deadline < 1 || deadline > 2147483647) throw new RangeError('invalid deadlineMs');
  }
  async invoke(target: string, spec: Interface, args: readonly Value[] = [], registry = new OperationRegistry()): Promise<InvocationResult> {
    // Validate the complete host frame before starting any process or callback.
    const result = new InvocationResult();
    try { frameBytes(invokeFrame(target, spec, args, registry.entries(), this.limits), this.limits.max_frame_bytes, this.limits.max_json_depth); }
    catch (error) {
      if (!(error instanceof ProtocolError)) throw error;
      result.kind = 'host_error'; result.hostEvidence = { schema: 'jacquard-host-typescript-evidence-v0', reason: 'invoke_refused_locally', code: error.code, detail: error.message }; return result;
    }
    return this.run(session => session.invoke(target, spec, args, registry.entries()), registry);
  }
  async shutdownProbe(): Promise<InvocationResult> { return this.run(session => session.shutdown(), new OperationRegistry()); }
  private async run(first: (session: HostSession) => JsonObject, registry: OperationRegistry): Promise<InvocationResult> {
    const session = new HostSession(); const result = new InvocationResult();
    const worker = new Worker(this.store, this.options);
    let sending = false;
    const send = async (frame: JsonObject, limits = session.limits) => {
      sending = true; await worker.writeFrame(frame, limits); result.hostFrames.push(frame); sending = false;
    };
    try {
      const hello = await worker.readFrame();
      if (!hello) throw new CarrierLoss('worker exited before core_hello');
      result.coreFrames.push(hello); session.receive(hello);
      await send(session.select(this.limits), HARD_LIMITS); worker.setStderrLimit(this.limits.max_stderr_bytes);
      await send(first(session));
      while (session.state !== 'closed') {
        const frame = await worker.readFrame(session.limits);
        if (!frame) throw new CarrierLoss('worker exited before a terminal frame');
        result.coreFrames.push(frame); const parsed = session.receive(frame);
        if (parsed.kind === 'effect_request') {
          let response: JsonObject;
          try {
            const value = await worker.guard(registry.dispatch(parsed, { signal: worker.controller.signal }));
            try {
              encodeValue(value, new Budget(session.limits));
              frameBytes({ kind: 'effect_ok', invocation_id: '0000000000000000', protocol: PROTOCOL, request_id: parsed.ordinal.toString(16).padStart(16, '0'), value: encodeValue(value, new Budget(session.limits)) }, session.limits.max_frame_bytes, session.limits.max_json_depth);
            } catch (error) {
              if (!(error instanceof ProtocolError)) throw error;
              throw new HostFailure('outside_failure', 'failed', 'handler result has no encoding within the selected limits');
            }
            response = session.respondOk(value);
          } catch (error) {
            if (!(error instanceof HostFailure)) throw error;
            response = session.respondFailure(error.category, error.completion, bounded(error.message, session.limits.max_host_message_bytes));
          }
          await send(response);
        } else if (parsed.kind === 'outcome') {
          result.kind = parsed.ok ? 'ok' : 'error'; result.value = parsed.value;
          result.diagnostics = parsed.diagnostics; result.terminal = parsed.terminal; result.evidence = parsed.evidence;
        } else if (parsed.kind === 'fatal') { result.kind = 'fatal'; result.diagnostics = parsed.diagnostics; }
        else if (parsed.kind === 'shutdown_ack') result.kind = 'shutdown_ack';
      }
      worker.closeInput();
      const extra = await worker.readFrame(session.limits);
      if (extra) { result.coreFrames.push(extra); session.receive(extra); }
      await worker.wait();
      if (worker.exitStatus !== 0) throw new CarrierLoss('worker exited unsuccessfully after its terminal');
    } catch (error) {
      const protocol = error instanceof ProtocolError;
      result.kind = protocol ? (sending ? 'host_error' : 'protocol_error') : 'carrier_loss';
      result.hostEvidence = {
        schema: 'jacquard-host-typescript-evidence-v0', code: protocol ? error.code : 'E1611',
        reason: error instanceof WorkerTimeout ? 'deadline' : worker.spawnError ? 'worker_spawn_failed' : protocol ? sending ? 'host_frame_refused' : 'core_frame_refused' : this.options.signal?.aborted ? 'aborted' : 'carrier_loss',
        detail: error instanceof Error ? error.message : 'carrier failed', state: session.state,
        outstanding_request: session.pending?.ordinal ?? null,
      };
    } finally {
      await worker.dispose();
      result.requests = [...session.requests]; result.exitStatus = worker.exitStatus;
      result.signal = worker.signal; result.stderr = worker.stderr;
    }
    return result;
  }
}
