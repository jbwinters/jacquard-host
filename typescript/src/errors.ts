import type { InvocationResult } from './host.js';
export class JacquardError extends Error {
  batchIndex?: number;
  completedValues?: unknown[];
  constructor(message: string, options?: ErrorOptions) { super(message, options); this.name = new.target.name; }
}
export class PreparationError extends JacquardError {}
export class BindingError extends JacquardError { result?: InvocationResult }
export class InvalidArgument extends JacquardError {}
export class InvocationError extends JacquardError {
  constructor(readonly result: InvocationResult) { super(result.message); }
  get code(): string | null { return this.result.code; }
  get origin(): 'core' | 'host' { return this.result.diagnostics.length ? 'core' : 'host'; }
}
export class DeadlineExceeded extends InvocationError {}
export class WorkerLost extends InvocationError {}
export class OperationFailed extends InvocationError {}
export function invocationError(result: InvocationResult): InvocationError {
  if (result.hostEvidence.reason === 'deadline') return new DeadlineExceeded(result);
  if (result.kind === 'carrier_loss') return new WorkerLost(result);
  if (result.terminal === 'host_failure' || result.terminal === 'completion_unknown') return new OperationFailed(result);
  return new InvocationError(result);
}
