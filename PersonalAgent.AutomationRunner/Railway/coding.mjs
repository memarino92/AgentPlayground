// Trusted controller adapter; workload containers never receive the Railway token or Docker socket.
export async function codingDispatch(request, api) {
  const options = { token: request.token, environmentId: request.environmentId, authType: 'bearer', verbose: false };
  if (request.operation === 'coding-create') {
    if (!/^[A-Za-z0-9_.-]{1,128}$/.test(request.checkpoint)) throw new Error('Invalid checkpoint');
    const sandbox = await api.create(request.checkpoint, { ...options, networkIsolation: 'ISOLATED', idleTimeoutMinutes: 35 });
    return { id: sandbox.id };
  }
  validateRequest(request);
  const sandbox = await api.connect(request.id, options);
  if (sandbox.networkIsolation !== 'ISOLATED') throw new Error('Unexpected network');
  const infoResult = await sandbox.exec('docker info --format "{{json .}}"', { timeoutSec: 15 });
  const info = JSON.parse(infoResult.stdout);
  if (infoResult.exitCode !== 0 || !['MemoryLimit', 'SwapLimit', 'CpuCfsQuota', 'PidsLimit'].every(k => info[k])
    || !info.SecurityOptions?.some(v => /^name=seccomp,profile=(builtin|default)$/.test(v))) throw new Error('Resource limits unavailable');
  // Inputs are JSON file data, never shell fragments. Only validated image/duration enter commands.
  await sandbox.files.write('/coding/request.json', JSON.stringify({ repository: request.repository, baseSha: request.baseSha,
    instruction: request.instruction, gateway: request.gateway, capability: request.capability,
    testProject: request.testProject, testFilter: request.testFilter }));
  const volume = await sandbox.exec('docker volume create coding-work', { timeoutSec: 15 });
  if (volume.exitCode !== 0) throw new Error('Workspace unavailable');
  const common = `--rm --pull=never --read-only --cap-drop=ALL --security-opt=no-new-privileges --memory=4g --memory-swap=4g --cpus=2 --pids-limit=512 --ulimit nofile=8192:8192 --log-driver=none --mount type=volume,src=coding-work,dst=/work --mount type=bind,src=/coding/request.json,dst=/run/job.json,readonly --tmpfs /tmp:rw,nosuid,size=536870912,mode=1777`;
  const checks = [];
  for (const phase of ['prepare', 'code', 'validate']) {
    let bytes = 0;
    const result = await sandbox.exec(`docker run ${common} ${phase === 'prepare' ? '' : '--user=65532:65532'} --name coding-${phase} --entrypoint timeout ${request.imageId} --signal=KILL ${request.minutes * 60} node /opt/coding/workload.mjs ${phase}`, {
      timeoutSec: request.minutes * 60 + 5,
      onStdout: x => { if ((bytes += Buffer.byteLength(x)) > 100000) throw new Error('Output limit'); },
      onStderr: x => { if ((bytes += Buffer.byteLength(x)) > 100000) throw new Error('Output limit'); }
    });
    checks.push({ command: phase === 'validate' ? `dotnet test ${request.testProject} --filter ${request.testFilter}` : phase,
      exitCode: result.exitCode ?? -1, output: (result.stdout + result.stderr).slice(-20000) });
    if (result.exitCode !== 0 || result.timedOut || result.truncated) break;
  }
  // Code container has exited (including descendants). Export uses a new trusted process and reads exact base diff.
  let artifact;
  try {
    const exported = await sandbox.exec(`docker run ${common} --user=65532:65532 --network=none --entrypoint node ${request.imageId} /opt/coding/workload.mjs export`, { timeoutSec: 30 });
    if (exported.exitCode !== 0 || exported.truncated || exported.stdout.length > 1000000) throw new Error('Artifact unavailable');
    artifact = JSON.parse(exported.stdout);
  } catch {
    checks.push({ command: 'export', exitCode: 1, output: 'No bounded text artifact could be exported.' });
    artifact = { baseSha: request.baseSha, files: [], summary: 'Coding failed; inspect retained phase checks.' };
  }
  artifact.checks = checks;
  return { success: checks.length === 3 && checks.every(c => c.exitCode === 0), artifact };
}

export function validateRequest(r) {
  if (r.operation !== 'coding-execute' || !/^sha256:[a-f0-9]{64}$/.test(r.imageId)
    || !/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(r.repository) || !/^[a-f0-9]{40}$/.test(r.baseSha)
    || !Number.isInteger(r.minutes) || r.minutes < 5 || r.minutes > 30
    || !/^[a-f0-9]{64}$/.test(r.capability) || !/^https:\/\/[^\s"\\]+\/coding-runtime\/[a-f0-9-]+\/v1$/.test(r.gateway)
    || typeof r.instruction !== 'string' || r.instruction.length > 8000
    || !/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+\.csproj$/.test(r.testProject) || r.testProject.includes('..')
    || typeof r.testFilter !== 'string' || r.testFilter.length > 500) throw new Error('Invalid coding request');
}
