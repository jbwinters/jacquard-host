import { copyFile, cp, mkdir, rm } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';

const root = fileURLToPath(new URL('..', import.meta.url));
await rm(resolve(root, 'data'), { recursive: true, force: true });
await mkdir(resolve(root, 'data'), { recursive: true });
await cp(resolve(root, '../src/jacquard_host/data/conformance/host-protocol-v0'), resolve(root, 'data/conformance'), { recursive: true });
for (const file of ['LICENSE', 'NOTICE']) await copyFile(resolve(root, '..', file), resolve(root, file));
for (const project of ['quickstart', 'planner', 'triage', 'fulfillment']) {
  const destination = resolve(root, 'data/examples', project);
  await mkdir(destination, { recursive: true });
  for (const file of project === 'quickstart' ? ['rules.jac'] : ['rules.jac', 'input.json']) {
    await copyFile(resolve(root, '../examples', project, file), resolve(destination, file));
  }
}
