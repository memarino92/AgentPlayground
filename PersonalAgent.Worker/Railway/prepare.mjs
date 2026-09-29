// Operator-only setup. JSON on stdin: {token, environmentId, checkpoint}. Never run inside the sandbox.
import { Sandbox } from 'railway';
import { loadPreparationFiles } from './preparation-files.mjs';

let sandbox;
try {
  let input = '';
  for await (const chunk of process.stdin) { input += chunk; if (input.length > 8192) throw new Error('Input limit'); }
  const request = JSON.parse(input);
  if (!/^[A-Za-z0-9_.-]{1,128}$/.test(request.checkpoint)) throw new Error('Invalid checkpoint');
  sandbox = await Sandbox.create({ token: request.token, environmentId: request.environmentId, authType: 'bearer', networkIsolation: 'ISOLATED', idleTimeoutMinutes: 5, verbose: false });
  for (const [file, content] of await loadPreparationFiles()) await sandbox.files.write('/prepare/' + file, content);
  const build = await sandbox.exec('docker build -f Dockerfile.automation-sandbox -t agentplayground-csharp-sandbox:1 .', { cwd: '/prepare', timeoutSec: 240 });
  if (build.exitCode !== 0 || build.timedOut) throw new Error('Build failed');
  const image = await sandbox.exec("docker image inspect --format '{{.Id}}' agentplayground-csharp-sandbox:1", { timeoutSec: 15 });
  const imageId = image.stdout.trim();
  if (!/^sha256:[a-f0-9]{64}$/.test(imageId)) throw new Error('Invalid image identity');
  const checkpoint = await sandbox.checkpoint(request.checkpoint);
  process.stdout.write(JSON.stringify({ checkpoint: request.checkpoint, checkpointId: checkpoint.id, imageId, environmentId: request.environmentId }));
} catch {
  process.stderr.write('Sandbox preparation failed. Check Railway access and the sandbox build. No configuration was changed.');
  process.exitCode = 1;
} finally {
  if (sandbox) {
    try { await sandbox.destroy(); }
    catch { process.stderr.write(` Cleanup required for preparation sandbox ${sandbox.id}.`); process.exitCode = 1; }
  }
}
