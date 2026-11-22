import test from 'node:test';
import assert from 'node:assert/strict';
import { codingDispatch, validateRequest } from './coding.mjs';
import { loadCodingPreparationFiles } from './coding-preparation-files.mjs';

test('coding preparation loads the repository Dockerfile and immutable workload before provisioning', async () => {
  const files = new Map(await loadCodingPreparationFiles());
  assert.equal(files.size, 6);
  assert.ok(files.get('Dockerfile.coding-sandbox').includes('@opencode/cli@2.3.2'));
  assert.ok(files.has('PersonalAgent.Worker/Railway/coding-questions.mjs'));
  assert.ok(files.has('PersonalAgent.Worker/Railway/coding-checkpoint.mjs'));
  assert.ok(files.has('PersonalAgent.Worker/Railway/coding-supervision.mjs'));
  assert.ok(files.has('PersonalAgent.Worker/Railway/coding-rebase.mjs'));
  assert.ok(files.get('PersonalAgent.Worker/Railway/workload.mjs').includes("phase === 'export'"));
});

const request = { operation: 'coding-execute', imageId: 'sha256:' + 'a'.repeat(64), repository: 'owner/repo',
  baseSha: 'b'.repeat(40), minutes: 20, capability: 'c'.repeat(64), gateway: 'https://api.example.test/coding-runtime/1234/v1',
  instruction: 'Do not execute $(payload)', testProject: 'Tests/Tests.csproj', testFilter: 'FullyQualifiedName~Unit', token: 'controller-secret' };
test('coding provisioning uses a supported idle timeout and keeps provider details private', async () => {
  const create = { operation: 'coding-create', checkpoint: 'coding-v1' };
  assert.deepEqual(await codingDispatch(create, { create: async (checkpoint, options) => {
    assert.equal(checkpoint, create.checkpoint);
    assert.equal(options.idleTimeoutMinutes, 5);
    assert.equal(options.networkIsolation, 'ISOLATED');
    return { id: 'sandbox-id' };
  } }), { id: 'sandbox-id' });
  for (const [status, message, expected] of [[200, 'Checkpoint private-name not found', /checkpoint/],
    [403, 'secret-token', /denied access/], [503, 'secret-token', /HTTP 503/]]) {
    const result = await codingDispatch(create, { create: async () => { throw Object.assign(new Error(message), { status }); } });
    assert.match(result.provisioningError, expected);
    assert.ok(!result.provisioningError.includes(message));
  }
});

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

test('tracked rebases require the new image and a commit series even after full validation', async () => {
  const expectedBranchHead = 'e'.repeat(40);
  const execute = (supported, includeSeries) => codingDispatch({ ...request, expectedBranchHead, executionSeconds: 1000 }, {
    connect: async () => ({
      networkIsolation: 'ISOLATED', files: { write: async () => {} }, exec: async command => {
        if (command.startsWith('docker info')) return { exitCode: 0, stdout: JSON.stringify({ MemoryLimit: true, SwapLimit: true,
          CpuCfsQuota: true, PidsLimit: true, SecurityOptions: ['name=seccomp,profile=builtin'] }) };
        if (command.includes('io.agentplayground.coding-rebase')) return { exitCode: 0, stdout: supported ? '1' : '' };
        if (command.includes('io.agentplayground.coding-checkpoint')) return { exitCode: 0, stdout: '1' };
        if (command.endsWith(' export')) return { exitCode: 0, stdout: JSON.stringify({
          baseSha: request.baseSha, files: [], summary: 'Original change is already present in the base.', checkpointVersion: 1,
          rebaseCommits: includeSeries ? [{ originalSha: expectedBranchHead, files: [] }] : undefined
        }) };
        return { exitCode: 0, stdout: '' };
      }
    })
  });
  assert.match((await execute(false, true)).artifact.summary, /checkpoint with tracked rebase support/);
  assert.equal((await execute(true, false)).success, false);
  assert.equal((await execute(true, true)).success, true);
});

