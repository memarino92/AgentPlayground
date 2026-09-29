// Operator-only: JSON on stdin { token, environmentId, checkpoint }. No credentials in image/checkpoint.
import { Sandbox } from 'railway';
import { loadCodingPreparationFiles } from './coding-preparation-files.mjs';
let sandbox;
try {
  let input = '';
  for await (const chunk of process.stdin) { input += chunk; if (input.length > 8192) throw new Error('Input limit'); }
  const request = JSON.parse(input);
  if (!/^[A-Za-z0-9_.-]{1,128}$/.test(request.checkpoint)) throw new Error('Invalid checkpoint');
  const files = await loadCodingPreparationFiles();
  sandbox = await Sandbox.create({ token: request.token, environmentId: request.environmentId, authType: 'bearer', networkIsolation: 'ISOLATED', idleTimeoutMinutes: 15, verbose: false });
  for (const [path, content] of files) await sandbox.files.write('/prepare/' + path, content);
  const build = await sandbox.exec('docker build -f Dockerfile.coding-sandbox -t platform-coding:1 .', { cwd: '/prepare', timeoutSec: 600 });
  if (build.exitCode !== 0 || build.timedOut) throw new Error('Build failed');
  const image = await sandbox.exec("docker image inspect --format '{{.Id}}' platform-coding:1", { timeoutSec: 15 });
  const imageId = image.stdout.trim();
  if (!/^sha256:[a-f0-9]{64}$/.test(imageId)) throw new Error('Invalid image');
  await sandbox.checkpoint(request.checkpoint);
  process.stdout.write(JSON.stringify({ checkpoint: request.checkpoint, imageId, environmentId: request.environmentId }));
} catch { process.stderr.write('Coding checkpoint preparation failed.'); process.exitCode = 1; }
finally {
  if (sandbox) {
    try { await sandbox.destroy(); }
    catch { process.stderr.write(` Cleanup required: ${sandbox.id}`); process.exitCode = 1; }
  }
}
