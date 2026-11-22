// Trusted controller adapter; workload containers never receive the Railway token or Docker socket.
import { checkpointVersion, maxArtifactBytes, validateCodingArtifact } from './coding-checkpoint.mjs';

export async function codingDispatch(request, api, now = () => performance.now()) {
  const options = { token: request.token, environmentId: request.environmentId, authType: 'bearer', verbose: false };
  if (request.operation === 'coding-create') {
    if (!/^[A-Za-z0-9_.-]{1,128}$/.test(request.checkpoint)) throw new Error('Invalid checkpoint');
    try {
      const sandbox = await api.create(request.checkpoint, { ...options, networkIsolation: 'ISOLATED', idleTimeoutMinutes: 5 });
      return { id: sandbox.id };
    } catch (error) {
      // Provider messages may contain credentials. Only return controller-authored descriptions.
      return { provisioningError: provisioningError(error) };
    }
  }
  validateRequest(request);
  const started = now();
  const sandbox = await api.connect(request.id, options);
  if (sandbox.networkIsolation !== 'ISOLATED') throw new Error('Unexpected network');
  const infoResult = await sandbox.exec('docker info --format "{{json .}}"', { timeoutSec: 15 });
  const info = JSON.parse(infoResult.stdout);
  if (infoResult.exitCode !== 0 || !['MemoryLimit', 'SwapLimit', 'CpuCfsQuota', 'PidsLimit'].every(k => info[k])
    || !info.SecurityOptions?.some(v => /^name=seccomp,profile=(builtin|default)$/.test(v))) throw new Error('Resource limits unavailable');
  let checkpointCapable = false;
  if (request.executionSeconds !== undefined || request.resumePhase || request.supervise) {
    const inspected = await sandbox.exec(`docker image inspect --format '{{if .Config.Labels}}{{index .Config.Labels "io.agentplayground.coding-checkpoint"}}{{end}}' ${request.imageId}`,
      { timeoutSec: 10 });
    checkpointCapable = inspected.exitCode === 0 && inspected.stdout.trim() === String(checkpointVersion);
  }
  if (!checkpointCapable && (request.resumePhase || request.supervise)) {
    const summary = 'This coding image does not support resume or supervision. Prepare and select a current checkpoint.';
    return { success: false, artifact: { baseSha: request.baseSha, files: [], summary,
      checks: [{ command: 'prepare', exitCode: 1, output: summary }] } };
  }
  const cutoff = started + (request.executionSeconds ?? request.minutes * 60 - 75) * 1000;
  const priorCode = request.resumePhase === 'validate' ? request.resumeArtifact.checks.find(check => check.command === 'code') : null;
  const resumeArtifact = request.resumeArtifact && { baseSha: request.resumeArtifact.baseSha,
    files: request.resumeArtifact.files, summary: request.resumeArtifact.summary };
  // Inputs are JSON file data, never shell fragments. Only validated image/duration enter commands.
  await sandbox.files.write('/coding/request.json', JSON.stringify({ repository: request.repository, baseSha: request.baseSha,
    instruction: request.instruction, gateway: request.gateway, capability: request.capability,
    minutes: request.minutes, canAsk: request.canAsk, followUpEvidence: request.followUpEvidence,
    resumeArtifact, resumePhase: request.resumePhase, ownerClarifications: request.ownerClarifications, supervise: request.supervise,
    testProject: request.testProject, testFilter: request.testFilter, model: request.model, responses: request.responses }));
  const readable = await sandbox.exec('chmod 0444 /coding/request.json', { timeoutSec: 15 });
  if (readable.exitCode !== 0) throw new Error('Request mount unavailable');
  const volume = await sandbox.exec('docker volume create coding-work', { timeoutSec: 15 });
  if (volume.exitCode !== 0) throw new Error('Workspace unavailable');
  const common = `--rm --pull=never --read-only --cap-drop=ALL --security-opt=no-new-privileges --memory=4g --memory-swap=4g --cpus=2 --pids-limit=512 --ulimit nofile=8192:8192 --log-driver=none --mount type=volume,src=coding-work,dst=/work --mount type=bind,src=/coding/request.json,dst=/run/job.json,readonly --tmpfs /tmp:rw,nosuid,size=536870912,mode=1777`;
  const checks = [];
  let resumePhase;
  let stopped = true;
  for (const phase of ['prepare', 'code', 'validate']) {
    if (phase === 'code' && priorCode) {
      checks.push({ command: 'code', exitCode: 0, output: priorCode.output });
      continue;
    }
    const command = phase === 'validate' ? `dotnet test ${request.testProject} --filter ${request.testFilter}` : phase;
    const phaseStarted = now();
    const remaining = checkpointCapable ? Math.floor((cutoff - phaseStarted) / 1000) : request.minutes * 60;
    if (remaining < 1) {
      if (phase !== 'prepare') resumePhase = phase;
      checks.push({ command, exitCode: 124, output: 'The sandbox time budget expired before this phase could start.' });
      break;
    }
    let bytes = 0;
    const timer = checkpointCapable ? '--verbose --signal=TERM --kill-after=3' : '--signal=KILL';
    const locale = checkpointCapable ? '--env LC_ALL=C' : '';
    const result = await sandbox.exec(`docker run ${common} ${phase === 'prepare' ? '' : '--user=65532:65532'} ${locale} --name coding-${phase} --entrypoint timeout ${request.imageId} ${timer} ${remaining} node /opt/coding/workload.mjs ${phase}`, {
      timeoutSec: remaining + (checkpointCapable ? 10 : 5),
      onStdout: x => { if ((bytes += Buffer.byteLength(x)) > 100000) throw new Error('Output limit'); },
      onStderr: x => { if ((bytes += Buffer.byteLength(x)) > 100000) throw new Error('Output limit'); }
    });
    const exitCode = result.timedOut || result.truncated ? -1 : result.exitCode ?? -1;
    stopped = !result.timedOut;
    // A generic SIGKILL can be an out-of-memory exit. Resume only when the phase ran through its allotted time.
    if (checkpointCapable && phase !== 'prepare' && [124, 137].includes(exitCode)
      && /^(?:\/usr\/bin\/|\/bin\/)?timeout: sending signal TERM to command 'node'\r?$/m.test(result.stderr ?? '')
      && now() - phaseStarted >= (remaining - 1) * 1000) resumePhase = phase;
    const output = (String(result.stdout ?? '') + String(result.stderr ?? '')).slice(-19700);
    checks.push({ command, exitCode, output: resumePhase ? `The sandbox time budget expired during ${phase}.\n${output}` : output });
    if (exitCode !== 0) break;
  }
  // Code container has exited (including descendants). Export uses a new trusted process and reads exact base diff.
  let artifact;
  let hasProgress = false;
  try {
    if (!stopped) throw new Error('Could not establish that the previous container stopped.');
    const exported = await sandbox.exec(`docker run ${common} --user=65532:65532 --network=none --entrypoint node ${request.imageId} /opt/coding/workload.mjs export`, { timeoutSec: 30 });
    if (exported.exitCode !== 0 || exported.timedOut || exported.truncated || Buffer.byteLength(exported.stdout) > maxArtifactBytes) throw new Error('Artifact unavailable');
    const { checkpointVersion: exportedVersion, hasProgress: progress, progressSummary, ...snapshot } = JSON.parse(exported.stdout);
    artifact = snapshot;
    if (artifact.baseSha !== request.baseSha || !Array.isArray(artifact.files)) throw new Error('Invalid artifact');
    if (checkpointCapable) {
      if (exportedVersion !== checkpointVersion) throw new Error('Invalid checkpoint version');
      validateCodingArtifact(artifact, request.baseSha);
      hasProgress = progress === true || artifact.files.length > 0 || (resumePhase === 'validate' && request.allowNoChanges === true);
      if (resumePhase === 'code' && typeof progressSummary === 'string' && progressSummary.trim() && progressSummary.length <= 4000)
        artifact.summary = progressSummary;
    }
  } catch {
    resumePhase = undefined;
    checks.push({ command: 'export', exitCode: 1, output: 'No bounded text artifact could be exported.' });
    artifact = { baseSha: request.baseSha, files: [], summary: 'Coding failed; inspect retained phase checks.' };
  }
  artifact.checks = checks;
  const success = checks.length === 3 && checks.every(c => c.exitCode === 0)
    && (artifact.files.length > 0 || request.allowNoChanges === true);
  return { success, artifact, ...(resumePhase && { resumePhase, hasProgress }) };
}

