import { createHash } from 'node:crypto';
import { createReadStream, constants } from 'node:fs';
import { access, mkdir, mkdtemp, readFile, readdir, realpath, rename, rm, writeFile } from 'node:fs/promises';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { delimiter, extname, join, resolve } from 'node:path';
import { CORE_VERSION, PROTOCOL } from './compat.js';
import { Data, type Codec } from './data.js';
import { BindingError, InvalidArgument, JacquardError, PreparationError } from './errors.js';
import { Host, HostFailure, OperationRegistry, type HostOptions, type InvocationResult } from './host.js';
import { checkGroups, hashGroups, normal, parseSignature, SignatureError, type Group } from './identities.js';
import { ProtocolError, frameBytes, stringify, type Json, type JsonObject } from './json.js';
import { HARD_LIMITS, invokeFrame, type Interface, type Limits, type Value, validateLimits } from './protocol.js';

const execute = promisify(execFile);
const SCHEMA = 'jacquard-typescript-program-v1';
const digest = (bytes: Uint8Array | string) => createHash('sha256').update(bytes).digest('hex');
async function fileDigest(path: string): Promise<string> {
  const hash = createHash('sha256'); for await (const chunk of createReadStream(path)) hash.update(chunk as Buffer); return hash.digest('hex');
}
async function files(root: string, prefix = ''): Promise<Record<string, string>> {
  const result: Record<string, string> = Object.create(null);
  for (const entry of (await readdir(join(root, prefix), { withFileTypes: true })).sort((a, b) => a.name.localeCompare(b.name))) {
    const name = prefix ? `${prefix}/${entry.name}` : entry.name;
    if (entry.isDirectory()) Object.assign(result, await files(root, name));
    else if (entry.isFile()) result[name] = await fileDigest(join(root, name));
    else throw new PreparationError(`unsupported filesystem entry in prepared artifacts: ${name}`);
  }
  return result;
}
async function executable(binary: string): Promise<string> {
  const paths = binary.includes('/') || binary.includes('\\') ? [resolve(binary)] : (process.env.PATH ?? '').split(delimiter).map(directory => resolve(directory, binary));
  for (const path of paths) { try { await access(path, constants.X_OK); return await realpath(path); } catch { /* next PATH candidate */ } }
  throw new PreparationError(`Cannot find Core executable ${binary}; configure JACQUARD_BIN using the setup guide`);
}
interface Metadata {
  identity: JsonObject; names: [string, string][]; constructors: [string, string][];
  signatures: Record<string, string>; groups: Group[]; files: Record<string, string>;
}
export interface RuntimeOptions { binary?: string; prelude?: string; deadlineMs?: number; limits?: Partial<Limits> }
export interface PrepareOptions { cacheDir?: string }
export class Runtime {
  readonly binary: string;
  readonly prelude: string;
  readonly deadlineMs: number;
  readonly limits: Limits;
  private checked = new Set<string>();
  private preludeGroups = new Map<string, Promise<Group[]>>();
  constructor(options: RuntimeOptions = {}) {
    this.binary = options.binary ?? process.env.JACQUARD_BIN ?? 'jacquard';
    const prelude = options.prelude ?? process.env.JACQUARD_PRELUDE;
    if (!prelude) throw new BindingError('Set JACQUARD_PRELUDE or pass prelude to Runtime; Core and prelude must match the compatibility pin');
    this.prelude = resolve(prelude); this.deadlineMs = options.deadlineMs ?? 30000;
    if (!Number.isFinite(this.deadlineMs) || this.deadlineMs < 1 || this.deadlineMs > 2147483647) throw new BindingError('deadlineMs must be a finite positive timer duration');
    try { this.limits = validateLimits({ ...HARD_LIMITS, ...options.limits }); }
    catch (error) { throw new BindingError(`Invalid runtime limits: ${error instanceof Error ? error.message : error}`); }
  }
  private async command(binary: string, args: string[]): Promise<string> {
    try {
      const result = await execute(binary, args, { env: { ...process.env, JACQUARD_PRELUDE: this.prelude }, timeout: 30000, killSignal: 'SIGKILL', maxBuffer: 8 * 1024 * 1024, encoding: 'utf8' });
      return result.stdout;
    } catch (error) {
      const detail = error instanceof Error && 'stderr' in error ? String(error.stderr).slice(0, 4096) : error instanceof Error ? error.message : 'command failed';
      throw new PreparationError(`Core ${args[0]} failed: ${detail}`);
    }
  }
  async prepare(source: string | readonly string[], options: PrepareOptions = {}): Promise<Program> {
    try { return await this.prepareProgram(source, resolve(options.cacheDir ?? '.scratch/jacquard-host-ts')); }
    catch (error) {
      if (error instanceof JacquardError) throw error;
      throw new PreparationError(`Could not prepare program: ${error instanceof Error ? error.message : error}`, { cause: error });
    }
  }
  private async prepareProgram(source: string | readonly string[], cache: string): Promise<Program> {
    const paths = typeof source === 'string' ? [source] : [...source];
    if (!paths.length || !['.jac', '.jqd'].includes(extname(paths[0])) || paths.some(path => extname(path) !== extname(paths[0]))) throw new PreparationError('Pass .jac files or .jqd files in dependency order');
    const binary = await executable(this.binary); const binaryHash = await fileDigest(binary);
    if (!this.checked.has(binaryHash)) {
      const version = (await this.command(binary, ['--version'])).trim();
      if (version !== CORE_VERSION) throw new PreparationError(`Expected pinned Core version ${CORE_VERSION}, received ${version}; the exact commit is also required`);
      this.checked.add(binaryHash);
    }
    const preludeHash = digest(stringify(await files(this.prelude)));
    const buffers = await Promise.all(paths.map(path => readFile(path)));
    const content = Buffer.concat(buffers.flatMap((bytes, index) => index ? [Buffer.from('\n'), bytes] : [bytes]));
    const identity: JsonObject = { schema: SCHEMA, protocol: PROTOCOL, source: digest(content), binary: binaryHash, prelude: preludeHash, syntax: extname(paths[0]) };
    const directory = join(cache, digest(stringify(identity)));
    await mkdir(cache, { recursive: true });
    try { await access(directory); return await this.load(directory, identity, binary); }
    catch (error) { if (!(error && typeof error === 'object' && 'code' in error && error.code === 'ENOENT')) throw error; }
    const staging = await mkdtemp(join(cache, 'prepare-'));
    try {
      const snapshot = join(staging, `source${extname(paths[0])}`); await writeFile(snapshot, content);
      const signatures: Record<string, string> = Object.create(null);
      for (const line of (await this.command(binary, ['check', snapshot, '--print-sigs'])).split(/\r?\n/)) {
        const match = /^(\S+) : (.+)$/.exec(line.trim()); if (match) signatures[match[1]] = match[2];
      }
      const preludeKey = binaryHash + preludeHash;
      let discovery = this.preludeGroups.get(preludeKey);
      if (!discovery) {
        discovery = (async () => {
          const groups: Group[] = [];
          for (const name of ['01-prim.jqd', '02-data.jqd', '19-audit.jqd']) groups.push(...hashGroups(await this.command(binary, ['hash', join(this.prelude, name)])));
          return groups;
        })();
        this.preludeGroups.set(preludeKey, discovery);
        void discovery.catch(() => this.preludeGroups.delete(preludeKey));
      }
      const groups = [...await discovery, ...hashGroups(await this.command(binary, ['hash', snapshot]))];
      checkGroups(groups);
      await this.command(binary, ['run', snapshot, '--store', join(staging, 'store')]);
      const names = new Map<string, string>(); const constructors = new Map<string, string>();
      for (const group of groups) for (const [name, id] of group) { if (!names.has(name)) names.set(name, id); constructors.set(name, id); }
      const metadata: Metadata = { identity, names: [...names], constructors: [...constructors], signatures, groups, files: await files(staging) };
      await writeFile(join(staging, 'program.json'), JSON.stringify(metadata));
      try { await rename(staging, directory); }
      catch (error) { if (!(error && typeof error === 'object' && 'code' in error && ['EEXIST', 'ENOTEMPTY'].includes(String(error.code)))) throw error; }
      return await this.load(directory, identity, binary);
    } finally { await rm(staging, { recursive: true, force: true }); }
  }
  private async load(directory: string, identity: JsonObject, binary: string): Promise<Program> {
    try {
      const metadata = JSON.parse(await readFile(join(directory, 'program.json'), 'utf8')) as Metadata;
      const observed = await files(directory); delete observed['program.json'];
      if (stringify(metadata.identity) !== stringify(identity) || stringify(observed) !== stringify(metadata.files)) throw new Error('cached artifact digests do not match');
      checkGroups(metadata.groups); return new Program(this, directory, metadata, binary);
    } catch (error) {
      if (error instanceof BindingError) throw error;
      throw new PreparationError(`Prepared cache ${directory} is incomplete or changed; remove that cache entry and prepare again`, { cause: error });
    }
  }
}
export interface ServiceMethod {
  handler: (...args: unknown[]) => unknown;
  parameters?: readonly Codec<unknown>[];
  result?: Codec<unknown>;
  /** Append a HandlerContext with an AbortSignal to the handler's arguments. */
  context?: boolean;
}
export interface FunctionOptions<A extends readonly unknown[], R> {
  effects?: Record<string, object>;
  parameters?: { readonly [K in keyof A]: Codec<A[K]> };
  result?: Codec<R>;
}
export class Program {
  readonly data: Data;
  readonly host: Host;
  private interfaces = new Map<string, Interface>();
  private effects = new Map<string, { identity: string; operations: Group }>();
  private services = new Map<string, object>();
  constructor(readonly runtime: Runtime, readonly directory: string, readonly metadata: Metadata, binary: string) {
    this.data = new Data(new Map(metadata.names), new Map(metadata.constructors), runtime.limits);
    this.host = new Host(join(directory, 'store'), { command: [binary], env: { ...process.env, JACQUARD_PRELUDE: runtime.prelude }, deadlineMs: runtime.deadlineMs, limits: runtime.limits });
    for (const [name, signature] of Object.entries(metadata.signatures)) {
      try { this.interfaces.set(name, parseSignature(signature, this.data.names)); }
      catch (error) { if (!(error instanceof SignatureError)) throw error; }
    }
    const effects = new Set([...this.interfaces.values()].flatMap(spec => spec.effects));
    for (const group of metadata.groups) for (const [name, id] of group) if (effects.has(id)) this.effects.set(name, { identity: id, operations: group.filter(([, hash]) => hash !== id) });
  }
  provide(services: Record<string, object>): this {
    for (const [name, service] of Object.entries(services)) {
      if (!this.effects.has(normal(name))) throw new BindingError(`unknown effect ${name}`);
      this.services.set(normal(name), service);
    }
    return this;
  }
  function<const A extends readonly unknown[] = unknown[], R = unknown>(name: string, options: FunctionOptions<A, R> = {}): BoundFunction<A, R> {
    const spec = this.interfaces.get(name); if (!spec) throw new BindingError(`${name}: absent or unsupported closed v0 signature`);
    if (options.parameters && options.parameters.length !== spec.parameters.length) throw new BindingError(`${name}: parameter codec count differs from checked signature`);
    const selected = new Map([...this.effects].filter(([, effect]) => spec.effects.includes(effect.identity)));
    if (new Set([...selected.values()].map(effect => effect.identity)).size !== spec.effects.length) throw new BindingError(`${name}: effect metadata is unavailable; use the low-level Host API`);
    const services = new Map(this.services);
    for (const [effect, service] of Object.entries(options.effects ?? {})) {
      if (!selected.has(normal(effect))) throw new BindingError(`${name} does not use ${effect}`);
      services.set(normal(effect), service);
    }
    const registry = new OperationRegistry(); const missing: string[] = [];
    for (const [effectName, effect] of selected) {
      const service = services.get(effectName);
      for (const [operation, id] of effect.operations) {
        const candidates = [operation, operation.replace(/[.-]([a-z])/g, (_, letter: string) => letter.toUpperCase()), operation.replace(/[.-]/g, '_')];
        const entry = candidates.map(key => service && Reflect.get(service, key)).find(value => typeof value === 'function' || (value && typeof value === 'object' && typeof value.handler === 'function')) as ((...args: unknown[]) => unknown) | ServiceMethod | undefined;
        if (!entry) { missing.push(`${effectName}.${candidates[1]}`); continue; }
        const descriptor: ServiceMethod = typeof entry === 'function' ? { handler: entry } : entry;
        registry.bind(effect.identity, id, async (args, context) => {
          if (descriptor.parameters && descriptor.parameters.length !== args.length) throw new HostFailure('outside_failure', 'failed', 'handler parameter codec count differs from request');
          const native = args.map((value, index) => descriptor.parameters?.[index] ? descriptor.parameters[index].decode(value) : this.data.decode(value));
          const answer = await descriptor.handler.apply(service, descriptor.context ? [...native, context] : native);
          try { return descriptor.result ? descriptor.result.encode(answer, 'handler result') : this.data.encode(answer, undefined, 'handler result'); }
          catch (error) {
            if (error instanceof InvalidArgument || error instanceof ProtocolError) throw new HostFailure('outside_failure', 'failed', 'handler result cannot be represented');
            throw error;
          }
        });
      }
    }
    if (missing.length) throw new BindingError(`${name}: missing handlers: ${missing.join(', ')}. Bind every operation in each declared effect before calling`);
    return new BoundFunction(this, name, spec, registry, options);
  }
}
export class Invocation<R = unknown> {
  private decoded = false;
  private decodedValue?: R;
  constructor(readonly raw: InvocationResult, readonly function_: BoundFunction<readonly unknown[], R>) {}
  get ok(): boolean { return this.raw.ok; }
  get code(): string | null { return this.raw.code; }
  get message(): string { return this.raw.message; }
  get value(): R | undefined {
    if (!this.raw.ok) return undefined;
    if (!this.decoded) {
      try {
        this.decodedValue = (this.function_.options.result ? this.function_.options.result.decode(this.raw.value!) : this.function_.program.data.decode(this.raw.value!, this.function_.signature.result)) as R;
        this.decoded = true;
      } catch (error) {
        const failure = new BindingError(`${this.function_.name}: Core completed, but result mapping failed: ${error instanceof Error ? error.message : error}`);
        failure.result = this.raw; throw failure;
      }
    }
    return this.decodedValue;
  }
  unwrap(): R { this.raw.unwrap(); return this.value!; }
  render(): string {
    const labels = this.function_.program.data.labels;
    return [`${this.function_.name}: ${this.ok ? 'ok' : this.message}`, ...this.raw.requests.map(request => `  ${request.ordinal}. ${labels.get(request.operation) ?? request.operation}`)].join('\n');
  }
  async save(path: string): Promise<void> {
    const record: JsonObject = { schema: 'jacquard-host-inspection-v1', function: this.function_.name, core_frames: this.raw.coreFrames, host_frames: this.raw.hostFrames,
      host: { evidence: this.raw.hostEvidence, exit_status: this.raw.exitStatus, signal: this.raw.signal, stderr_base64: this.raw.stderr.toString('base64') } };
    await writeFile(path, stringify(record, 128) + '\n');
  }
}
export class BoundFunction<A extends readonly unknown[] = unknown[], R = unknown> {
  constructor(readonly program: Program, readonly name: string, readonly signature: Interface, readonly registry: OperationRegistry, readonly options: FunctionOptions<A, R>) {}
  private arguments_(arguments_: A): Value[] {
    if (arguments_.length !== this.signature.parameters.length) throw new InvalidArgument(`${this.name}: expected ${this.signature.parameters.length} arguments, received ${arguments_.length}`);
    try {
      const values = arguments_.map((value, index) => {
        const path = `${this.name}.argument[${index}]`; const codec = this.options.parameters?.[index];
        return codec ? codec.encode(value, path) : this.program.data.encode(value, this.signature.parameters[index], path);
      });
      frameBytes(invokeFrame(this.program.data.names.get(this.name)!, this.signature, values, this.registry.entries(), this.program.host.limits), this.program.host.limits.max_frame_bytes, this.program.host.limits.max_json_depth);
      return values;
    } catch (error) {
      if (error instanceof ProtocolError) throw new InvalidArgument(`${this.name}: ${error.message}; reduce input size or use smaller independent batches`);
      throw error;
    }
  }
  async invoke(...args: A): Promise<Invocation<R>> { return this.invokeWith({}, ...args); }
  async invokeWith(options: { signal?: AbortSignal }, ...args: A): Promise<Invocation<R>> {
    const values = this.arguments_(args);
    const host = options.signal ? new Host(this.program.host.store, { ...this.program.host.options, signal: options.signal }) : this.program.host;
    const result = await host.invoke(this.program.data.names.get(this.name)!, this.signature, values, this.registry);
    return new Invocation(result, this as unknown as BoundFunction<readonly unknown[], R>);
  }
  async call(...args: A): Promise<R> { return (await this.invoke(...args)).unwrap(); }
  async mapBatches(args: A, options: { batchSize?: number } = {}): Promise<R extends readonly (infer T)[] ? T[] : unknown[]> {
    const size = options.batchSize ?? 8;
    if (!Number.isSafeInteger(size) || size < 1) throw new InvalidArgument('batchSize must be a positive integer');
    const last = this.signature.parameters.at(-1); const result = this.signature.result;
    if (this.signature.effects.length || last?.kind !== 'nominal' || last.identity !== this.program.data.names.get('list') || result.kind !== 'nominal' || result.identity !== this.program.data.names.get('list')) throw new BindingError('mapBatches needs a pure function with final List parameter and List result');
    const rows = args.at(-1); if (!Array.isArray(rows)) throw new InvalidArgument('mapBatches needs a final array argument');
    this.arguments_([...args.slice(0, -1), []] as unknown as A);
    const chunks: A[] = [];
    for (let i = 0; i < rows.length; i += size) chunks.push([...args.slice(0, -1), rows.slice(i, i + size)] as unknown as A);
    for (const chunk of chunks) this.arguments_(chunk);
    const values: unknown[] = [];
    for (const [index, chunk] of chunks.entries()) {
      const invocation = await this.invoke(...chunk);
      try {
        const batch = invocation.unwrap(); if (!Array.isArray(batch)) throw new BindingError('batch result mapping did not return an array');
        values.push(...batch);
      } catch (error) {
        if (error instanceof JacquardError) { error.batchIndex = index; error.completedValues = [...values]; }
        throw error;
      }
    }
    return values as R extends readonly (infer T)[] ? T[] : unknown[];
  }
}
