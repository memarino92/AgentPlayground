import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import assert from 'node:assert/strict';
import { dispatch } from './bridge.mjs';
import { loadPreparationFiles } from './preparation-files.mjs';

test('operator preparation resolves the checked-in compiler payload from any working directory', async () => {
  const files = new Map(await loadPreparationFiles());
  assert.equal(files.size, 5);
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
  assert.ok(!f.commands.at(-1)[0].split('docker run --rm').at(-1).includes('--network=none'));
  for (const flag of ['--read-only', '--memory=512m', '--pids-limit=128', '--ulimit nofile=4096:4096', '--pull=never', '--signal=KILL 95']) assert.ok(f.commands.at(-1)[0].includes(flag));
});
test('shell scripts run inside the bounded container and receive input separately', async () => {
  const f = fake();
  await dispatch({ ...request, language: 'shell', source: 'wc -w' }, f.api);
  assert.equal(new Map(f.writes).get('/job/program.sh'), 'wc -w');
  assert.match(new Map(f.writes).get('/job/run.sh'), /program.sh < \/work\/input.txt/);
  assert.ok(!f.commands.at(-1)[0].includes('wc -w'));
});

test('custom image builds with an empty context and runs the resolved image ID', async () => {
  const f = fake();
  const exec = f.sandbox.exec;
  const imageId = 'sha256:' + 'c'.repeat(64);
  const phases = [];
  f.sandbox.exec = async (command, options) => {
    phases.push(command);
    if (command.includes('docker build')) return { exitCode: 0 };
    if (command === 'cat /job/image.id') return { exitCode: 0, stdout: imageId };
    return exec(command, options);
  };
  const result = await dispatch({ ...request, language: 'dockerfile', source: 'FROM alpine' }, f.api);
  assert.equal(result.imageId, imageId);
  assert.equal(new Map(f.writes).get('/job/Dockerfile'), 'FROM alpine');
  assert.match(phases[1], /docker build --iidfile \/job\/image.id - < \/job\/Dockerfile/);
  assert.ok(phases.at(-1).endsWith(imageId + ' < /job/input.txt'));
  assert.ok(!phases.at(-1).split('docker run --rm').at(-1).includes('--entrypoint'));
  assert.ok(!phases.join(' ').includes(request.source));
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

test('binary artifact transfer bypasses console output and rejects partial downloads', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'railway-artifacts-'));
  try {
    const bytes = Buffer.alloc(100000, 0xff);
    const f = fake();
    const exec = f.sandbox.exec;
    f.sandbox.exec = (command, options) => command.includes('tar --format=posix')
      ? { stdout: String(bytes.length), exitCode: 0 } : exec(command, options);
    f.sandbox.files.read = async (path, options) => {
      assert.equal(path, '/job/export.tar');
      assert.equal(options.format, 'stream');
      return new Blob([bytes]).stream();
    };
    const artifactDestination = join(directory, 'complete.tar');
    const result = await dispatch({ ...request, artifactDestination }, f.api);
    assert.equal(result.hasArtifacts, true);
    assert.deepEqual(await readFile(artifactDestination), bytes);
    f.sandbox.files.read = async () => new Blob([bytes.subarray(1)]).stream();
    const short = await dispatch({ ...request, artifactDestination: join(directory, 'short.tar') }, f.api);
    assert.equal(short.status, 'ArtifactExportFailed');
  } finally { await rm(directory, { recursive: true, force: true }); }
});

test('background and warm programs receive binary input through the controller-owned mount', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'sandbox-input-'));
  try {
    const path = join(dir, 'private');
    const bytes = Buffer.from([0, 255, 42]);
    await writeFile(path, bytes);
    for (const operation of ['execute', 'scratch-cell']) {
      const f = fake();
      const exec = f.sandbox.exec;
      f.sandbox.exec = (command, options) => command.startsWith('mkdir') ? Promise.resolve({ exitCode: 0 }) : exec(command, options);
      await dispatch({ ...request, operation, inputFiles: [{ name: 'data.bin', path }] }, f.api);
      assert.deepEqual(new Map(f.writes).get('/job/incoming'), bytes);
      await assert.rejects(dispatch({ ...request, operation, inputFiles: [{ name: '../escape', path }] }, f.api), /Invalid input filename/);
      if (operation === 'execute') assert.match(f.commands.at(-1)[0], /\/sandbox-inputs:\/inputs:ro/);
    }
  } finally { await rm(dir, { recursive: true, force: true }); }
});

test('warm Python uses the existing isolated VM, streams text, and skips empty artifact exports', async () => {
  const f = fake();
  const prior = await dispatch({ ...request, operation: 'scratch-open', previousId: f.sandbox.id }, f.api);
  assert.deepEqual(prior, { id: f.sandbox.id, fresh: false });
  assert.equal(f.commands.length, 0);
  const dir = await mkdtemp(join(tmpdir(), 'scratch-proof-'));
  try {
    const progressPath = join(dir, 'cell.log');
    f.sandbox.exec = async (command, options) => {
      assert.match(command, /docker exec -i/);
      assert.equal(Buffer.from(options.env.SCRATCH_SOURCE, 'base64').toString(), 'total * 2');
      options.onStdout('42');
      return { exitCode: 3, stdout: '42', stderr: '' };
    };
    const result = await dispatch({ ...request, operation: 'scratch-cell', source: 'total * 2', progressPath }, f.api);
    assert.equal(result.status, 'Completed');
    assert.equal(result.hasArtifacts, false);
    assert.equal(await readFile(progressPath, 'utf8'), '42');
    f.sandbox.networkIsolation = 'PRIVATE';
    await assert.rejects(dispatch({ ...request, operation: 'scratch-cell' }, f.api));
  } finally { await rm(dir, { recursive: true, force: true }); }
});

test('Python exports an artifact from a scratchpad with no staged inputs', async () => {
  const f = fake();
  const directory = await mkdtemp(join(tmpdir(), 'scratch-export-'));
  try {
    const bytes = Buffer.from('test archive');
    const phases = [];
    f.sandbox.exec = async command => {
      if (command.startsWith('printf')) return { exitCode: 0, stdout: 'Saved chaos.gif', stderr: '' };
      if (command === 'docker pause scratch' || command === 'docker unpause scratch') {
        phases.push(command);
        return { exitCode: 0 };
      }
      phases.push('pack');
      assert.match(command, /\nmkdir -p \/job\n[\s\S]*> \/job\/export\.tar/);
      return { exitCode: 0, stdout: String(bytes.length) };
    };
    f.sandbox.files.read = async path => {
      assert.equal(path, '/job/export.tar');
      phases.push('download');
      return new Blob([bytes]).stream();
    };

    const artifactDestination = join(directory, 'cell.tar');
    const result = await dispatch({ ...request, operation: 'scratch-cell', artifactDestination }, f.api);

    assert.equal(result.status, 'Completed');
    assert.equal(result.hasArtifacts, true);
    assert.deepEqual(await readFile(artifactDestination), bytes);
    assert.deepEqual(phases, ['docker pause scratch', 'pack', 'download', 'docker unpause scratch']);
    assert.deepEqual(f.writes, []);
  } finally { await rm(directory, { recursive: true, force: true }); }
});
