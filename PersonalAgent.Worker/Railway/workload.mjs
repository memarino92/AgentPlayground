// Included in the immutable coding image. Parameters arrive through a read-only JSON mount.
import { spawnSync } from 'node:child_process';
import { readFileSync, writeFileSync, mkdirSync, lstatSync, readdirSync } from 'node:fs';
const r = JSON.parse(readFileSync('/run/job.json', 'utf8'));
const phase = process.argv[2];
const root = '/work/repo';
const env = { PATH: process.env.PATH, HOME: '/work/home', DOTNET_CLI_HOME: '/work/home', NUGET_PACKAGES: '/work/packages',
  DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1', GIT_CONFIG_NOSYSTEM: '1', GIT_CONFIG_GLOBAL: '/dev/null',
  GIT_CONFIG_COUNT: '1', GIT_CONFIG_KEY_0: 'safe.directory', GIT_CONFIG_VALUE_0: root };
function run(command, args, cwd = root, extra = {}) {
  const p = spawnSync(command, args, { cwd, env: { ...env, ...extra }, encoding: 'utf8', maxBuffer: 8_000_000, timeout: 25 * 60 * 1000 });
  if (p.error || p.status !== 0) throw new Error(`${command} failed (${p.status}): ${(p.stderr ?? '').slice(-15000)} ${(p.stdout ?? '').slice(-15000)}`);
  return p.stdout;
}
try {
  if (phase === 'prepare') {
    mkdirSync('/work/home', { recursive: true });
    run('git', ['-c', 'core.hooksPath=/dev/null', 'clone', '--no-checkout', '--', `https://github.com/${r.repository}.git`, root], '/work');
    run('git', ['-c', 'core.hooksPath=/dev/null', 'checkout', '--detach', r.baseSha]);
    // Agent runs unprivileged. A writable home/workspace never implies access to the VM controller.
    run('chmod', ['-R', 'a+rwX', '/work'], '/work');
  } else if (phase === 'code') {
    const config = { $schema: 'https://opencode.ai/config.json', autoupdate: false, share: 'disabled',
      enabled_providers: ['coding'], model: 'coding/openai/gpt-5.4-mini', small_model: 'coding/openai/gpt-5.4-mini',
      permission: { '*': 'allow', question: 'deny', external_directory: 'deny' },
      provider: { coding: { npm: '@ai-sdk/openai-compatible', name: 'Scoped coding gateway',
        options: { baseURL: r.gateway, apiKey: r.capability },
        models: { 'openai/gpt-5.4-mini': { name: 'Coding', limit: { context: 400000, output: 8192 } } } } } };
    const prompt = `Implement the following request in this repository. Read AGENTS.md and applicable documentation. Make a focused change and appropriate tests. Do not push, open PRs, deploy, change credentials, or modify .github files. Do not ask questions; if blocked, explain in your final response. Your final response should describe the change and validation.\n\n${r.instruction}`;
    // No actual provider key exists here. The job capability can only make budgeted model requests.
    const output = run('opencode', ['run', '--format', 'json', '--model', 'coding/openai/gpt-5.4-mini', prompt], root,
      { OPENCODE_CONFIG_CONTENT: JSON.stringify(config), OPENCODE_DISABLE_AUTOUPDATE: 'true' });
    writeFileSync('/work/agent-output.jsonl', output.slice(-100000));
    if (output.split('\n').filter(Boolean).some(line => { try { return JSON.parse(line).type === 'error'; } catch { return false; } }))
      throw new Error('OpenCode reported a failed model operation');
  } else if (phase === 'validate') {
    const output = run('dotnet', ['test', r.testProject, '--nologo', '--filter', r.testFilter, '--logger', 'trx', '--results-directory', '/tmp/coding-results']);
    const files = readdirSync('/tmp/coding-results').filter(x => x.endsWith('.trx'));
    if (!files.length || !files.some(f => /<Counters[^>]*\bexecuted="[1-9][0-9]*"/.test(readFileSync('/tmp/coding-results/' + f, 'utf8')))) throw new Error('No tests executed');
    process.stdout.write(output.slice(-18000));
  } else if (phase === 'export') {
    // Use a fresh index, not an index or staged selection manipulated by the candidate.
    const gitEnv = { GIT_INDEX_FILE: '/tmp/export-index' };
    run('git', ['-c', 'core.hooksPath=/dev/null', 'read-tree', r.baseSha], root, gitEnv);
    run('git', ['-c', 'core.hooksPath=/dev/null', 'add', '-A', '--', '.'], root, gitEnv);
    const names = run('git', ['-c', 'core.hooksPath=/dev/null', 'diff', '--cached', '--name-only', '-z', r.baseSha], root, gitEnv).split('\0').filter(Boolean);
    if (!names.length || names.length > 50) throw new Error('Expected 1–50 changed files');
    const files = names.map(path => {
      if (!/^[A-Za-z0-9_./-]{1,240}$/.test(path) || path.split('/').some(p => !p || p === '.' || p === '..' || p.startsWith('.git'))) throw new Error('Unsupported path');
      const entry = run('git', ['ls-files', '--stage', '--', path], root, gitEnv);
      if (!entry) return { path, content: null };
      const mode = entry.slice(0, 6);
      if (!['100644', '100755'].includes(mode) || !lstatSync(root + '/' + path).isFile()) throw new Error('Unsupported file type');
      const content = readFileSync(root + '/' + path, 'utf8');
      if (content.includes('\0') || content.includes('\ufffd')) throw new Error('Text changes only');
      return { path, content, mode };
    });
    let summary = 'Implements the requested platform change. Review the diff and recorded test results.';
    try {
      const text = readFileSync('/work/agent-output.jsonl', 'utf8').split('\n').flatMap(line => {
        try { const event = JSON.parse(line); return event.type === 'text' && typeof event.part?.text === 'string' ? [event.part.text] : []; } catch { return []; }
      }).join('\n').trim();
      if (text) summary = text.slice(-4000);
    } catch { /* A valid diff still has the generic summary. */ }
    process.stdout.write(JSON.stringify({ baseSha: r.baseSha, files, checks: [], summary }));
  } else throw new Error('Unknown phase');
} catch (e) { process.stderr.write(String(e.message).slice(-20000)); process.exitCode = 1; }
