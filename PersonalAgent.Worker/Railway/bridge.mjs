import { Sandbox, SandboxNotFoundError } from 'railway';
import { appendFileSync, createWriteStream } from 'node:fs';
import { readFile } from 'node:fs/promises';
import { pipeline } from 'node:stream/promises';
import { pathToFileURL } from 'node:url';
import { codingDispatch } from './coding.mjs';

const imagePattern = /^sha256:[a-f0-9]{64}$/;
const packagePattern = /^([A-Za-z0-9_.-]{1,100})@([0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?)$/;

// Run after the candidate exits. The host chooses both archive paths. Never follow candidate links.
async function collectArtifacts(sandbox, destination, checkpointImage) {
  const command = `set -eu
mkdir -p /job
ulimit -f 147456
docker run --rm --network=none --read-only --cap-drop=ALL --cap-add=DAC_OVERRIDE --security-opt=no-new-privileges --user=0 --mount type=volume,src=automation-artifacts,dst=/export,readonly --entrypoint /bin/sh ${checkpointImage} -ec '
cd /export
first=$(find . -mindepth 1 -print -quit)
[ -n "$first" ] || exit 0
special=$(find . ! -type d ! -type f -print -quit)
[ -z "$special" ] || exit 2
exec tar --format=posix --hard-dereference -cf - .' > /job/export.tar
wc -c < /job/export.tar`;
  const packed = await sandbox.exec(command, { timeoutSec: 40 });
  const size = Number(packed.stdout.trim());
  const limit = 72 * 1024 * 1024;
  if (packed.exitCode !== 0 || packed.timedOut || !Number.isSafeInteger(size) || size < 0 || size > limit)
    throw new Error('Artifact export failed');
  if (size === 0) return false;
  const stream = await sandbox.files.read('/job/export.tar', { format: 'stream' });
  let received = 0;
  await pipeline(stream, async function* (chunks) {
    for await (const chunk of chunks) {
      received += chunk.length;
      if (received > size) throw new Error('Invalid artifact length');
      yield chunk;
    }
    if (received !== size) throw new Error('Incomplete artifact');
  }, createWriteStream(destination, { flags: 'wx', mode: 0o600 }));
  return true;
}

async function stageInputs(vm, files = []) {
  for (const { name, path } of files) {
    if (!/^[a-zA-Z0-9][a-zA-Z0-9._-]{0,95}$/.test(name)) throw new Error('Invalid input filename');
    await vm.files.write('/job/incoming', await readFile(path));
    // Publish a complete file atomically. Only the VM controller can write this directory.
    const result = await vm.exec(`mkdir -p /sandbox-inputs && chmod 0755 /sandbox-inputs && chmod 0444 /job/incoming && mv -f /job/incoming /sandbox-inputs/${name}`, { timeoutSec: 15 });
    if (result.exitCode !== 0) throw new Error('Input transfer failed');
  }
}

// The bridge is trusted controller code. Agent text is only ever written as file content.
export async function dispatch(request, api = Sandbox) {
  if (request.operation?.startsWith('coding-')) return codingDispatch(request, api);
  const options = { token: request.token, environmentId: request.environmentId, authType: 'bearer', verbose: false };
  if (request.operation === 'scratch-open') {
    if (!imagePattern.test(request.imageId)) throw new Error('Invalid checkpoint image');
    if (request.previousId) {
      try {
        const prior = await api.connect(request.previousId, options);
        if (prior.status === 'RUNNING' && prior.networkIsolation === 'ISOLATED')
          return { id: prior.id, fresh: false };
      } catch (error) { if (!(error instanceof SandboxNotFoundError)) throw error; }
    }
    const vm = await api.create(request.checkpoint, { ...options, networkIsolation: 'ISOLATED', idleTimeoutMinutes: 10 });
    try {
      if (vm.networkIsolation !== 'ISOLATED') throw new Error('Unexpected scratch network');
      const startup = await vm.exec(`docker volume create automation-artifacts >/dev/null && docker run --rm --network=none --cap-drop=ALL --cap-add=CHOWN --user=0 --mount type=volume,src=automation-artifacts,dst=/artifacts --entrypoint /bin/chown ${request.imageId} 65532:65532 /artifacts && docker run -d --name scratch -v /sandbox-inputs:/inputs:ro --pull=never --read-only --user=65532:65532 --cap-drop=ALL --security-opt=no-new-privileges --memory=512m --memory-swap=512m --cpus=1 --pids-limit=128 --log-driver=none --tmpfs /tmp:rw,nosuid,size=64m,mode=1777 --tmpfs /work:rw,nosuid,size=256m,mode=1777 --mount type=volume,src=automation-artifacts,dst=/artifacts --entrypoint python3 ${request.imageId} /template/scratchpad.py serve`, { timeoutSec: 20 });
      if (startup.exitCode !== 0) throw new Error('Python checkpoint unavailable');
      return { id: vm.id, fresh: true };
    } catch (error) { await vm.destroy(); throw error; }
  }
  if (request.operation === 'scratch-cell') {
    if (!imagePattern.test(request.imageId)) throw new Error('Invalid image');
    const vm = await api.connect(request.id, options);
    if (vm.networkIsolation !== 'ISOLATED') throw new Error('Unexpected scratch network');
    await stageInputs(vm, request.inputFiles);
    let bytes = 0;
    const count = chunk => {
      bytes += Buffer.byteLength(chunk);
      if (bytes > 32768) throw new Error('Cell output limit exceeded');
      if (request.progressPath) appendFileSync(request.progressPath, chunk, { mode: 0o600 });
    };
    const result = await vm.exec('printf %s "$SCRATCH_SOURCE" | base64 -d | docker exec -i -e SCRATCH_SKIP_EMPTY=1 scratch python3 /template/scratchpad.py submit',
      { env: { SCRATCH_SOURCE: Buffer.from(request.source).toString('base64') }, timeoutSec: 30, onStdout: count, onStderr: count });
    if (!result.timedOut && !result.truncated && result.exitCode === 3)
      return { ...result, exitCode: 0, status: 'Completed', hasArtifacts: false };
    if (!result.timedOut && !result.truncated && result.exitCode === 42)
      return { ...result, status: 'PythonError' };
    if (result.timedOut || result.truncated || result.exitCode !== 0)
      return { ...result, status: result.timedOut ? 'TimedOut' : 'ExecutionFailed' };
    let hasArtifacts = false;
    if (request.artifactDestination) {
      // Freeze all candidate processes while the trusted exporter walks the volume.
      if ((await vm.exec('docker pause scratch', { timeoutSec: 5 })).exitCode !== 0) throw new Error('Freeze failed');
      try { hasArtifacts = await collectArtifacts(vm, request.artifactDestination, request.imageId); }
      finally {
        if ((await vm.exec('docker unpause scratch', { timeoutSec: 5 })).exitCode !== 0) throw new Error('Resume failed');
      }
    }
    return { ...result, status: 'Completed', hasArtifacts };
  }
  if (request.operation === 'create') {
    const sandbox = await api.create(request.checkpoint, {
      ...options, environmentId: request.environmentId, networkIsolation: 'ISOLATED', idleTimeoutMinutes: request.customImage ? 17 : 5,
    });
    return { id: sandbox.id };
  }
  if (request.operation === 'destroy') {
    try {
      const sandbox = await api.connect(request.id, options);
      if (sandbox.status !== 'DESTROYED') await sandbox.destroy();
    } catch (error) {
      if (!(error instanceof SandboxNotFoundError)) throw error;
    }
    return { destroyed: true };
  }
  if (request.operation !== 'execute' || !imagePattern.test(request.imageId)) throw new Error('Invalid operation');
  const sandbox = await api.connect(request.id, options);
  if (sandbox.networkIsolation !== 'ISOLATED') throw new Error('Unexpected sandbox network');
  await stageInputs(sandbox, request.inputFiles);
  const language = request.language ?? 'csharp';
  if (!['shell', 'csharp', 'dockerfile', 'python'].includes(language)) throw new Error('Unsupported program language');
  if (language !== 'csharp' && (request.packages?.length || request.packageLock)) throw new Error('Unexpected shell dependencies');
  const references = (request.packages ?? []).map(p => {
    const match = packagePattern.exec(p);
    if (!match) throw new Error('Invalid package');
    return `<PackageReference Include="${match[1]}" Version="[${match[2]}]" />`;
  }).join('');
  if (references && !request.packageLock) throw new Error('Package lock required');
  const project = `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><EnableNETAnalyzers>false</EnableNETAnalyzers><EnableDefaultCompileItems>false</EnableDefaultCompileItems><UseSharedCompilation>false</UseSharedCompilation></PropertyGroup><ItemGroup><Compile Include="/work/Program.cs" />${references}</ItemGroup></Project>`;
  const script = `#!/bin/sh
set -eu
cd /work
if ! dotnet restore Program.csproj --configfile /work/NuGet.Config ${references ? '--locked-mode' : ''} > /work/build.log 2>&1 || ! dotnet build Program.csproj --no-restore --nologo -v:q -o /work/out >> /work/build.log 2>&1; then
  head -c 30000 /work/build.log >&2
  exit 200
fi
exec dotnet /work/out/Program.dll < /work/input.txt
`;
  const customImage = language === 'dockerfile';
  const files = customImage ? { Dockerfile: request.source, 'input.txt': request.input } : language === 'python' ? {
    'program.py': request.source, 'input.txt': request.input, 'run.sh': 'exec python3 /template/scratchpad.py once /work/program.py < /work/input.txt',
  } : language === 'shell' ? {
    'program.sh': request.source, 'input.txt': request.input, 'run.sh': 'exec /bin/sh /work/program.sh < /work/input.txt',
  } : {
    'Program.cs': request.source, 'input.txt': request.input, 'Program.csproj': project, 'run.sh': script,
    'NuGet.Config': '<configuration><packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>',
  };
  if (references) files['packages.lock.json'] = request.packageLock;
  for (const [name, value] of Object.entries(files)) await sandbox.files.write(`/job/${name}`, value);
  // Only this short-lived capability enters the VM. Railway/database/Jev credentials never do.
  if (!/^https:\/\/[^\r\n]+$/.test(request.gatewayUrl) || !/^[a-f0-9]{64}$/.test(request.gatewayToken)) throw new Error('Invalid gateway');
  await sandbox.files.write('/job/runtime.env', `AUTOMATION_GATEWAY_URL=${request.gatewayUrl}\nAUTOMATION_GATEWAY_TOKEN=${request.gatewayToken}\n`);
  const host = await sandbox.exec('docker info --format "{{json .}}"', { timeoutSec: 15 });
  const info = JSON.parse(host.stdout);
  if (host.exitCode !== 0 || info.OSType !== 'linux' || !['MemoryLimit', 'SwapLimit', 'CpuCfsQuota', 'PidsLimit'].every(k => info[k])
    || !info.SecurityOptions?.some(v => /^name=seccomp,profile=(builtin|default)$/.test(v))) throw new Error('Resource limits unavailable');
  let imageId = request.imageId;
  if (customImage) {
    // Dockerfile on stdin creates an empty build context. No job inputs or gateway credentials enter the build.
    const build = await sandbox.exec('timeout -s KILL 480 docker build --iidfile /job/image.id - < /job/Dockerfile > /job/build.log 2>&1 || { status=$?; tail -c 16000 /job/build.log >&2; exit "$status"; }', { timeoutSec: 485 });
    if (build.exitCode !== 0 || build.timedOut)
      return { status: build.timedOut || [124, 137].includes(build.exitCode) ? 'TimedOut' : 'BuildFailed', exitCode: build.exitCode, stdout: '', stderr: (build.stderr ?? '').slice(-16000) };
    const identity = await sandbox.exec('cat /job/image.id', { timeoutSec: 10 });
    imageId = identity.stdout.trim();
    if (identity.exitCode !== 0 || !imagePattern.test(imageId)) throw new Error('Invalid built image identity');
  }
  // Docker copies image-baked /artifacts into the fresh volume when it creates the seed container.
  // The seed is never started. The named volume survives both containers for offline collection.
  const prepare = `docker volume create automation-artifacts >/dev/null && docker create --name artifact-seed --mount type=volume,src=automation-artifacts,dst=/artifacts --entrypoint /bin/true ${imageId} >/dev/null && docker rm -v artifact-seed >/dev/null && docker run --rm --network=none --read-only --cap-drop=ALL --cap-add=CHOWN --user=0 --mount type=volume,src=automation-artifacts,dst=/export --entrypoint /bin/chown ${request.imageId} 65532:65532 /export`;
  const runner = `docker run --rm -i --name automation-program -v /sandbox-inputs:/inputs:ro --pull=never --mount type=volume,src=automation-artifacts,dst=/artifacts --no-healthcheck --read-only --user=65532:65532 --cap-drop=ALL --security-opt=no-new-privileges --memory=512m --memory-swap=512m --cpus=1 --pids-limit=128 --ulimit nofile=4096:4096 --log-driver=none --tmpfs ${customImage ? '/scratch' : '/work'}:rw,noexec,nosuid,size=268435456,mode=1777 --tmpfs /tmp:rw,noexec,nosuid,size=16777216,mode=1777 --env-file /job/runtime.env`;
  const command = customImage
    ? `timeout -s KILL 300 ${runner} ${imageId} < /job/input.txt`
    : `tar -C /job -cf - ${Object.keys(files).join(' ')} | ${runner} --entrypoint timeout ${imageId} --signal=KILL 95 /bin/sh -c 'cd /work && tar --no-same-owner -xf - && /bin/sh /work/run.sh'`;
  let bytes = 0;
  const count = chunk => { if ((bytes += Buffer.byteLength(chunk)) > 32768) throw new Error('OutputLimitExceeded'); };
  try {
    const result = await sandbox.exec(prepare + ' && ' + command, { timeoutSec: customImage ? 305 : 100, onStdout: count, onStderr: count });
    let hasArtifacts = false;
    if (request.artifactDestination && !result.truncated && !result.timedOut && result.exitCode === 0) {
      try { hasArtifacts = await collectArtifacts(sandbox, request.artifactDestination, request.imageId); }
      catch { return { imageId, status: 'ArtifactExportFailed', exitCode: 0, stdout: result.stdout, stderr: 'Could not export /artifacts. Use regular files within the artifact budget.' }; }
    }
    return { ...result, imageId, hasArtifacts, status: result.truncated ? 'OutputLimitExceeded' : (result.timedOut || (customImage && [124, 137].includes(result.exitCode))) ? 'TimedOut' : result.exitCode === 0 ? 'Completed' : language === 'csharp' && result.exitCode === 200 ? 'BuildFailed' : 'ExecutionFailed' };
  } catch (error) {
    if (error.message === 'OutputLimitExceeded') return { imageId, status: 'OutputLimitExceeded', stdout: '', stderr: '', exitCode: null };
    throw error;
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    let input = '';
    for await (const chunk of process.stdin) {
      input += chunk;
      if (input.length > 300000) throw new Error('Input limit');
    }
    const result = await dispatch(JSON.parse(input));
    process.stdout.write(JSON.stringify(result));
  } catch {
    // SDK errors may include request content. Never forward them to process logs or diagnostics.
    process.stderr.write('Railway sandbox operation failed.');
    process.exitCode = 1;
  }
}