test('an empty exported diff succeeds only with authorization and full validation', async () => {
  const execute = (options, validation, exported) => codingDispatch({ ...request, ...options }, {
    connect: async () => ({
      networkIsolation: 'ISOLATED', files: { write: async () => {} },
      exec: async command => {
        if (command.startsWith('docker info')) return { exitCode: 0, stdout: JSON.stringify({ MemoryLimit: true, SwapLimit: true,
          PidsLimit: true, CpuCfsQuota: true, SecurityOptions: ['name=seccomp,profile=builtin'] }) };
        if (command.endsWith(' export')) return { exitCode: 0, stdout: JSON.stringify({ baseSha: request.baseSha, files: [],
          summary: 'Verification complete' }), ...exported };
        return { exitCode: 0, stdout: '', stderr: '', ...(command.endsWith(' validate') ? validation : {}) };
      }
    })
  });
  const allowed = { allowNoChanges: true };
  const verified = await execute(allowed);
  assert.equal(verified.success, true);
  assert.deepEqual(verified.artifact.files, []);
  assert.equal(verified.artifact.summary, 'Verification complete');
  assert.equal(verified.artifact.checks.length, 3);
  assert.equal((await execute({})).success, false);
  assert.equal((await execute(allowed, { exitCode: 1 })).success, false);
  assert.equal((await execute(allowed, { truncated: true })).success, false);
  assert.equal((await execute(allowed, {}, { timedOut: true })).success, false);
  assert.equal((await execute(allowed, {}, { stdout: '{"files":[]}' })).success, false);
});

test('subscription and follow-up context reach the workload without account credentials', async () => {
  let written;
  const sandbox = { networkIsolation: 'ISOLATED', files: { write: async (_, value) => written = JSON.parse(value) }, exec: async command => {
    if (command.startsWith('docker info')) return { exitCode: 0, stdout: JSON.stringify({ MemoryLimit: true, SwapLimit: true, CpuCfsQuota: true, PidsLimit: true, SecurityOptions: ['name=seccomp,profile=builtin'] }) };
    if (command.endsWith(' export')) return { exitCode: 0, stdout: JSON.stringify({ baseSha: request.baseSha, files: [{ path: 'a.cs', content: 'hi' }] }) };
    return { exitCode: 0, stdout: '', stderr: '' };
  }};
  await codingDispatch({ ...request, model: 'gpt-6-astra', responses: true, canAsk: true, followUpEvidence: 'Failing validation' },
    { connect: async () => sandbox });
  assert.equal(written.model, 'gpt-6-astra');
  assert.equal(written.responses, true);
  assert.equal(written.canAsk, true);
  assert.equal(written.followUpEvidence, 'Failing validation');
  assert.equal(written.token, undefined);
  assert.throws(() => validateRequest({ ...request, model: 'x;id' }));
});

const snapshot = { baseSha: request.baseSha, files: [{ path: 'src/work.cs', content: 'partial', mode: '100644' }],
  checks: [], summary: 'A worktree diff was saved.' };
const softTimeout = "timeout: sending signal TERM to command 'node'\n";

function checkpointSandbox(handlers = {}, exported = { ...snapshot, checkpointVersion: 1 }, imageLabel = '1') {
  const commands = [], requests = [];
  const ok = (stdout = '') => ({ exitCode: 0, stdout, stderr: '' });
  const sandbox = { networkIsolation: 'ISOLATED', files: { write: async (_, data) => requests.push(JSON.parse(data)) },
    exec: async (command, options) => {
      commands.push(command);
      if (command.startsWith('docker info')) return ok(JSON.stringify({ MemoryLimit: true, SwapLimit: true, CpuCfsQuota: true,
        PidsLimit: true, SecurityOptions: ['name=seccomp,profile=builtin'] }));
      if (command.startsWith('docker image inspect')) return ok(imageLabel);
      if (command.endsWith(' export')) return ok(JSON.stringify(exported));
      return handlers[command.split(' ').at(-1)]?.(command, options) ?? ok();
    } };
  return { commands, requests, api: { connect: async () => sandbox } };
}

test('a cumulative soft timeout returns a bounded checkpoint for a fresh sandbox', async () => {
  let now = 500;
  const sandbox = checkpointSandbox({
    prepare: () => { now += 21_000; return { exitCode: 0 }; },
    code: (command, options) => {
      assert.match(command, /--signal=TERM --kill-after=3 79 node/);
      assert.equal(options.timeoutSec, 89);
      now += 79_000;
      return { exitCode: 124, stderr: softTimeout + 'Build did not finish.' };
    },
    validate: () => assert.fail('Validation must wait for a complete code phase.')
  }, { ...snapshot, checkpointVersion: 1, hasProgress: true, progressSummary: 'Finish the API tests.' });
  const result = await codingDispatch({ ...request, executionSeconds: 100 }, sandbox.api, () => now);
  assert.equal(result.success, false);
  assert.equal(result.hasProgress, true);
  assert.equal(result.resumePhase, 'code');
  assert.deepEqual(result.artifact.files, snapshot.files);
  assert.equal(result.artifact.summary, 'Finish the API tests.');
  assert.equal(result.artifact.checkpointVersion, undefined);
  assert.equal(result.artifact.progressSummary, undefined);
  assert.ok(sandbox.commands.at(-1).includes('--network=none'));
});

