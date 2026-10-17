import { BindingError, InvalidArgument } from './errors.js';
import { displayToStore } from './identities.js';
import { INT_MIN, INT_MAX, scalar } from './json.js';
import { Constructor, Hash, Real, Tuple, type BoundaryType, type Limits, type Value } from './protocol.js';

export interface Codec<T> {
  encode(value: T, path?: string, depth?: number): Value;
  decode(value: Value): T;
}
export type Infer<C> = C extends Codec<infer T> ? T : never;
export type Fields = Readonly<Record<string, Codec<unknown>>>;
export type FieldValues<F extends Fields> = { [K in keyof F]: Infer<F[K]> };
export type Tagged<Tag extends string, T> = { readonly $tag: Tag } & T;
export interface RecordCodec<Tag extends string, F extends Fields> extends Codec<Tagged<Tag, FieldValues<F>>> {
  readonly tag: Tag;
  readonly identity: string;
  create(fields: FieldValues<F>): Tagged<Tag, FieldValues<F>>;
}
export class Some<T = unknown> { constructor(readonly value: T) { Object.freeze(this); } }
export class Variant {
  readonly tag: string;
  readonly fields: readonly unknown[];
  constructor(tag: string, ...fields: unknown[]) { this.tag = displayToStore(tag); this.fields = Object.freeze([...fields]); Object.freeze(this); }
}
function invalid(path: string, message: string): never { throw new InvalidArgument(`${path}: ${message}`); }
function mapping(message: string): never { throw new BindingError(message); }
const builtins = new Set(['true', 'false', 'some', 'none', 'cons', 'nil']);

