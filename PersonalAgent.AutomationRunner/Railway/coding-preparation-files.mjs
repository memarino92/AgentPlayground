import { readFile } from 'node:fs/promises';

export async function loadCodingPreparationFiles() {
  return [
    ['Dockerfile.coding-sandbox', await readFile(new URL('../../Dockerfile.coding-sandbox', import.meta.url), 'utf8')],
    ['PersonalAgent.AutomationRunner/Railway/workload.mjs', await readFile(new URL('./workload.mjs', import.meta.url), 'utf8')],
  ];
}
