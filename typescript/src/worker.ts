import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { decodeObject, exceeded, frameBytes, type JsonObject, ProtocolError } from './json.js';
import { HARD_LIMITS, type Limits } from './protocol.js';

export class CarrierLoss extends Error {}
export class WorkerTimeout extends CarrierLoss {}
export interface WorkerOptions { command?: readonly string[]; env?: NodeJS.ProcessEnv; deadlineMs?: number; signal?: AbortSignal }
/** One bounded child lifetime. Pipes are drained even while a handler awaits. */
export class Worker {
  readonly child: ChildProcessWithoutNullStreams;
  readonly controller = new AbortController();
  readonly closed: Promise<void>;
  private input = Buffer.alloc(0);
  private stderrChunks: Buffer[] = [];
  private stderrLength = 0;
  private stderrLimit = HARD_LIMITS.max_stderr_bytes as number;
  private ended = false;
  private fault?: Error;
  private listeners = new Set<() => void>();
  private timer: NodeJS.Timeout;
  private readonly expiresAt: number;
  private externalAbort?: () => void;
  exitStatus: number | null = null;
  signal: NodeJS.Signals | null = null;
  spawnError?: Error;
  constructor(store: string, readonly options: WorkerOptions = {}) {
    const deadline = options.deadlineMs ?? 30000;
    if (!Number.isFinite(deadline) || deadline < 1 || deadline > 2147483647) throw new RangeError('deadlineMs must be a finite positive timer duration');
    this.expiresAt = performance.now() + deadline;
    const command = options.command ?? [process.env.JACQUARD_BIN ?? 'jacquard'];
    if (!command.length || command.some(item => typeof item !== 'string' || !item)) throw new TypeError('command must be a nonempty executable argument array');
    this.child = spawn(command[0], [...command.slice(1), 'host', 'worker', '--store', store], { env: options.env ?? process.env, stdio: 'pipe', shell: false });
    this.closed = new Promise(resolve => {
      this.child.once('close', (code, signal) => { this.exitStatus = code; this.signal = signal; this.ended = true; this.notify(); resolve(); });
    });
    this.child.once('error', error => { this.spawnError = error; this.fail(new CarrierLoss(`Could not start ${command[0]} (${(error as NodeJS.ErrnoException).code ?? error.name}); check the configured Core installation`)); });
    this.child.stdout.on('data', (chunk: Buffer) => {
      if (this.fault) return;
      if (this.input.length + chunk.length > 2 * (HARD_LIMITS.max_frame_bytes + 4)) { this.fail(new ProtocolError('E1602', 'worker stdout exceeded bounded buffering')); return; }
      this.input = Buffer.concat([this.input, chunk]); this.notify();
    });
    this.child.stdout.once('end', () => { this.ended = true; this.notify(); });
    this.child.stdout.on('error', error => this.fail(new CarrierLoss(`stdout failed (${error.name})`)));
    // A write callback reports EPIPE. Keep stdout readable for Core's terminal.
    this.child.stdin.on('error', () => {});
    this.child.stderr.on('data', (chunk: Buffer) => {
      const bytes = chunk.subarray(0, Math.max(0, this.stderrLimit - this.stderrLength));
      if (bytes.length) { this.stderrChunks.push(Buffer.from(bytes)); this.stderrLength += bytes.length; }
    });
    this.child.stderr.on('error', () => {});
    this.timer = setTimeout(() => this.fail(new WorkerTimeout('worker lifetime deadline exceeded')), deadline);
    if (options.signal) {
      this.externalAbort = () => this.fail(new CarrierLoss('invocation aborted; outside completion is unknown'));
      options.signal.addEventListener('abort', this.externalAbort, { once: true });
      if (options.signal.aborted) this.externalAbort();
    }
  }
  private notify(): void { for (const listener of [...this.listeners]) listener(); }
  private checkDeadline(): void {
    if (!this.fault && performance.now() >= this.expiresAt) this.fail(new WorkerTimeout('worker lifetime deadline exceeded'));
  }
  private fail(error: Error): void {
    this.fault ??= error; this.controller.abort(this.fault); this.child.kill('SIGKILL'); this.notify();
  }
  setStderrLimit(limit: number): void {
    this.stderrLimit = limit;
    const kept = this.stderr.subarray(0, limit); this.stderrChunks = kept.length ? [kept] : []; this.stderrLength = kept.length;
  }
  get stderr(): Buffer { return Buffer.concat(this.stderrChunks, this.stderrLength); }
  guard<T>(promise: PromiseLike<T>): Promise<T> {
    this.checkDeadline();
    return new Promise<T>((resolve, reject) => {
      const aborted = () => { if (this.fault) { this.listeners.delete(aborted); reject(this.fault); } };
      this.listeners.add(aborted);
      Promise.resolve(promise).then(value => { this.checkDeadline(); this.listeners.delete(aborted); if (this.fault) reject(this.fault); else resolve(value); }, error => { this.listeners.delete(aborted); reject(error); });
      aborted();
    });
  }
  private async readExact(size: number, eofAllowed = false): Promise<Buffer | null> {
    this.checkDeadline();
    while (this.input.length < size) {
      if (this.fault) throw this.fault;
      if (this.ended) {
        if (eofAllowed && this.input.length === 0) return null;
        throw new CarrierLoss('worker closed stdout during a frame');
      }
      await this.guard(new Promise<void>(resolve => {
        const changed = () => { this.listeners.delete(changed); resolve(); };
        this.listeners.add(changed);
      }));
    }
    if (this.fault) throw this.fault;
    const value = this.input.subarray(0, size); this.input = this.input.subarray(size); return value;
  }
  async readFrame(limits: Limits = HARD_LIMITS): Promise<JsonObject | null> {
    const prefix = await this.readExact(4, true); if (!prefix) return null;
    const length = prefix.readUInt32BE();
    if (!length || length > limits.max_frame_bytes) exceeded('frame length exceeds max_frame_bytes');
    return decodeObject((await this.readExact(length))!, limits.max_json_depth);
  }
  async writeBytes(bytes: Uint8Array): Promise<void> {
    this.checkDeadline();
    if (this.fault) throw this.fault;
    await this.guard(new Promise<void>((resolve, reject) => {
      this.child.stdin.write(bytes, error => error ? reject(new CarrierLoss(`worker write failed (${error.name})`)) : resolve());
    }));
  }
  async writeFrame(frame: JsonObject, limits: Limits = HARD_LIMITS): Promise<void> { await this.writeBytes(frameBytes(frame, limits.max_frame_bytes, limits.max_json_depth)); }
  closeInput(): void { this.child.stdin.end(); }
  async wait(): Promise<void> { await this.guard(this.closed); }
  async dispose(): Promise<void> {
    clearTimeout(this.timer);
    if (this.externalAbort) this.options.signal!.removeEventListener('abort', this.externalAbort);
    this.closeInput();
    if (this.child.exitCode === null && this.child.signalCode === null) this.child.kill('SIGKILL');
    this.child.stdout.destroy(); this.child.stdin.destroy(); this.child.stderr.destroy();
    await this.closed;
    this.listeners.clear();
  }
}
