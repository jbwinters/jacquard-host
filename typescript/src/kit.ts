/** Replay the published kit independently, including intentional hostile host inputs. */
import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { join } from 'node:path';
import { CARRIER, CORE_COMMIT, CORE_VERSION, KIT_DIGESTS, PROTOCOL } from './compat.js';
import { type Json, type JsonObject, array, object, stringify, frameBytes } from './json.js';
import { Worker, type WorkerOptions } from './worker.js';

export const KIT_DIRECTORY = fileURLToPath(new URL('../data/conformance', import.meta.url));
export interface Kit { directory: string; manifest: JsonObject; transcripts: Transcript[] }
export interface Transcript {
  name: string; kind: string; phase: string | null; expect: JsonObject; observed: JsonObject;
  status: string; exit_status: number;
  core_output: JsonObject[];
  host_input: { direction: string; raw_hex?: string; frame?: JsonObject; after_terminal?: boolean }[];
}
export async function loadKit(directory = KIT_DIRECTORY): Promise<Kit> {
  for (const [file, expected] of Object.entries(KIT_DIGESTS)) {
    const actual = createHash('sha256').update(await readFile(join(directory, file))).digest('hex');
    if (actual !== expected) throw new Error(`pinned conformance digest mismatch: ${file}`);
  }
  const manifest = JSON.parse(await readFile(join(directory, 'kit/kit.json'), 'utf8')) as JsonObject;
  const transcripts = JSON.parse(await readFile(join(directory, 'kit/transcripts.json'), 'utf8')) as Transcript[];
  if (manifest.protocol !== PROTOCOL || manifest.carrier !== CARRIER || manifest.core_version !== CORE_VERSION || transcripts.length !== 21) throw new Error('kit metadata does not match the pin');
  return { directory, manifest, transcripts };
}
const terminal = (frame: JsonObject): boolean => ['outcome', 'fatal', 'shutdown_ack'].includes(String(frame.kind));
const maybeObject = (value: Json | undefined): JsonObject | null => value && typeof value === 'object' && !Array.isArray(value) ? value : null;
const prose = ['summary', 'cause', 'next_step', 'contrast'];
function stripProse(value: Json): Json {
  if (Array.isArray(value)) return value.map(stripProse);
  if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).filter(([key]) => value.schema !== 'jacquard-diagnostic-v1' || !prose.includes(key)).map(([key, item]) => [key, stripProse(item)]));
  return value;
}
function proseDrift(expected: Json, actual: Json, path = ''): string[] {
  if (!expected || !actual || typeof expected !== 'object' || typeof actual !== 'object') return [];
  if (Array.isArray(expected) && Array.isArray(actual)) return expected.flatMap((item, index) => proseDrift(item, actual[index], `${path}/${index}`));
  if (Array.isArray(expected) || Array.isArray(actual)) return [];
  if (expected.schema === 'jacquard-diagnostic-v1') return prose.filter(key => stringify(expected[key] ?? null) !== stringify(actual[key] ?? null)).map(key => `${path}/${key}`);
  return Object.keys(expected).flatMap(key => proseDrift(expected[key], actual[key], `${path}/${key}`));
}
export interface Replay {
  name: string; matchesRecording: boolean; status: 'conforms' | 'diverges'; differences: string[]; proseDrift: string[];
  summary: JsonObject; coreFrames: JsonObject[]; exitStatus: number | null; signal: NodeJS.Signals | null; stderr: Buffer;
}
export async function replayOne(transcript: Transcript, store: string, options: WorkerOptions = {}): Promise<Replay> {
  const worker = new Worker(store, options);
  const frames: JsonObject[] = []; let ended = false; let refusal: string | null = null; let skipped = 0;
  const differences: string[] = [];
  const read = async () => {
    const frame = await worker.readFrame();
    if (!frame) return false;
    frames.push(frame); if (terminal(frame)) ended = true; return true;
  };
  try {
    for (const step of transcript.host_input) {
      if (step.direction === 'core_to_host') await read();
      else if (step.raw_hex !== undefined) { await worker.writeBytes(Buffer.from(step.raw_hex, 'hex')); worker.closeInput(); }
      else if (step.after_terminal && ended) refusal = 'E1608';
      else if (ended) skipped++;
      else {
        try { await worker.writeBytes(frameBytes(step.frame!)); }
        catch (error) { if (!(error instanceof Error) || !/write failed/.test(error.message)) throw error; }
      }
    }
    worker.closeInput(); while (await read()) { /* bounded by the child lifetime */ }
    await worker.wait();
  } catch (error) { differences.push(error instanceof Error ? error.message : 'replay failed'); }
  finally { await worker.dispose(); }
  const end = frames.find(terminal);
  const result = end?.kind === 'outcome' ? maybeObject(end.result) : null;
  const diagnosticArray = end?.kind === 'fatal' ? end.diagnostics : result?.kind === 'error' ? result.diagnostics : [];
  const diagnostic = Array.isArray(diagnosticArray) ? maybeObject(diagnosticArray[0]) : null;
  const primary = diagnostic?.code ?? null;
  const summary: JsonObject = {
    terminal_kind: end?.kind ?? null, primary_code: primary ?? null,
    terminal: end?.kind === 'fatal' ? 'fatal' : end?.kind === 'shutdown_ack' ? 'shutdown' : maybeObject(maybeObject(end?.evidence)?.core)?.terminal ?? null,
    effect_requests: frames.filter(frame => frame.kind === 'effect_request').length,
    terminal_frames: frames.filter(terminal).length, host_local_refusal: refusal, host_inputs_skipped_after_terminal: skipped,
  };
  const expected = transcript.expect;
  let conforms: boolean;
  if (transcript.kind === 'positive') {
    conforms = summary.terminal === expected.terminal && worker.exitStatus === 0 && stringify(stripProse(frames)) === stringify(stripProse(transcript.core_output));
    const message = transcript.host_input.find(step => step.frame?.kind === 'effect_failure')?.frame?.message;
    if (message !== undefined) conforms &&= typeof diagnostic?.cause === 'string' && diagnostic.cause.endsWith(String(message));
  } else {
    conforms = summary.effect_requests === expected.effect_requests_before_failure && Number(summary.terminal_frames) <= Number(expected.terminal_frames_max) && (!expected.forbid_outside_action || summary.effect_requests === 0) && worker.exitStatus === (expected.code === 'E1611' ? 74 : 0);
    conforms &&= transcript.phase === 'terminal' ? refusal === expected.code && summary.terminal === 'ok' : primary === expected.code;
  }
  const status = conforms ? 'conforms' : 'diverges';
  if (status !== transcript.status) differences.push('conformance status differs from the recording');
  if (worker.exitStatus !== transcript.exit_status) differences.push('exit status differs from the recording');
  if (stringify(summary) !== stringify(transcript.observed)) differences.push('observed summary differs from the recording');
  if (stringify(stripProse(frames)) !== stringify(stripProse(transcript.core_output))) differences.push('Core frames differ semantically from the recording');
  return { name: transcript.name, matchesRecording: !differences.length, status, differences, proseDrift: proseDrift(transcript.core_output, frames), summary, coreFrames: frames, exitStatus: worker.exitStatus, signal: worker.signal, stderr: worker.stderr };
}
export async function replayKit(store: string, options: WorkerOptions = {}, kit?: Kit): Promise<Replay[]> {
  kit ??= await loadKit(); const result: Replay[] = [];
  for (const transcript of kit.transcripts) result.push(await replayOne(transcript, store, options));
  return result;
}
export const kitPin = Object.freeze({ protocol: PROTOCOL, carrier: CARRIER, coreVersion: CORE_VERSION, coreCommit: CORE_COMMIT, digests: KIT_DIGESTS });
