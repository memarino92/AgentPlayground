import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { exportRebase, prepareRebase } from './coding-rebase.mjs';

test('a conflicting tracked PR rebases in commit order and its resolved series restores', t => {
  const root = mkdtempSync(join(tmpdir(), 'coding-rebase-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const git = args => execFileSync('git', ['-c', 'core.autocrlf=false', '-c', 'core.hooksPath=/dev/null', ...args],
    { cwd: root, encoding: 'utf8', env: { ...process.env, GIT_EDITOR: 'true', GIT_CONFIG_GLOBAL: '', GIT_CONFIG_NOSYSTEM: '1' },
      stdio: ['ignore', 'pipe', 'pipe'] });
  const write = (path, content) => writeFileSync(join(root, path), content);
  const commit = message => {
    git(['add', '--all']);
    git(['commit', '--no-gpg-sign', '-m', message]);
    return git(['rev-parse', 'HEAD']).trim();
  };
  git(['init', '--initial-branch=main']);
  git(['config', 'user.name', 'Fixture']);
  git(['config', 'user.email', 'fixture@example.com']);
  write('conflict.txt', 'original\n');
  commit('feat: initial');
  git(['checkout', '-b', 'tracked']);
  write('conflict.txt', 'branch\n');
  const first = commit('feat: first tracked change');
  write('second.txt', 'second\n');
  const head = commit('fix: second tracked change');
  git(['checkout', 'main']);
  write('conflict.txt', 'main\n');
  const base = commit('fix: update main');
  git(['checkout', '--detach', base]);

  prepareRebase(root, git, base, head);
  assert.match(git(['ls-files', '-u']), /conflict\.txt/);
  assert.throws(() => exportRebase(root, git, base, head), /finish the Git rebase/);
  write('conflict.txt', 'resolved\n');
  git(['add', 'conflict.txt']);
  git(['rebase', '--continue']);

  const series = exportRebase(root, git, base, head);
  assert.deepEqual(series, [
    { originalSha: first, files: [{ path: 'conflict.txt', content: 'resolved\n', mode: '100644' }] },
    { originalSha: head, files: [{ path: 'second.txt', content: 'second\n', mode: '100644' }] }
  ]);
  const tree = git(['rev-parse', 'HEAD^{tree}']).trim();
  git(['checkout', '--detach', base]);
  prepareRebase(root, git, base, head, { rebaseCommits: series });
  assert.equal(git(['rev-parse', 'HEAD^{tree}']).trim(), tree);
  assert.equal(git(['rev-list', '--count', base + '..HEAD']).trim(), '2');
  assert.deepEqual(exportRebase(root, git, base, head), series);
});
