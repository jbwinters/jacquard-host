import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, mkdir, rm } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { Runtime, OperationFailed } from '../dist/index.js';
import { fulfillment, Warehouse } from '../dist/examples/projects.js';
if (!process.env.JACQUARD_BIN || !process.env.JACQUARD_PRELUDE) throw new Error('live tests require JACQUARD_BIN and JACQUARD_PRELUDE');
const run = promisify(execFile);
await mkdir('../.scratch', { recursive: true });
const root = await mkdtemp(resolve('../.scratch/typescript-examples-'));
test.after(async () => { await rm(root, { recursive: true, force: true }); });
test('compiled quickstart, planner, triage, and fulfillment run as CLI applications', async () => {
  const entry = resolve('dist/examples/run.js');
  for (const project of ['quickstart', 'planner', 'triage', 'fulfillment']) {
    const { stdout } = await run(process.execPath, [entry, project, '--workdir', join(root, project)], { cwd: root, timeout: 30000 });
    if (project === 'quickstart') assert.equal(stdout, 'Order total 35: shipping 5\nOrder total 75: shipping 0\n');
    if (project === 'planner') assert.match(stdout, /"score": 16/);
    if (project === 'triage') { const result = JSON.parse(stdout); assert.deepEqual(result.map(row => row.urgency), ['PAGE', 'TODAY', 'WATCH']); }
    if (project === 'fulfillment') {
      assert.match(stdout, /"\$tag": "Fulfilled"/);
      const repeated = await run(process.execPath, [entry, project, '--workdir', join(root, project)], { cwd: root, timeout: 30000 });
      assert.match(repeated.stdout, /DuplicateOrder/); assert.match(repeated.stdout, /"deliveries":1/);
    }
  }
});
test('all three partial-completion faults preserve state and explicit recovery deduplicates across restart', async () => {
  const runtime = new Runtime();
  for (const [fault, code, deliveries] of [['after-commit', 'E1612', 1], ['publish-failed', 'E1613', 1], ['after-delivery', 'E1612', 0]]) {
    const program = await runtime.prepare(resolve('../examples/fulfillment/rules.jac'), { cacheDir: join(root, 'programs') });
    const path = join(root, fault, 'inventory.json');
    const { Line, Order, warehouse, place } = await fulfillment(program, path);
    const order = Order.create({ id: 'one', lines: [Line.create({ sku: 'sensor', units: 2 })] });
    warehouse.fault = fault;
    await assert.rejects(place.call(order), error => error instanceof OperationFailed && error.code === code);
    assert.equal(warehouse.summary().bookings, 1);
    const restarted = await Warehouse.open(path, program.data);
    assert.equal(await restarted.deliverPending(), deliveries); assert.equal(await restarted.deliverPending(), 0);
    assert.deepEqual(restarted.summary(), { bookings: 1, pendingDeliveries: 0, deliveries: 1 });
    const repeated = program.function('fulfillment.place', { effects: { Inventory: restarted, Dispatch: restarted } });
    const result = await repeated.call(order); assert.equal(result.$tag, 'DuplicateOrder');
    assert.deepEqual(restarted.summary(), { bookings: 1, pendingDeliveries: 0, deliveries: 1 });
  }
});