test('a validation timeout resumes with the original code check and reruns the complete validation gate', async () => {
  let now = 0;
  const first = checkpointSandbox({
    code: () => { now += 80_000; return { exitCode: 0, stdout: 'Code completed.' }; },
    validate: () => { now += 20_000; return { exitCode: 124, stderr: softTimeout }; }
  });
  const stopped = await codingDispatch({ ...request, executionSeconds: 100 }, first.api, () => now);
  assert.equal(stopped.resumePhase, 'validate');
  assert.equal(stopped.success, false);
  const second = checkpointSandbox({ code: () => assert.fail('A proven successful code phase should not rerun.') });
  const result = await codingDispatch({ ...request, executionSeconds: 100, resumePhase: stopped.resumePhase,
    resumeArtifact: stopped.artifact, ownerClarifications: [{ question: 'Which option?', answer: 'The first.' }] }, second.api);
  assert.equal(result.success, true);
  assert.deepEqual(result.artifact.checks[1], stopped.artifact.checks[1]);
  assert.equal(second.requests[0].resumeArtifact.checks, undefined);
  assert.deepEqual(second.requests[0].resumeArtifact.files, snapshot.files);
  assert.deepEqual(second.requests[0].ownerClarifications, [{ question: 'Which option?', answer: 'The first.' }]);
});

test('a SIGKILL without a timer signal and an uncertain transport timeout do not trigger continuation', async () => {
  const early = checkpointSandbox({ code: () => ({ exitCode: 137 }) });
  const failed = await codingDispatch({ ...request, executionSeconds: 100 }, early.api, () => 0);
  assert.equal(failed.resumePhase, undefined);
  assert.deepEqual(failed.artifact.files, snapshot.files);
  let now = 0;
  const late = checkpointSandbox({ code: () => { now += 100_000; return { exitCode: 137 }; } });
  assert.equal((await codingDispatch({ ...request, executionSeconds: 100 }, late.api, () => now)).resumePhase, undefined);
  const uncertain = checkpointSandbox({ code: () => ({ timedOut: true, exitCode: 124 }) });
  const notExported = await codingDispatch({ ...request, executionSeconds: 100 }, uncertain.api, () => 100_000);
  assert.equal(notExported.resumePhase, undefined);
  assert.equal(notExported.artifact.files.length, 0);
  assert.ok(!uncertain.commands.some(command => command.endsWith(' export')));
});

test('empty checkpoints preserve an explicit timeout and legacy images retain their original phase timing', async () => {
  let now = 0;
  const empty = checkpointSandbox({ code: () => { now += 100_000; return { exitCode: 124, stderr: softTimeout }; } },
    { ...snapshot, files: [], checkpointVersion: 1, hasProgress: false });
  const result = await codingDispatch({ ...request, executionSeconds: 100 }, empty.api, () => now);
  assert.equal(result.resumePhase, 'code');
  assert.equal(result.hasProgress, false);
  const legacy = checkpointSandbox({}, snapshot, '');
  const normal = await codingDispatch({ ...request, executionSeconds: 100 }, legacy.api);
  assert.equal(normal.success, true);
  assert.ok(legacy.commands.some(command => command.includes('--signal=KILL 1200 ')));
  const unsupported = checkpointSandbox({}, snapshot, '');
  const grouped = await codingDispatch({ ...request, supervise: true }, unsupported.api);
  assert.equal(grouped.success, false);
  assert.match(grouped.artifact.summary, /current checkpoint/);
  assert.equal(unsupported.requests.length, 0);
});

test('resume data rejects unauthorized paths and a fabricated validation shortcut', () => {
  for (const path of ['../outside', 'src/.git/config', '.ENV/test.key', '.agentplayground-coding-progress.md', 'app/bin/compiled']) {
    assert.throws(() => validateRequest({ ...request, resumePhase: 'code',
      resumeArtifact: { ...snapshot, files: [{ ...snapshot.files[0], path }] } }), /checkpoint file/);
  }
  assert.throws(() => validateRequest({ ...request, resumePhase: 'validate', resumeArtifact: snapshot }), /earlier successful code phase/);
  assert.throws(() => validateRequest({ ...request, resumeArtifact: snapshot }), /resume phase/);
  assert.throws(() => validateRequest({ ...request, executionSeconds: '30; id' }), /Invalid coding request/);
});
