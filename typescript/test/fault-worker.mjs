import { readFile } from 'node:fs/promises';
const transcript = JSON.parse(await readFile(process.env.TRANSCRIPTS, 'utf8'))[0];
const [hello, outcome] = transcript.core_output;
const mode = process.env.MODE;
const send = frame => { const payload = Buffer.from(JSON.stringify(frame)); const prefix = Buffer.alloc(4); prefix.writeUInt32BE(payload.length); process.stdout.write(Buffer.concat([prefix, payload])); };
if (mode === 'exit') process.exit(0);
if (mode === 'silent') { setInterval(() => {}, 1000); }
else if (mode === 'truncated') { process.stdout.write(Buffer.from([0, 0, 0])); }
else if (mode === 'flood') { process.stdout.write(Buffer.alloc(3 * 1024 * 1024)); }
else {
  if (mode === 'stderr') await new Promise(resolve => process.stderr.write(Buffer.alloc(2 * 1024 * 1024, 120), resolve));
  send(mode === 'unknown-field' ? { ...hello, extra: 1 } : hello);
  if (mode === 'no-read') { process.stdin.pause(); setInterval(() => {}, 1000); }
  else {
    let input = Buffer.alloc(0); let count = 0;
    process.stdin.on('data', chunk => {
      input = Buffer.concat([input, chunk]);
      while (input.length >= 4 && input.length >= input.readUInt32BE() + 4) {
        input = input.subarray(input.readUInt32BE() + 4);
        if (++count === 2) {
          if (mode === 'invalid-hash') outcome.evidence.core.target.callable = 'A'.repeat(64);
          send(outcome);
          if (mode === 'extra') send(outcome);
          if (mode === 'stall') setInterval(() => {}, 1000);
          else { process.stdout.end(); process.stdin.destroy(); process.exitCode = mode === 'nonzero' ? 74 : 0; }
        }
      }
    });
  }
}
