import { chmodSync, closeSync, constants, fstatSync, lstatSync, mkdirSync, openSync, readFileSync, rmdirSync, unlinkSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';

export const checkpointVersion = 1;
export const maxArtifactBytes = 1_000_000;
export const progressNote = '.agentplayground-coding-progress.md';

export function codingPath(path) {
  return typeof path === 'string' && path.length >= 1 && path.length <= 240 && path.toLowerCase() !== progressNote
    && !/[^A-Za-z0-9_./-]/.test(path) && !/\.(?:pem|key)$/i.test(path)
    && path.split('/').every(part => part && part !== '.' && part !== '..' && !/^\.git/i.test(part)
      && !['.env', 'node_modules', 'bin', 'obj'].includes(part));
}

export function validateCodingArtifact(artifact, baseSha) {
  if (!artifact || artifact.baseSha !== baseSha || !Array.isArray(artifact.files) || artifact.files.length > 50
    || typeof artifact.summary !== 'string' || artifact.summary.length > 4000
    || Buffer.byteLength(JSON.stringify(artifact)) > maxArtifactBytes) throw new Error('Invalid coding checkpoint artifact.');
  validateFiles(artifact.files);
  if (artifact.rebaseCommits != null) {
    if (!Array.isArray(artifact.rebaseCommits) || !artifact.rebaseCommits.length || artifact.rebaseCommits.length > 25)
      throw new Error('Invalid rebased commit series.');
    for (const commit of artifact.rebaseCommits) {
      if (!commit || !/^[a-f0-9]{40}$/.test(commit.originalSha) || !Array.isArray(commit.files) || commit.files.length > 50)
        throw new Error('Invalid rebased commit.');
      validateFiles(commit.files);
    }
  }
}

function validateFiles(files) {
  const seen = new Set();
  for (const file of files) {
    if (!file || !codingPath(file.path) || seen.has(file.path.toLowerCase())
      || !['100644', '100755'].includes(file.mode)
      || (file.content !== null && (typeof file.content !== 'string' || /[\0\ufffd]/.test(file.content))))
      throw new Error('Invalid coding checkpoint file.');
    seen.add(file.path.toLowerCase());
  }
}

function entry(path) {
  try { return lstatSync(path); }
  catch (error) { if (error.code !== 'ENOENT') throw error; }
}

function pathInCheckout(root, path, createParents = false) {
  if (!codingPath(path)) throw new Error('Unsupported coding path.');
  const parts = path.split('/');
  const name = parts.pop();
  let parent = root;
  for (const part of parts) {
    parent = join(parent, part);
    const stat = entry(parent);
    if (!stat && createParents) mkdirSync(parent);
    else if (!stat?.isDirectory()) throw new Error('Coding path has a missing or non-directory parent.');
  }
  return join(parent, name);
}

export function applyCodingArtifact(root, artifact, baseSha) {
  validateCodingArtifact(artifact, baseSha);
  for (const file of artifact.files.filter(file => file.content === null)) {
    const target = pathInCheckout(root, file.path);
    if (!entry(target)?.isFile()) throw new Error('Coding deletion does not match a regular base file.');
    unlinkSync(target);
    // Git has no empty directories. Prune them so a directory can be replaced by a file.
    for (let parent = dirname(target); parent !== root; parent = dirname(parent)) {
      try { rmdirSync(parent); }
      catch (error) { if (error.code !== 'ENOTEMPTY' && error.code !== 'EEXIST') throw error; break; }
    }
  }
  for (const file of artifact.files.filter(file => file.content !== null)) {
    const target = pathInCheckout(root, file.path, true);
    const stat = entry(target);
    if (stat && !stat.isFile()) throw new Error('Coding checkpoint cannot replace a non-regular file.');
    writeFileSync(target, file.content, { flag: constants.O_WRONLY | constants.O_CREAT | constants.O_TRUNC | constants.O_NOFOLLOW });
    chmodSync(target, file.mode === '100755' ? 0o755 : 0o644);
  }
}

export function exportCodingFiles(root, baseSha, git) {
  git(['read-tree', baseSha]);
  git(['add', '-A', '--', '.', ':(exclude,top,literal)' + progressNote]);
  const names = git(['diff', '--cached', '--no-renames', '--name-only', '-z', baseSha]).split('\0').filter(Boolean);
  if (names.length > 50) throw new Error('Expected at most 50 changed files.');
  return names.map(path => {
    if (!codingPath(path)) throw new Error('Unsupported coding path.');
    const entry = exactEntry(git(['ls-files', '--stage', '-z', '--', path]), path);
    const mode = (entry || exactEntry(git(['ls-tree', '-z', baseSha, '--', path]), path)).slice(0, 6);
    if (!['100644', '100755'].includes(mode)) throw new Error('Unsupported coding file type.');
    return { path, content: entry ? readCodingFile(root, path) : null, mode };
  });
}

function exactEntry(entries, path) {
  return entries.split('\0').find(entry => entry.slice(entry.indexOf('\t') + 1) === path) ?? '';
}

export function readCodingFile(root, path) {
  return readBoundedText(pathInCheckout(root, path), maxArtifactBytes);
}

export function readBoundedText(path, maxBytes) {
  if (lstatSync(path).isSymbolicLink()) throw new Error('Expected bounded regular text.');
  const fd = openSync(path, constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK);
  try {
    const stat = fstatSync(fd);
    if (!stat.isFile() || stat.size > maxBytes) throw new Error('Expected bounded regular text.');
    const text = readFileSync(fd, 'utf8');
    if (/[\0\ufffd]/.test(text)) throw new Error('Expected bounded regular text.');
    return text;
  } finally { closeSync(fd); }
}
