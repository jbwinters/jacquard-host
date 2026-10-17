/** Application mappings for the same Jacquard rules used by the Python examples. */
import { createHash, randomUUID } from 'node:crypto';
import { mkdir, readFile, rename, rm, writeFile } from 'node:fs/promises';
import { dirname } from 'node:path';
import { Data, type Infer } from '../data.js';
import { HostFailure } from '../host.js';
import { type Program } from '../sdk.js';

export function planner(program: Program) {
  const d = program.data;
  const Job = d.record('Job', { id: d.text, effort: d.int, value: d.int, latest: d.int });
  const Assignment = d.record('Assignment', { id: d.text, slot: d.int });
  const Plan = d.record('Plan', { score: d.int, assignments: d.list(Assignment) });
  const Planned = d.record('Planned', { plan: Plan });
  const InvalidPlan = d.record('InvalidPlan', { reason: d.text });
  return { Job, plan: program.function('planner.plan', { parameters: [d.list(Job), d.list(d.int)], result: d.union(Planned, InvalidPlan) }) };
}
export function triage(program: Program) {
  const d = program.data;
  const Policy = d.record('TriagePolicy', { page: d.int, stale: d.int });
  const Incident = d.record('Incident', { id: d.text, service: d.text, severity: d.int, age: d.int, visible: d.bool, owner: d.option(d.text) });
  const urgency = d.enumeration({ PAGE: 'PageNow', TODAY: 'WorkToday', WATCH: 'WatchOnly' });
  const Routed = d.record('Routed', { id: d.text, team: d.text, urgency, reasons: d.list(d.text) });
  const InvalidIncident = d.record('InvalidIncident', { id: d.text });
  return { Policy, Incident, route: program.function('triage.batch', { parameters: [Policy, d.list(Incident)], result: d.list(d.union(Routed, InvalidIncident)) }) };
}
interface Order { id: string; lines: { sku: string; units: number }[] }
interface State {
  schema: 'jacquard-example-inventory-v1'; stock: { sku: string; available: number }[];
  bookings: { id: string; payload: string; receipt: string }[];
  outbox: { id: string; receipt: string; delivered: boolean }[];
  deliveries: string[];
}
/** Single-process synthetic service. Atomic JSON snapshots; no cross-process locking. */
export class Warehouse {
  fault = '';
  calls: string[] = [];
  private constructor(readonly path: string, readonly data: Data, private state: State) {}
  static async open(path: string, data: Data): Promise<Warehouse> {
    let state: State;
    try {
      state = JSON.parse(await readFile(path, 'utf8')) as State;
      if (state.schema !== 'jacquard-example-inventory-v1') throw new Error('inventory snapshot version mismatch');
    } catch (error) {
      if (!(error && typeof error === 'object' && 'code' in error && error.code === 'ENOENT')) throw error;
      state = { schema: 'jacquard-example-inventory-v1', stock: [{ sku: 'sensor', available: 20 }, { sku: 'cable', available: 30 }, { sku: 'battery', available: 15 }], bookings: [], outbox: [], deliveries: [] };
    }
    return new Warehouse(path, data, state);
  }
  private async commit(state: State): Promise<void> {
    const temporary = `${this.path}.${randomUUID()}.tmp`;
    await mkdir(dirname(this.path), { recursive: true });
    try { await writeFile(temporary, JSON.stringify(state) + '\n', { flag: 'wx', mode: 0o600 }); await rename(temporary, this.path); this.state = state; }
    finally { await rm(temporary, { force: true }); }
  }
  private payload(order: Order): string { return JSON.stringify(order.lines.map(line => [line.sku, line.units]).sort((a, b) => String(a[0]).localeCompare(String(b[0])))); }
  bookingStatus(order: Order) {
    this.calls.push('status');
    const previous = this.state.bookings.find(item => item.id === order.id);
    return !previous ? this.data.make('BookingPending') : previous.payload === this.payload(order) ? this.data.make('RepeatBooking', previous.receipt) : this.data.make('BookingConflict');
  }
  lookupStock(sku: string) {
    this.calls.push('lookup'); const stock = this.state.stock.find(item => item.sku === sku);
    return stock ? this.data.make('Stock', stock.available) : this.data.make('UnknownSku');
  }
  async reserveOrder(order: Order) {
    this.calls.push('reserve');
    if (!order.id || !order.lines.length || order.lines.length > 20 || new Set(order.lines.map(line => line.sku)).size !== order.lines.length || order.lines.some(line => !Number.isSafeInteger(line.units) || line.units < 1 || line.units > 20)) throw new HostFailure('refused_authority', 'not_started', 'invalid synthetic reservation');
    const previous = this.state.bookings.find(item => item.id === order.id);
    if (previous) return previous.payload === this.payload(order) ? this.data.make('RepeatBooking', previous.receipt) : this.data.make('BookingConflict');
    const next = structuredClone(this.state);
    for (const line of order.lines) {
      const stock = next.stock.find(item => item.sku === line.sku);
      if (!stock || stock.available < line.units) return this.data.make('StockChanged');
      stock.available -= line.units;
    }
    const payload = this.payload(order); const receipt = createHash('sha256').update(order.id + '\0' + payload).digest('hex');
    next.bookings.push({ id: order.id, payload, receipt }); next.outbox.push({ id: order.id, receipt, delivered: false });
    await this.commit(next);
    if (this.fault === 'after-commit') throw new Error('synthetic fault after booking and outbox commit');
    return this.data.make('Booked', receipt);
  }
  async publishBooking(id: string, receipt: string): Promise<void> {
    this.calls.push('publish');
    if (this.fault === 'publish-failed') throw new HostFailure('outside_failure', 'failed', 'synthetic delivery unavailable; outbox retained');
    const next = structuredClone(this.state); const entry = next.outbox.find(item => item.receipt === receipt && item.id === id);
    if (!entry) throw new HostFailure('refused_authority', 'not_started', 'no matching outbox entry');
    if (!next.deliveries.includes(receipt)) next.deliveries.push(receipt);
    entry.delivered = true; await this.commit(next);
    if (this.fault === 'after-delivery') throw new Error('synthetic fault after delivery commit');
  }
  async deliverPending(): Promise<number> {
    const entries = this.state.outbox.filter(item => !item.delivered);
    for (const entry of entries) await this.publishBooking(entry.id, entry.receipt);
    return entries.length;
  }
  summary() { return { bookings: this.state.bookings.length, pendingDeliveries: this.state.outbox.filter(item => !item.delivered).length, deliveries: this.state.deliveries.length }; }
}
export async function fulfillment(program: Program, path: string) {
  const d = program.data;
  const Line = d.record('Line', { sku: d.text, units: d.int });
  const Order = d.record('Order', { id: d.text, lines: d.list(Line) });
  const Fulfilled = d.record('Fulfilled', { receipt: d.text });
  const DuplicateOrder = d.record('DuplicateOrder', { receipt: d.text });
  const OrderRejected = d.record('OrderRejected', { reason: d.text });
  const warehouse = await Warehouse.open(path, d);
  return { Line, Order, warehouse, place: program.function('fulfillment.place', {
    parameters: [Order], result: d.union(Fulfilled, DuplicateOrder, OrderRejected), effects: { Inventory: warehouse, Dispatch: warehouse },
  }) };
}
