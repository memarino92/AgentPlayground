import { existsSync } from 'node:fs';
import { join } from 'node:path';
import { applyCodingArtifact, codingPath, progressNote } from './coding-checkpoint.mjs';

const hashes = output => output.trim().split('\n').filter(Boolean);

function pending(root) {
  return ['rebase-merge', 'rebase-apply'].some(state => existsSync(join(root, '.git', state)));
}

function linearCommits(git, from, to) {
  const commits = hashes(git(['rev-list', '--reverse', '--max-count=26', from + '..' + to]));
  if (!commits.length || commits.length > 25) throw new Error('A tracked rebase requires between 1 and 25 linear commits.');
  let parent = from;
  for (const sha of commits) {
    if (git(['show', '-s', '--format=%P', sha]).trim() !== parent) throw new Error('The tracked rebase history must be linear.');
    parent = sha;
  }
  return commits;
}

export function prepareRebase(root, git, base, originalHead, saved) {
  const fork = git(['merge-base', base, originalHead]).trim();
  linearCommits(git, fork, originalHead);
  git(['config', 'user.name', 'Platform improvements']);
  git(['config', 'user.email', 'platform-improvements@users.noreply.github.com']);
  if (saved) {
    const originals = linearCommits(git, fork, originalHead);
    if (!saved.rebaseCommits || saved.rebaseCommits.map(commit => commit.originalSha).join() !== originals.join())
      throw new Error('The saved rebase no longer matches the original commits.');
    for (const commit of saved.rebaseCommits) {
      applyCodingArtifact(root, { baseSha: base, files: commit.files, summary: '' }, base);
      git(['add', '-A', '--', '.', ':(exclude,top,literal)' + progressNote]);
      git(['commit', '--allow-empty', '--no-gpg-sign', '-C', commit.originalSha]);
    }
    return;
  }

  git(['checkout', '--detach', originalHead]);
  try {
    git(['-c', 'rerere.enabled=false', 'rebase', '--onto', base, '--reapply-cherry-picks', '--empty=keep', fork]);
  } catch (error) {
    if (!pending(root) || !git(['ls-files', '--unmerged', '-z'])) throw error;
  }
}

export function exportRebase(root, git, base, originalHead) {
  if (pending(root) || git(['ls-files', '--unmerged', '-z']))
    throw new Error('Resolve and stage all conflicts, then finish the Git rebase.');
  const original = linearCommits(git, git(['merge-base', base, originalHead]).trim(), originalHead);
  const rebased = linearCommits(git, base, 'HEAD');
  if (original.length !== rebased.length) throw new Error('Preserve each original commit in the rebase.');
  let parent = base;
  return rebased.map((sha, i) => {
    const paths = git(['diff', '--no-renames', '--name-only', '-z', parent, sha]).split('\0').filter(Boolean);
    const files = paths.map(path => {
      if (!codingPath(path)) throw new Error('Unsupported rebased path.');
      const find = rev => git(['ls-tree', '-z', rev, '--', path]).split('\0').find(line => line.slice(line.indexOf('\t') + 1) === path);
      const changed = find(sha);
      const entry = changed ?? find(parent);
      if (!entry || !/^100(644|755) blob [a-f0-9]{40}\t/.test(entry)) throw new Error('Unsupported rebased file type.');
      return { path, mode: entry.slice(0, 6), content: changed ? git(['cat-file', 'blob', sha + ':' + path]) : null };
    });
    parent = sha;
    return { originalSha: original[i], files };
  });
}
