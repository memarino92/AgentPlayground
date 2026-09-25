import { readFile } from 'node:fs/promises';

export async function loadPreparationFiles() {
  const files = ['Dockerfile.automation-sandbox', 'automation-sandbox/run.sh', 'automation-sandbox/Program.csproj', 'automation-sandbox/NuGet.Config'];
  return Promise.all(files.map(async file => [file, await readFile(new URL('../../' + file, import.meta.url), 'utf8')]));
}
