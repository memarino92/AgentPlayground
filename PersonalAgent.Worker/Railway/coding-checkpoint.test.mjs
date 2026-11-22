import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, renameSync, statSync, symlinkSync, unlinkSync, writeFileSync } from 'node:fs';
import { rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { applyCodingArtifact, exportCodingFiles, progressNote, readCodingFile, validateCodingArtifact } from './coding-checkpoint.mjs';

function checkout(t) {
  const root = mkdtempSync(join(tmpdir(), 'coding-checkpoint-'));
  t.after(() => rm(root, { recursive: true, force: true }));
  return root;
}

const baseSha = 'c'.repeat(40);
const file = (path, content, mode = '100644') => ({ path, content, mode });
const diff = files => ({ baseSha, files, summary: '', checks: [] });

test('a trusted export replays the full diff in a separate clone of the original pinned commit', t => {
  const root = checkout(t);
  const source = join(root, 'source'), target = join(root, 'target');
  mkdirSync(source);
  const git = (cwd, args, index) => execFileSync('git', ['-c', 'core.autocrlf=false', '-c', 'core.safecrlf=false', ...args], {
    cwd, encoding: 'utf8', env: index ? { ...process.env, GIT_INDEX_FILE: join(root, index) } : process.env
  });
  const snapshot = (repo, sha, index) => exportCodingFiles(repo, sha, args => git(repo, args, index));
  git(source, ['init', '-q']);
  writeFileSync(join(source, 'rename-me'), 'same content\n');
  writeFileSync(join(source, 'edit-me'), 'old content\n');
  git(source, ['add', '--all']);
  git(source, ['-c', 'user.name=Proof', '-c', 'user.email=proof@example.invalid', 'commit', '-qm', 'initial']);
  const sha = git(source, ['rev-parse', 'HEAD']).trim();
  git(root, ['clone', '-q', '--no-hardlinks', source, target]);
  renameSync(join(source, 'rename-me'), join(source, 'renamed'));
  unlinkSync(join(source, 'edit-me'));
  mkdirSync(join(source, 'edit-me'));
  writeFileSync(join(source, 'edit-me', 'new-file'), 'replacement\n');
  writeFileSync(join(source, progressNote), 'private continuation note');
  const files = snapshot(source, sha, 'source-index');
  assert.ok(files.some(item => item.path === 'rename-me' && item.content === null));
  assert.ok(files.some(item => item.path === 'renamed' && item.content === 'same content\n'));
  assert.ok(!files.some(item => item.path === progressNote));
  applyCodingArtifact(target, { baseSha: sha, files, checks: [], summary: 'Continue from these files.' }, sha);
  assert.deepEqual(snapshot(target, sha, 'target-index'), files);
  assert.ok(!existsSync(join(target, progressNote)));
});

test('a fresh checkout restores edits, executable mode, deletes, and file/directory transitions', t => {
  const root = checkout(t);
  writeFileSync(join(root, 'source'), 'base');
  mkdirSync(join(root, 'renamed', 'nested'), { recursive: true });
  writeFileSync(join(root, 'renamed', 'nested', 'old'), 'base');
  writeFileSync(join(root, 'old-name'), 'before');
  applyCodingArtifact(root, diff([
    file('source/shell.sh', '#!/bin/sh\n', '100755'),
    file('source', null),
    file('renamed', 'replaced'),
    file('renamed/nested/old', null),
    file('new-name', 'before'),
    file('old-name', null)
  ]), baseSha);
  assert.equal(readCodingFile(root, 'source/shell.sh'), '#!/bin/sh\n');
  if (process.platform !== 'win32') assert.equal(statSync(join(root, 'source', 'shell.sh')).mode & 0o777, 0o755);
  assert.equal(readCodingFile(root, 'renamed'), 'replaced');
  assert.equal(readCodingFile(root, 'new-name'), 'before');
  assert.ok(!existsSync(join(root, 'old-name')));
});

test('restore rejects links in both parent and leaf paths', t => {
  const root = checkout(t), outside = checkout(t);
  const secret = join(outside, 'keep');
  writeFileSync(secret, 'expected');
  symlinkSync(outside, join(root, 'redirect'), 'junction');
  assert.throws(() => applyCodingArtifact(root, diff([file('redirect/keep', 'changed')]), baseSha), /non-directory parent/);
  assert.throws(() => applyCodingArtifact(root, diff([file('redirect', 'changed')]), baseSha), /non-regular file/);
  assert.throws(() => readCodingFile(root, 'redirect/keep'), /non-directory parent/);
  assert.equal(readFileSync(secret, 'utf8'), 'expected');
});

test('checkpoint validation rejects a wrong base, duplicate case variants, binary content, and oversized files', () => {
  assert.throws(() => validateCodingArtifact(diff([]), 'different'), /checkpoint artifact/);
  assert.throws(() => validateCodingArtifact(diff([file('a', '1'), file('A', '2')]), baseSha), /checkpoint file/);
  assert.throws(() => validateCodingArtifact(diff([file('a', '\0')]), baseSha), /checkpoint file/);
  assert.throws(() => validateCodingArtifact(diff([file('a', '😀'.repeat(250_000))]), baseSha), /checkpoint artifact/);
});