export class Data {
  readonly int: Codec<number>;
  readonly bigint: Codec<bigint>;
  readonly real: Codec<number>;
  readonly text: Codec<string>;
  readonly bool: Codec<boolean>;
  readonly hash: Codec<Hash>;
  readonly unit: Codec<void> = {
    encode(value, path = 'value') { if (value !== undefined) invalid(path, 'expected undefined for unit'); return new Tuple(); },
    decode(value) { if (!(value instanceof Tuple) || value.items.length) mapping('expected unit'); },
  };
  private readonly records = new Map<string, RecordCodec<string, Fields>>();
  private readonly recordTags = new Map<string, RecordCodec<string, Fields>>();
  readonly labels: ReadonlyMap<string, string>;
  constructor(readonly names: ReadonlyMap<string, string>, readonly constructors: ReadonlyMap<string, string>, readonly limits: Limits) {
    this.labels = new Map([...constructors].map(([name, id]) => [id, name]));
    this.bigint = {
      encode(value, path = 'value') { if (typeof value !== 'bigint' || value < INT_MIN || value > INT_MAX) invalid(path, 'expected a 63-bit bigint'); return value; },
      decode(value) { if (typeof value !== 'bigint') mapping('expected Int'); return value as bigint; },
    };
    this.int = {
      encode: (value, path = 'value') => { if (!Number.isSafeInteger(value)) invalid(path, 'expected a safe integer; use bigint for the full Int range'); return this.bigint.encode(BigInt(value), path); },
      decode: value => { const number = Number(this.bigint.decode(value)); if (!Number.isSafeInteger(number)) mapping('Int exceeds the safe number range; use the bigint codec'); return number; },
    };
    this.real = {
      encode(value, path = 'value') { if (typeof value !== 'number') invalid(path, 'expected a number'); return Real.from(value); },
      decode(value) { if (!(value instanceof Real)) mapping('expected Real'); return (value as Real).number; },
    };
    this.text = {
      encode: (value, path = 'value') => {
        if (typeof value !== 'string') invalid(path, 'expected text');
        try { scalar(value); } catch { invalid(path, 'text contains a lone surrogate'); }
        if (Buffer.byteLength(value) > this.limits.max_text_bytes) invalid(path, 'text exceeds max_text_bytes');
        return value;
      },
      decode(value) { if (typeof value !== 'string') mapping('expected Text'); return value as string; },
    };
    this.bool = {
      encode: (value, path = 'value') => { if (typeof value !== 'boolean') invalid(path, 'expected boolean'); return this.construct(value ? 'true' : 'false'); },
      decode: value => { if (!(value instanceof Constructor) || value.arguments.length || ![this.id('true'), this.id('false')].includes(value.identity)) mapping('expected Bool'); return (value as Constructor).identity === this.id('true'); },
    };
    this.hash = { encode(value, path = 'value') { if (!(value instanceof Hash)) invalid(path, 'expected Hash'); return value; }, decode(value) { if (!(value instanceof Hash)) mapping('expected Hash'); return value as Hash; } };
  }
  private depth(depth: number, path: string): void { if (depth > this.limits.max_json_depth) invalid(path, 'value exceeds the v0 JSON depth; use smaller independent batches'); }
  private id(name: string): string { const id = this.constructors.get(displayToStore(name)); if (!id) throw new BindingError(`unknown constructor ${name}`); return id; }
  private construct(name: string, ...args: Value[]): Constructor { return new Constructor(this.id(name), ...args); }
  make(name: string, ...fields: unknown[]): Variant { this.id(name); return new Variant(name, ...fields); }
  list<T>(item: Codec<T>): Codec<T[]> {
    return {
      encode: (items, path = 'value', depth = 3) => {
        if (!Array.isArray(items)) invalid(path, 'expected an array');
        if (items.length > this.limits.max_collection_items) invalid(path, 'list exceeds max_collection_items');
        this.depth(depth + 2 * items.length, path);
        let result = this.construct('nil');
        for (let i = items.length - 1; i >= 0; i--) result = this.construct('cons', item.encode(items[i], `${path}[${i}]`, depth + 2 * (i + 1)), result);
        return result;
      },
      decode: value => {
        const result: T[] = []; let cursor = value;
        while (cursor instanceof Constructor && cursor.identity === this.id('cons') && cursor.arguments.length === 2) {
          if (result.length >= this.limits.max_collection_items) mapping('list exceeds selected limit');
          result.push(item.decode(cursor.arguments[0])); cursor = cursor.arguments[1];
        }
        if (!(cursor instanceof Constructor) || cursor.identity !== this.id('nil') || cursor.arguments.length) mapping('expected a proper List');
        return result;
      },
    };
  }
  option<T>(item: Codec<T>): Codec<T | null | Some<T>> {
    return {
      encode: (value, path = 'value', depth = 3) => {
        this.depth(depth, path);
        return value === null ? this.construct('none') : this.construct('some', item.encode(value instanceof Some ? value.value : value, path, depth + 2));
      },
      decode: value => {
        if (!(value instanceof Constructor)) return mapping('expected Option');
        if (value.identity === this.id('none') && !value.arguments.length) return null;
        if (value.identity !== this.id('some') || value.arguments.length !== 1) return mapping('expected Option');
        const decoded = item.decode(value.arguments[0]);
        return decoded === null || decoded instanceof Some ? new Some(decoded) : decoded;
      },
    };
  }
  tuple<const C extends readonly Codec<unknown>[]>(...items: C): Codec<{ -readonly [K in keyof C]: Infer<C[K]> }> {
    type T = { -readonly [K in keyof C]: Infer<C[K]> };
    return {
      encode: (value, path = 'value', depth = 3) => {
        this.depth(depth, path);
        if (!Array.isArray(value) || value.length !== items.length) invalid(path, `expected a ${items.length}-item tuple`);
        return new Tuple(...items.map((codec, i) => codec.encode(value[i], `${path}[${i}]`, depth + 2)));
      },
      decode(value) { if (!(value instanceof Tuple) || value.items.length !== items.length) return mapping('tuple shape mismatch'); return items.map((codec, i) => codec.decode(value.items[i])) as T; },
    };
  }
  record<const Tag extends string, const F extends Fields>(tag: Tag, fields: F): RecordCodec<Tag, F> {
    const name = displayToStore(tag); const identity = this.id(name);
    if (builtins.has(name) || this.records.has(identity)) throw new BindingError(`constructor ${tag} is builtin or already mapped`);
    if (Object.keys(fields).some(key => ['$tag', '__proto__', 'constructor', 'prototype'].includes(key))) throw new BindingError('reserved record field name');
    const entries = Object.entries(fields);
    type T = Tagged<Tag, FieldValues<F>>;
    const codec: RecordCodec<Tag, F> = {
      tag, identity,
      create: value => ({ ...value, $tag: tag }),
      encode: (value, path = tag, depth = 3) => {
        this.depth(depth, path);
        if (!value || typeof value !== 'object' || value.$tag !== tag) invalid(path, `expected ${tag}.create({...})`);
        if (Object.keys(value).some(key => key !== '$tag' && !Object.hasOwn(fields, key))) invalid(path, 'unexpected record field');
        return new Constructor(identity, ...entries.map(([key, item]) => item.encode((value as Record<string, unknown>)[key], `${path}.${key}`, depth + 2)));
      },
      decode: value => {
        if (!(value instanceof Constructor) || value.identity !== identity || value.arguments.length !== entries.length) return mapping(`${tag} result has the wrong constructor or field count`);
        return { ...Object.fromEntries(entries.map(([key, item], index) => [key, item.decode(value.arguments[index])])), $tag: tag } as T;
      },
    };
    this.records.set(identity, codec as unknown as RecordCodec<string, Fields>); this.recordTags.set(tag, codec as unknown as RecordCodec<string, Fields>);
    return codec;
  }
  enumeration<const M extends Record<string, string>>(mapping_: M): Codec<keyof M & string> {
    const entries = Object.entries(mapping_).map(([key, name]) => [key, this.id(name)] as const);
    if (new Set(entries.map(([, id]) => id)).size !== entries.length || entries.some(([, id]) => builtins.has(this.labels.get(id) ?? ''))) throw new BindingError('enum constructors must be distinct and not builtin');
    return {
      encode(value, path = 'value') { const entry = entries.find(([key]) => key === value); if (!entry) invalid(path, 'unknown enum member'); return new Constructor(entry![1]); },
      decode(value) { const entry = value instanceof Constructor && !value.arguments.length ? entries.find(([, id]) => id === value.identity) : undefined; if (!entry) return mapping('unknown enum constructor'); return entry[0] as keyof M & string; },
    };
  }
  union<const C extends readonly RecordCodec<string, Fields>[]>(...cases: C): Codec<Infer<C[number]>> {
    return {
      encode(value, path, depth) {
        const codec = cases.find(item => (value as { $tag?: string })?.$tag === item.tag);
        if (!codec) invalid(path ?? 'value', 'unknown union case');
        return codec!.encode(value as Tagged<string, FieldValues<Fields>>, path, depth);
      },
      decode(value) { const codec = value instanceof Constructor ? cases.find(item => item.identity === value.identity) : undefined; if (!codec) return mapping('unknown union case'); return codec.decode(value) as Infer<C[number]>; },
    };
  }
  encode(value: unknown, expected?: BoundaryType, path = 'value', depth = 3): Value {
    this.depth(depth, path);
    if (expected?.kind === 'nominal') {
      const name = [...this.names].find(([, id]) => id === expected.identity)?.[0];
      if (name === 'int') return typeof value === 'bigint' ? this.bigint.encode(value, path) : this.int.encode(value as number, path);
      if (name === 'real') return value instanceof Real ? value : this.real.encode(value as number, path);
      if (name === 'text') return this.text.encode(value as string, path);
      if (name === 'bool') return this.bool.encode(value as boolean, path);
      if (name === 'hash') return this.hash.encode(value as Hash, path);
      const child = expected.arguments[0];
      if (name === 'list') return this.list({ encode: (item, itemPath, itemDepth) => this.encode(item, child, itemPath, itemDepth), decode: item => this.decode(item, child) }).encode(value as unknown[], path, depth);
      if (name === 'option') return this.option({ encode: (item, itemPath, itemDepth) => this.encode(item, child, itemPath, itemDepth), decode: item => this.decode(item, child) }).encode(value, path, depth);
    }
    if (expected?.kind === 'tuple') {
      if (value === undefined && !expected.items.length) return new Tuple();
      if (!Array.isArray(value) || value.length !== expected.items.length) invalid(path, 'tuple arity mismatch');
      return new Tuple(...expected.items.map((item, i) => this.encode(value[i], item, `${path}[${i}]`, depth + 2)));
    }
    if (value instanceof Hash || value instanceof Real || value instanceof Tuple || value instanceof Constructor) return value;
    if (value === undefined) return new Tuple();
    if (value instanceof Variant) return new Constructor(this.id(value.tag), ...value.fields.map((item, i) => this.encode(item, undefined, `${path}.fields[${i}]`, depth + 2)));
    if (value instanceof Some) return this.construct('some', this.encode(value.value, undefined, path, depth + 2));
    if (value === null) return this.construct('none');
    if (typeof value === 'boolean') return this.bool.encode(value, path);
    if (typeof value === 'bigint') return this.bigint.encode(value, path);
    if (typeof value === 'number') return Number.isInteger(value) ? this.int.encode(value, path) : this.real.encode(value, path);
    if (typeof value === 'string') return this.text.encode(value, path);
    if (Array.isArray(value)) return this.list({ encode: (item, p, d) => this.encode(item, undefined, p, d), decode: item => this.decode(item) }).encode(value, path, depth);
    if (value && typeof value === 'object' && '$tag' in value) {
      const codec = this.recordTags.get(String(value.$tag));
      if (codec) return codec.encode(value as Tagged<string, FieldValues<Fields>>, path, depth);
    }
    return invalid(path, 'unsupported value; register a record or use data.make');
  }
  decode(value: Value, expected?: BoundaryType): unknown {
    if (typeof value === 'bigint') { const number = Number(value); return Number.isSafeInteger(number) ? number : value; }
    if (value instanceof Real) return value.number;
    if (value instanceof Tuple) return value.items.map((item, i) => this.decode(item, expected?.kind === 'tuple' ? expected.items[i] : undefined));
    if (!(value instanceof Constructor)) return value;
    const label = this.labels.get(value.identity); if (!label) return mapping(`unmapped constructor ${value.identity}`);
    if (label === 'true' || label === 'false') return this.bool.decode(value);
    const child = expected?.kind === 'nominal' ? expected.arguments[0] : undefined;
    if (label === 'nil' || label === 'cons') return this.list({ encode: item => this.encode(item), decode: item => this.decode(item, child) }).decode(value);
    if (label === 'none') { if (value.arguments.length) return mapping('None has fields'); return null; }
    if (label === 'some') {
      if (value.arguments.length !== 1) return mapping('Some has the wrong field count');
      const decoded = this.decode(value.arguments[0], child);
      return expected?.kind === 'nominal' && expected.identity === this.names.get('option') && decoded !== null && !(decoded instanceof Some) ? decoded : new Some(decoded);
    }
    const record = this.records.get(value.identity); if (record) return record.decode(value);
    return new Variant(label, ...value.arguments.map(item => this.decode(item)));
  }
}
