import test from 'node:test';
import assert from 'node:assert/strict';
import { codingDispatch, validateRequest } from './coding.mjs';
import { loadCodingPreparationFiles } from './coding-preparation-files.mjs';

test('coding preparation loads the repository Dockerfile and immutable workload before provisioning', async () => {
  const files = new Map(await loadCodingPreparationFiles());
  assert.equal(files.size, 2);
  assert.ok(files.get('Dockerfile.coding-sandbox').includes('opencode-ai@1.18.32'));
  assert.ok(files.get('PersonalAgent.AutomationRunner/Railway/workload.mjs').includes("phase === 'export'"));
});

const request = { operation: 'coding-execute', imageId: 'sha256:' + 'a'.repeat(64), repository: 'owner/repo',
  baseSha: 'b'.repeat(40), minutes: 20, capability: 'c'.repeat(64), gateway: 'https://api.example.test/coding-runtime/1234/v1',
  instruction: 'Do not execute $(payload)', testProject: 'Tests/Tests.csproj', testFilter: 'FullyQualifiedName~Unit', token: 'controller-secret' };
test('rejects shell interpolation in controller parameters', () => {
  for (const [key, value] of [['imageId', 'image;id'], ['repository', 'owner/repo;id'], ['minutes', '20;id'], ['baseSha', 'HEAD;id'], ['testProject', '../Tests.csproj']]) {
    assert.throws(() => validateRequest({ ...request, [key]: value }));
  }
  validateRequest(request);
});
test('coding runs use isolated unprivileged containers without publishing or provider credentials', async () => {
  const commands = [], writes = [];
  const sandbox = { networkIsolation: 'ISOLATED', files: { write: async (...args) => writes.push(args) }, exec: async (command) => {
    commands.push(command);
    if (command.startsWith('docker info')) return { exitCode: 0, stdout: JSON.stringify({ MemoryLimit: true, SwapLimit: true, CpuCfsQuota: true, PidsLimit: true, SecurityOptions: ['name=seccomp,profile=builtin'] }) };
    if (command.endsWith(' export')) return { exitCode: 0, stdout: JSON.stringify({ baseSha: request.baseSha, files: [{ path: 'a.cs', content: 'hi' }], summary: 'changed' }) };
    return { exitCode: 0, stdout: 'passed', stderr: '' };
  }};
  const result = await codingDispatch(request, { connect: async () => sandbox });
  assert.equal(result.success, true);
  assert.equal(result.artifact.checks.length, 3);
  assert.ok(commands.some(c => c.includes('--user=65532:65532') && c.endsWith(' code')));
  assert.ok(commands.some(c => c.includes('--network=none') && c.endsWith(' export')));
  assert.ok(!JSON.stringify(writes).includes('controller-secret'));
  assert.ok(!commands.join('\n').includes('$(payload)'));
  assert.equal(JSON.parse(writes[0][1]).instruction, request.instruction);
});

test('failed coding retains phase diagnostics even when no diff can be exported', async () => {
  const sandbox = { networkIsolation: 'ISOLATED', files: { write: async () => {} }, exec: async command => {
    if (command.startsWith('docker info')) return { exitCode: 0, stdout: JSON.stringify({ MemoryLimit: true, SwapLimit: true, CpuCfsQuota: true, PidsLimit: true, SecurityOptions: ['name=seccomp,profile=builtin'] }) };
    if (command.endsWith(' export')) return { exitCode: 1, stdout: '', stderr: 'No changes' };
    if (command.endsWith(' code')) return { exitCode: 1, stdout: '', stderr: 'Model unavailable' };
    return { exitCode: 0, stdout: '', stderr: '' };
  }};
  const result = await codingDispatch(request, { connect: async () => sandbox });
  assert.equal(result.success, false);
  assert.equal(result.artifact.files.length, 0);
  assert.equal(result.artifact.checks.find(c => c.command === 'code').output, 'Model unavailable');
});
