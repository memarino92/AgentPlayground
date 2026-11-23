import {
  ExecInterruptedError, RailwayAuthError, RailwayConnectionError, RailwayGraphQLError,
  SandboxFailedError, SandboxFileNotFoundError, SandboxFilesError, SandboxNotFoundError, SandboxTimeoutError
} from 'railway';

// Only this vocabulary crosses into Worker logs. Never serialize an error, cause, command result, or request.
export class BridgeError extends Error {
  constructor(reason, code = null) {
    super(reason);
    this.reason = reason;
    this.code = code;
  }
}

const systemReasons = new Map([
  ['ENOENT', 'file-not-found'], ['EACCES', 'access-denied'], ['EPERM', 'access-denied'],
  ['ENOSPC', 'disk-full'], ['EEXIST', 'file-exists'], ['EROFS', 'read-only-filesystem'],
  ['ECONNRESET', 'connection-reset'], ['ECONNREFUSED', 'connection-refused'],
  ['ETIMEDOUT', 'timeout'], ['EAI_AGAIN', 'dns-failure'], ['ENOTFOUND', 'dns-failure'],
  ['EPIPE', 'connection-closed'], ['ABORT_ERR', 'cancelled']
]);

function systemReason(error) {
  return systemReasons.get(error?.code) ?? systemReasons.get(error?.cause?.code);
}

export function failureDetails(error) {
  if (error instanceof BridgeError) return { reason: error.reason, code: error.code };
  if (error instanceof RailwayAuthError) return { reason: 'authentication' };
  if (error instanceof RailwayGraphQLError) {
    const denied = [401, 403].includes(error.status) || error.errors.some(item =>
      ['FORBIDDEN', 'UNAUTHENTICATED'].includes(item.extensions?.code) || item.message === 'Not Authorized');
    const reason = denied ? 'authentication' : error.status === 429 ? 'rate-limited' : 'graphql-error';
    return { reason, code: error.status };
  }
  if (error instanceof ExecInterruptedError) return { reason: 'exec-interrupted', code: error.closeCode };
  if (error instanceof RailwayConnectionError)
    return { reason: systemReason(error) ?? 'connection-failure', code: error.closeCode };
  if (error instanceof SandboxTimeoutError) return { reason: 'timeout' };
  if (error instanceof SandboxNotFoundError) return { reason: 'sandbox-not-found' };
  if (error instanceof SandboxFailedError) return { reason: 'sandbox-start-failed' };
  if (error instanceof SandboxFileNotFoundError) return { reason: 'file-not-found' };
  if (error instanceof SandboxFilesError) return { reason: commandReason(error.message) ?? 'sandbox-file-error' };
  return { reason: systemReason(error) ?? 'unexpected-error' };
}

function commandReason(text) {
  // Match fixed symptoms without copying any message. Even controller commands can echo user filenames.
  const sample = typeof text === 'string' ? text.slice(0, 8192) : '';
  if (/permission denied|operation not permitted/i.test(sample)) return 'access-denied';
  if (/no space left on device/i.test(sample)) return 'disk-full';
  if (/read-only file system/i.test(sample)) return 'read-only-filesystem';
  if (/no such container/i.test(sample)) return 'container-not-found';
  if (/no such image/i.test(sample)) return 'image-not-found';
  if (/no such file|not found/i.test(sample)) return 'file-not-found';
  if (/cannot connect to the docker daemon/i.test(sample)) return 'docker-unavailable';
}

export function checkCommand(result) {
  if (result.timedOut) throw new BridgeError('timeout');
  if (result.exitCode !== 0) throw new BridgeError(commandReason(result.stderr) ?? 'command-failed', result.exitCode);
}
