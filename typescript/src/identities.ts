import { BindingError } from './errors.js';
import { nominal, tupleType, type BoundaryType, type Interface } from './protocol.js';
export type Group = [name: string, hash: string][];
export const displayToStore = (name: string): string => /^[A-Z]/.test(name) ? name.replace(/(?<!^)(?=[A-Z])/g, '-').toLowerCase() : name;
export const normal = (name: string): string => displayToStore(name).replaceAll('_', '-');
export function hashGroups(output: string): Group[] {
  const groups = new Map<number, Group>();
  for (const line of output.split(/\r?\n/)) {
    const match = /^(\d+):(\S+) ([0-9a-f]{64})$/.exec(line.trim()); if (!match) continue;
    const index = Number(match[1]); const group = groups.get(index) ?? [];
    group.push([match[2], match[3]]); groups.set(index, group);
  }
  if (!groups.size) throw new BindingError('Core hash command returned no public identities');
  return [...groups.values()];
}
export function checkGroups(groups: Group[]): void {
  const seen = new Map<string, Set<string>>();
  for (const group of groups) {
    const current = new Map<string, Set<string>>();
    for (const [name, hash] of group) { const values = current.get(name) ?? new Set(); values.add(hash); current.set(name, values); }
    for (const [name, values] of current) {
      const previous = seen.get(name);
      if (previous && [...values].some(value => !previous.has(value))) throw new BindingError(`Ambiguous public name ${name} occurs in different declaration groups; use distinct names or explicit low-level identities`);
      seen.set(name, new Set([...(previous ?? []), ...values]));
    }
  }
}
export class SignatureError extends Error {}
export function parseSignature(text: string, names: ReadonlyMap<string, string>): Interface {
  const tokens: string[] = []; let position = 0;
  const pattern = /\s*(->\{|[(),{}]|[A-Za-z][A-Za-z0-9.?!-]*|\s)/y;
  while (position < text.length) {
    pattern.lastIndex = position; const match = pattern.exec(text);
    if (!match) throw new SignatureError(`unsupported signature near ${text.slice(position, position + 12)}`);
    position = pattern.lastIndex; if (match[1].trim()) tokens.push(match[1]);
  }
  let index = 0;
  const peek = () => tokens[index];
  const take = (expected?: string): string => {
    const value = tokens[index++];
    if (value === undefined || (expected !== undefined && value !== expected)) throw new SignatureError(`expected ${expected ?? 'type'}, found ${value}`);
    return value;
  };
  const identity = (display: string): string => {
    const value = names.get(displayToStore(display)) ?? names.get(display);
    if (!value) throw new SignatureError(`no identity for ${display}`); return value;
  };
  function list(close: string): BoundaryType[] {
    const result: BoundaryType[] = [];
    if (peek() === close) { take(close); return result; }
    while (true) {
      result.push(type());
      if (peek() === ',') { take(','); if (peek() === close) { take(close); return result; } }
      else { take(close); return result; }
    }
  }
  function atom(): BoundaryType {
    const token = take();
    if (token === '(') {
      const items = list(')');
      return items.length === 1 && tokens[index - 2] !== ',' ? items[0] : tupleType(...items);
    }
    if (!/^[A-Za-z]/.test(token)) throw new SignatureError('unsupported type');
    return nominal(identity(token));
  }
  function type(): BoundaryType {
    const head = atom();
    if (head.kind === 'nominal') while (peek() !== undefined && ![',', ')', '}', '->{'].includes(peek())) head.arguments.push(atom());
    return head;
  }
  take('('); const parameters = list(')'); take('->{'); const effects: string[] = [];
  if (peek() !== '}') while (true) { effects.push(identity(take())); if (peek() !== ',') break; take(','); }
  take('}'); const result = type();
  if (peek() !== undefined) throw new SignatureError('trailing signature tokens');
  return { parameters, effects: [...new Set(effects)].sort(), result };
}
