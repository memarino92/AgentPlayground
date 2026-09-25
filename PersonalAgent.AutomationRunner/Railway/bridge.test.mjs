import test from 'node:test';
import assert from 'node:assert/strict';
import { dispatch } from './bridge.mjs';
import { loadPreparationFiles } from './preparation-files.mjs';

test('operator preparation resolves the checked-in compiler payload from any working directory', async () => {
  const files = new Map(await loadPreparationFiles());
  assert.equal(files.size, 4);
  assert.ok(files.get('Dockerfile.automation-sandbox').includes('@sha256:'));
  assert.ok(files.get('automation-sandbox/Program.csproj').includes('<TargetFramework>net11.0</TargetFramework>'));
  assert.ok(files.get('automation-sandbox/run.sh').includes('dotnet build Program.csproj'));
});

function fake() {
  const writes = [], commands = [], connections = [];
  const sandbox = { id: 'sandbox-id', status: 'RUNNING', networkIsolation: 'ISOLATED',
    files: { write: async (path, value) => writes.push([path, value]) },
    exec: async (command, options) => {
      commands.push([command, options]);
      if (command.startsWith('docker info')) return { exitCode: 0, stdout: JSON.stringify({ OSType: 'linux', MemoryLimit: true, SwapLimit: true, CpuCfsQuota: true, PidsLimit: true, SecurityOptions: ['name=seccomp,profile=builtin'] }) };
      options.onStdout('hello');
      return { exitCode: 0, stdout: 'hello', stderr: '', timedOut: false, truncated: false };
    }, destroy: async () => { sandbox.status = 'DESTROYED'; },
  };
  const api = { create: async (...args) => { connections.push(args); return sandbox; }, connect: async (...args) => { connections.push(args); return sandbox; } };
  return { api, sandbox, writes, commands, connections };
}
const request = { operation: 'execute', token: 'management-secret', environmentId: 'environment-id', id: 'sandbox-id', imageId: 'sha256:' + 'a'.repeat(64),
  source: 'Console.Write("$(do-not-execute)");', input: 'private input', gatewayUrl: 'https://api.example.test/automation-runtime/run/0/tools', gatewayToken: 'b'.repeat(64) };

test('create always uses isolated networking, finite idle lifetime and explicit environment', async () => {
  const f = fake();
  assert.deepEqual(await dispatch({ operation: 'create', token: 'secret', checkpoint: 'base-v1', environmentId: 'env' }, f.api), { id: 'sandbox-id' });
  assert.equal(f.connections[0][0], 'base-v1');
  assert.equal(f.connections[0][1].networkIsolation, 'ISOLATED');
  assert.equal(f.connections[0][1].idleTimeoutMinutes, 5);
  assert.equal(f.connections[0][1].environmentId, 'env');
  assert.equal(f.connections[0][1].env, undefined);
});
test('source is file content; Internet enabled; management token never enters VM', async () => {
  const f = fake(); const result = await dispatch(request, f.api);
  assert.equal(result.status, 'Completed');
  assert.equal(f.connections[0][1].environmentId, 'environment-id');
  assert.equal(f.writes.find(([path]) => path === '/job/Program.cs')[1], request.source);
  const boundary = JSON.stringify([f.writes, f.commands]);
  assert.ok(!boundary.includes('management-secret'));
  assert.ok(!f.commands.at(-1)[0].includes('do-not-execute'));
  assert.ok(!f.commands.at(-1)[0].includes('--network=none'));
  for (const flag of ['--read-only', '--memory=512m', '--pids-limit=128', '--pull=never', '--signal=KILL 95']) assert.ok(f.commands.at(-1)[0].includes(flag));
});
test('unknown operations and agent-selected image expressions are rejected', async () => {
  const f = fake();
  await assert.rejects(dispatch({ ...request, imageId: 'image; malicious' }, f.api));
  await assert.rejects(dispatch({ ...request, operation: 'shell' }, f.api));
  assert.equal(f.commands.length, 0);
});
test('packages require exact versions and a lock; locked restore has a fixed feed', async () => {
  const f = fake();
  await assert.rejects(dispatch({ ...request, packages: ['Newtonsoft.Json@latest'] }, f.api));
  await assert.rejects(dispatch({ ...request, packages: ['Newtonsoft.Json@13.0.3'] }, f.api));
  await dispatch({ ...request, packages: ['Newtonsoft.Json@13.0.3'], packageLock: '{"version":1}' }, f.api);
  assert.ok(f.writes.find(([p]) => p === '/job/run.sh')[1].includes('--locked-mode'));
  assert.ok(f.writes.find(([p]) => p === '/job/Program.csproj')[1].includes('Version="[13.0.3]"'));
});
test('refuses private networking and missing resource enforcement', async () => {
  const f = fake(); f.sandbox.networkIsolation = 'PRIVATE';
  await assert.rejects(dispatch(request, f.api));
  f.sandbox.networkIsolation = 'ISOLATED';
  f.sandbox.exec = async () => ({ exitCode: 0, stdout: '{}' });
  await assert.rejects(dispatch(request, f.api));
});
test('output flood is bounded, and destroy is idempotent for destroyed VM', async () => {
  const f = fake(); const exec = f.sandbox.exec;
  f.sandbox.exec = (command, options) => command.startsWith('docker info') ? exec(command, options) : Promise.resolve().then(() => options.onStdout('x'.repeat(32769)));
  assert.equal((await dispatch(request, f.api)).status, 'OutputLimitExceeded');
  await dispatch({ operation: 'destroy', id: 'sandbox-id', token: 'secret' }, f.api);
  await dispatch({ operation: 'destroy', id: 'sandbox-id', token: 'secret' }, f.api);
  assert.equal(f.sandbox.status, 'DESTROYED');
});
