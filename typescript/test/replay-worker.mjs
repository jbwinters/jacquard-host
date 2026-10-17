// Stand-in for offline recorded-transcript tests. It implements no Core semantics.
import { readFile } from 'node:fs/promises';
import { once } from 'node:events';
const transcript = JSON.parse(await readFile(process.env.TRANSCRIPTS, 'utf8')).find(item => item.name === process.env.TRANSCRIPT_CASE);
let input = Buffer.alloc(0);
process.stdin.on('data', chunk => { input = Buffer.concat([input, chunk]); process.emit('input-changed'); });
const waitBytes = async length => {
  while (input.length < length) await once(process, 'input-changed');
  const bytes = input.subarray(0, length); input = input.subarray(length); return bytes;
};
let coreIndex = 0;
let raw = false;
for (const step of transcript.host_input) {
  if (step.direction === 'core_to_host') {
    const frame = transcript.core_output[coreIndex++];
    const payload = Buffer.from(JSON.stringify(frame)); const prefix = Buffer.alloc(4); prefix.writeUInt32BE(payload.length);
    process.stdout.write(Buffer.concat([prefix, payload]));
    if (['outcome', 'fatal', 'shutdown_ack'].includes(frame.kind)) break;
  } else if (step.raw_hex !== undefined) { await waitBytes(Buffer.from(step.raw_hex, 'hex').length); raw = true; }
  else if (!step.after_terminal && !raw) { const prefix = await waitBytes(4); await waitBytes(prefix.readUInt32BE()); }
}
process.stdout.end(); process.stdin.destroy(); process.exitCode = transcript.exit_status;
