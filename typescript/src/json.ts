/** Strict UTF-8 JSON, including duplicate-key checks and lossless 63-bit integers. */
export type Json = null | boolean | number | bigint | string | Json[] | { [key: string]: Json };
export type JsonObject = { [key: string]: Json };
export const INT_MIN = -(1n << 62n);
export const INT_MAX = (1n << 62n) - 1n;
export class ProtocolError extends Error {
  constructor(public readonly code: string, message: string) { super(message); this.name = 'ProtocolError'; }
}
export const malformed = (message: string): never => { throw new ProtocolError('E1601', message); };
export const exceeded = (message: string): never => { throw new ProtocolError('E1602', message); };
export const order = (message: string): never => { throw new ProtocolError('E1608', message); };
export function scalar(text: string): string {
  for (let i = 0; i < text.length; i++) {
    const code = text.charCodeAt(i);
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = text.charCodeAt(++i);
      if (!(next >= 0xdc00 && next <= 0xdfff)) malformed('a JSON string contains a lone surrogate');
    } else if (code >= 0xdc00 && code <= 0xdfff) malformed('a JSON string contains a lone surrogate');
  }
  return text;
}
export function object(value: unknown): JsonObject {
  if (!value || typeof value !== 'object' || Array.isArray(value)) malformed('expected a JSON object');
  return value as JsonObject;
}
export function exact(value: unknown, keys: string[]): JsonObject {
  const obj = object(value);
  if (Object.keys(obj).length !== keys.length || keys.some(key => !Object.hasOwn(obj, key))) malformed(`expected exactly: ${keys.join(', ')}`);
  return obj;
}
export function string(value: unknown): string {
  if (typeof value !== 'string') malformed('expected a string');
  return scalar(value as string);
}
export function array(value: unknown): Json[] {
  if (!Array.isArray(value)) malformed('expected an array');
  return value as Json[];
}

export function decodeObject(payload: Uint8Array, maxDepth = 64): JsonObject {
  let text: string;
  try { text = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(payload); }
  catch { return malformed('payload is not valid UTF-8'); }
  let position = 0;
  const whitespace = () => { while (/[\x20\t\r\n]/.test(text[position] ?? 'x')) position++; };
  function quoted(): string {
    const start = position++;
    while (position < text.length) {
      const character = text[position++];
      if (character === '\\') position++;
      else if (character === '"') {
        try { return scalar(JSON.parse(text.slice(start, position)) as string); }
        catch (error) { if (error instanceof ProtocolError) throw error; return malformed('invalid JSON string'); }
      }
    }
    return malformed('unterminated JSON string');
  }
  function value(depth: number): Json {
    whitespace();
    const character = text[position];
    if (character === '"') return quoted();
    if (character === '{' || character === '[') {
      if (depth > maxDepth) exceeded('JSON nesting exceeds max_json_depth');
      position++; whitespace();
      const close = character === '{' ? '}' : ']';
      const result: JsonObject | Json[] = character === '{' ? Object.create(null) as JsonObject : [];
      if (text[position] === close) { position++; return result; }
      while (position < text.length) {
        if (Array.isArray(result)) result.push(value(depth + 1));
        else {
          if (text[position] !== '"') malformed('object key must be a string');
          const key = quoted(); whitespace();
          if (Object.hasOwn(result, key)) malformed('duplicate JSON field name');
          if (text[position++] !== ':') malformed('expected a colon');
          result[key] = value(depth + 1);
        }
        whitespace();
        if (text[position] === close) { position++; return result; }
        if (text[position++] !== ',') malformed('expected a comma');
        whitespace();
      }
      return malformed('unterminated JSON container');
    }
    for (const [literal, decoded] of [['true', true], ['false', false], ['null', null]] as const) {
      if (text.startsWith(literal, position)) { position += literal.length; return decoded; }
    }
    const token = /^-?(?:0|[1-9][0-9]*)/.exec(text.slice(position))?.[0];
    if (!token || token.length > 20) malformed('invalid JSON scalar');
    position += token!.length;
    const integer = BigInt(token!);
    if (integer < INT_MIN || integer > INT_MAX) malformed('JSON integer is outside the 63-bit range');
    return integer >= BigInt(Number.MIN_SAFE_INTEGER) && integer <= BigInt(Number.MAX_SAFE_INTEGER) ? Number(integer) : integer;
  }
  const result = value(1); whitespace();
  if (position !== text.length) malformed('payload is not one JSON value');
  return object(result);
}

export function stringify(value: Json, maxDepth = 64, depth = 1): string {
  if (typeof value === 'string') return JSON.stringify(scalar(value));
  if (value === null || typeof value === 'boolean') return String(value);
  if (typeof value === 'number' || typeof value === 'bigint') {
    if (typeof value === 'number' && !Number.isSafeInteger(value)) malformed('JSON numbers must be safe integers; use bigint for larger integers');
    const integer = BigInt(value);
    if (integer < INT_MIN || integer > INT_MAX) malformed('JSON integer is outside the 63-bit range');
    return String(value);
  }
  if (depth > maxDepth) exceeded('JSON nesting exceeds max_json_depth');
  if (Array.isArray(value)) return '[' + Array.from(value, item => stringify(item, maxDepth, depth + 1)).join(',') + ']';
  object(value);
  return '{' + Object.keys(value).sort().map(key => `${JSON.stringify(scalar(key))}:${stringify(value[key], maxDepth, depth + 1)}`).join(',') + '}';
}
export function frameBytes(frame: JsonObject, maxBytes = 1048576, maxDepth = 64): Buffer {
  const payload = Buffer.from(stringify(object(frame), maxDepth));
  if (payload.length < 1 || payload.length > maxBytes) exceeded('payload exceeds max_frame_bytes');
  const prefix = Buffer.alloc(4); prefix.writeUInt32BE(payload.length);
  return Buffer.concat([prefix, payload]);
}
