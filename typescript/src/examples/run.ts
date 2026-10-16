import { readFile } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';
import { JacquardError, Runtime } from '../index.js';
import { fulfillment, planner, triage } from './projects.js';

export const EXAMPLES = fileURLToPath(new URL('../../data/examples', import.meta.url));
async function main(): Promise<void> {
  const { values, positionals } = parseArgs({ allowPositionals: true, options: {
    workdir: { type: 'string', default: '.scratch/typescript-examples' }, inspect: { type: 'string' },
    fault: { type: 'string', default: '' }, 'deliver-pending': { type: 'boolean', default: false },
  } });
  const [project, input] = positionals;
  if (!['quickstart', 'triage', 'planner', 'fulfillment'].includes(project)) throw new Error('Choose quickstart, triage, planner, or fulfillment');
  if (!['', 'publish-failed', 'after-commit', 'after-delivery'].includes(values.fault)) throw new Error('Unknown fault');
  const workdir = resolve(values.workdir); const runtime = new Runtime();
  const program = await runtime.prepare(join(EXAMPLES, project, 'rules.jac'), { cacheDir: join(workdir, 'programs') });
  if (project === 'quickstart') {
    const shipping = program.function('shipping-cost', { parameters: [program.data.int], result: program.data.int });
    console.log(`Order total 35: shipping ${await shipping.call(35)}`);
    console.log(`Order total 75: shipping ${await shipping.call(75)}`); return;
  }
  const payload = JSON.parse(await readFile(input ?? join(EXAMPLES, project, 'input.json'), 'utf8'));
  if (project === 'planner') {
    const { Job, plan } = planner(program);
    const run = await plan.invoke(payload.jobs.map((row: Parameters<typeof Job.create>[0]) => Job.create(row)), payload.capacities);
    if (values.inspect) await run.save(values.inspect);
    console.log(JSON.stringify(run.unwrap(), null, 2)); console.log(run.render());
  } else if (project === 'triage') {
    const { Policy, Incident, route } = triage(program);
    const policy = Policy.create(payload.policy); const rows = payload.incidents.map((row: Parameters<typeof Incident.create>[0]) => Incident.create(row));
    if (values.inspect) {
      const results = [];
      for (let start = 0; start < rows.length; start += 8) {
        const run = await route.invoke(policy, rows.slice(start, start + 8));
        await run.save(`${values.inspect}.${start / 8}.json`); results.push(...run.unwrap());
      }
      console.log(JSON.stringify(results, null, 2));
    } else console.log(JSON.stringify(await route.mapBatches([policy, rows]), null, 2));
  } else {
    const { Line, Order, warehouse, place } = await fulfillment(program, join(workdir, 'inventory.json'));
    warehouse.fault = values.fault;
    try {
      if (values['deliver-pending']) { console.log(JSON.stringify({ delivered: await warehouse.deliverPending() })); return; }
      const run = await place.invoke(Order.create({ id: payload.id, lines: payload.lines.map((row: Parameters<typeof Line.create>[0]) => Line.create(row)) }));
      if (values.inspect) await run.save(values.inspect);
      console.log(JSON.stringify(run.unwrap(), null, 2)); console.log(run.render());
    } finally { console.log(JSON.stringify(warehouse.summary())); }
  }
}
main().catch(error => { console.error(error instanceof Error ? error.message : String(error)); process.exitCode = error instanceof JacquardError ? 1 : 2; });