function provisioningError(error) {
  if (error?.name === 'RailwayAuthError' || [401, 403].includes(error?.status) || /unauthori[sz]ed|forbidden|permission denied/i.test(error?.message))
    return 'Railway denied access. Check the automation runner credential and environment.';
  if (/(checkpoint|template).*(not found|does not exist)/i.test(error?.message))
    return 'Railway could not find the coding checkpoint. Check its name in the configured environment.';
  if (error?.status === 429 || /too many sandboxes|sandbox limit/i.test(error?.message))
    return 'Railway reported a sandbox or request limit.';
  if (error?.name === 'SandboxTimeoutError') return 'Railway timed out waiting for the sandbox to start.';
  if (error?.name === 'SandboxFailedError') return 'Railway reported that the sandbox failed during startup.';
  if (Number.isInteger(error?.status) && error.status >= 100 && error.status <= 599)
    return `Railway returned a provisioning error (HTTP ${error.status}).`;
  return 'The Railway SDK could not provision the coding sandbox.';
}

export function validateRequest(r) {
  if (r.operation !== 'coding-execute' || !/^sha256:[a-f0-9]{64}$/.test(r.imageId)
    || !/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(r.repository) || !/^[a-f0-9]{40}$/.test(r.baseSha)
    || !Number.isInteger(r.minutes) || r.minutes < 5 || r.minutes > 30
    || !/^[a-f0-9]{64}$/.test(r.capability) || !/^https:\/\/[^\s"\\]+\/coding-runtime\/[a-f0-9-]+\/v1$/.test(r.gateway)
    || typeof r.instruction !== 'string' || r.instruction.length > 8000
    || (r.canAsk !== undefined && typeof r.canAsk !== 'boolean')
    || (r.allowNoChanges !== undefined && typeof r.allowNoChanges !== 'boolean')
    || (r.executionSeconds !== undefined && (!Number.isInteger(r.executionSeconds) || r.executionSeconds < 1
      || r.executionSeconds > r.minutes * 60))
    || (r.supervise !== undefined && typeof r.supervise !== 'boolean')
    || (r.ownerClarifications != null && (!Array.isArray(r.ownerClarifications) || r.ownerClarifications.length > 5
      || r.ownerClarifications.some(value => !value || typeof value.question !== 'string' || !value.question.trim() || value.question.length > 2000
        || typeof value.answer !== 'string' || !value.answer.trim() || value.answer.length > 4000)))
    || (r.followUpEvidence != null && (typeof r.followUpEvidence !== 'string' || r.followUpEvidence.length > 12000))
    || (r.responses !== undefined && typeof r.responses !== 'boolean')
    || (r.model !== undefined && (typeof r.model !== 'string' || !/^[A-Za-z0-9_./-]{1,100}$/.test(r.model)))
    || !/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+\.csproj$/.test(r.testProject) || r.testProject.includes('..')
    || typeof r.testFilter !== 'string' || r.testFilter.length > 500) throw new Error('Invalid coding request');
  if (r.resumePhase == null && r.resumeArtifact == null) return;
  if (!['code', 'validate'].includes(r.resumePhase)) throw new Error('Invalid coding resume phase.');
  validateCodingArtifact(r.resumeArtifact, r.baseSha);
  if (r.resumePhase === 'validate') {
    const checks = r.resumeArtifact.checks;
    const code = Array.isArray(checks) && checks.filter(check => check?.command === 'code');
    if (!code || code.length !== 1 || code[0].exitCode !== 0 || typeof code[0].output !== 'string' || code[0].output.length > 19700)
      throw new Error('Validation resume requires an earlier successful code phase.');
  }
}
