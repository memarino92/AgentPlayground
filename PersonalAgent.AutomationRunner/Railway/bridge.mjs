import { Sandbox, SandboxNotFoundError } from 'railway';
import { pathToFileURL } from 'node:url';

const imagePattern = /^sha256:[a-f0-9]{64}$/;
const packagePattern = /^([A-Za-z0-9_.-]{1,100})@([0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?)$/;

// The bridge is trusted controller code. Agent text is only ever written as file content.
export async function dispatch(request, api = Sandbox) {
  const options = { token: request.token, environmentId: request.environmentId, authType: 'bearer', verbose: false };
  if (request.operation === 'create') {
    const sandbox = await api.create(request.checkpoint, {
      ...options, environmentId: request.environmentId, networkIsolation: 'ISOLATED', idleTimeoutMinutes: 5,
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
  const files = {
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
  // Immutable image must already be present in the administrator-prepared checkpoint; no agent-selected pulls.
  const command = `tar -C /job -cf - ${Object.keys(files).join(' ')} | docker run --rm -i --name automation-program --pull=never --read-only --user=65532:65532 --cap-drop=ALL --security-opt=no-new-privileges --memory=512m --memory-swap=512m --cpus=1 --pids-limit=128 --ulimit nofile=256:256 --log-driver=none --tmpfs /work:rw,noexec,nosuid,size=268435456,mode=1777 --tmpfs /tmp:rw,noexec,nosuid,size=16777216,mode=1777 --env-file /job/runtime.env --entrypoint timeout ${request.imageId} --signal=KILL 95 /bin/sh -c 'cd /work && tar --no-same-owner -xf - && /bin/sh /work/run.sh'`;
  let bytes = 0;
  const count = chunk => { if ((bytes += Buffer.byteLength(chunk)) > 32768) throw new Error('OutputLimitExceeded'); };
  try {
    const result = await sandbox.exec(command, { timeoutSec: 100, onStdout: count, onStderr: count });
    return { ...result, status: result.truncated ? 'OutputLimitExceeded' : result.timedOut ? 'TimedOut' : result.exitCode === 0 ? 'Completed' : result.exitCode === 200 ? 'BuildFailed' : 'ExecutionFailed' };
  } catch (error) {
    if (error.message === 'OutputLimitExceeded') return { status: 'OutputLimitExceeded', stdout: '', stderr: '', exitCode: null };
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
