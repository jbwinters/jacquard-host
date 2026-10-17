/** npm pack -> offline installation -> imports, types, assets, and carrier replay. */
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { resolve, join } from 'node:path';
const run = promisify(execFile);
const packageRoot = fileURLToPath(new URL('..', import.meta.url));
const scratch = resolve(packageRoot, '../.scratch'); await mkdir(scratch, { recursive: true });
const root = await mkdtemp(join(scratch, 'npm-distribution-'));
const npm = process.platform === 'win32' ? 'npm.cmd' : 'npm';
try {
  const packed = await run(npm, ['pack', '--ignore-scripts', '--json', '--pack-destination', root], { cwd: packageRoot });
  const info = JSON.parse(packed.stdout)[0];
  for (const name of ['LICENSE', 'NOTICE', 'README.md', 'dist/index.js', 'dist/index.d.ts', 'src/index.ts', 'data/conformance/kit/kit.json', 'data/examples/quickstart/rules.jac']) {
    if (!info.files.some(item => item.path === name)) throw new Error(`tarball missing ${name}`);
  }
  await writeFile(join(root, 'package.json'), '{"private":true,"type":"module"}');
  await run(npm, ['install', '--offline', '--ignore-scripts', '--no-audit', '--no-fund', '--no-package-lock', join(root, info.filename)], { cwd: root });
  const replayWorker = fileURLToPath(new URL('../test/replay-worker.mjs', import.meta.url));
  const smoke = `
import assert from 'node:assert/strict';
import { join } from 'node:path';
import { Runtime, VERSION, Host, Tuple } from 'jacquard-host';
import { tupleType } from 'jacquard-host/protocol';
import { loadKit, replayOne } from 'jacquard-host/kit';
assert.equal(VERSION, '0.2.0');
const kit = await loadKit();
for (const transcript of kit.transcripts) {
  const result = await replayOne(transcript, 'unused', { command: [process.execPath, ${JSON.stringify(replayWorker)}], env: { ...process.env, TRANSCRIPTS: join(kit.directory, 'kit/transcripts.json'), TRANSCRIPT_CASE: transcript.name } });
  assert.deepEqual(result.differences, [], transcript.name);
}
const host = new Host('unused', { command: [process.execPath, ${JSON.stringify(replayWorker)}], env: { ...process.env, TRANSCRIPTS: join(kit.directory, 'kit/transcripts.json'), TRANSCRIPT_CASE: 'pure-success' } });
assert.deepEqual((await host.invoke(kit.manifest.identities['kit-ready'], { parameters: [], result: tupleType(), effects: [] })).unwrap(), new Tuple());
console.log('installed package: public exports, all 21 transcripts, invocation, and pinned assets pass');
`;
  await writeFile(join(root, 'smoke.mjs'), smoke);
  const result = await run(process.execPath, [join(root, 'smoke.mjs')], { cwd: root }); process.stdout.write(result.stdout);
  await writeFile(join(root, 'consumer.ts'), `import { Runtime, type Infer } from 'jacquard-host';
const p = await new Runtime({ prelude: '/configured/prelude' }).prepare('rules.jac');
const f = p.function('shipping-cost', { parameters: [p.data.int], result: p.data.int });
const n: number = await f.call(35);
// @ts-expect-error installed declarations reject text in an Int position
f.call('35');
`);
  await run(resolve(packageRoot, 'node_modules/.bin/tsc'), ['--strict', '--target', 'ES2022', '--module', 'NodeNext', '--moduleResolution', 'NodeNext', '--noEmit', '--types', 'node', '--typeRoots', resolve(packageRoot, 'node_modules/@types'), join(root, 'consumer.ts')], { cwd: root });
  if (process.env.JACQUARD_BIN && process.env.JACQUARD_PRELUDE) {
    const entry = join(root, 'node_modules/jacquard-host/dist/examples/run.js');
    for (const project of ['quickstart', 'planner', 'triage', 'fulfillment']) {
      const result = await run(process.execPath, [entry, project, '--workdir', join(root, 'state')], { cwd: root, timeout: 30000 });
      if (project === 'quickstart' && !result.stdout.includes('Order total 75: shipping 0')) throw new Error('installed quickstart failed');
      if (project === 'planner' && !result.stdout.includes('"score": 16')) throw new Error('installed planner failed');
      if (project === 'triage' && JSON.parse(result.stdout).length !== 3) throw new Error('installed triage failed');
      if (project === 'fulfillment' && !result.stdout.includes('Fulfilled')) throw new Error('installed fulfillment failed');
    }
    console.log('installed package: all four examples pass with real Core, outside checkout');
  }
  console.log('npm distribution check passed (tarball -> offline installation -> exports and TypeScript consumer).');
} finally { await rm(root, { recursive: true, force: true }); }
