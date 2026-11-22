import { readFile } from 'node:fs/promises';

export async function loadCodingPreparationFiles() {
  return [
    ['Dockerfile.coding-sandbox', await readFile(new URL('../../Dockerfile.coding-sandbox', import.meta.url), 'utf8')],
    ['PersonalAgent.Worker/Railway/workload.mjs', await readFile(new URL('./workload.mjs', import.meta.url), 'utf8')],
    ['PersonalAgent.Worker/Railway/coding-questions.mjs', await readFile(new URL('./coding-questions.mjs', import.meta.url), 'utf8')],
    ['PersonalAgent.Worker/Railway/coding-checkpoint.mjs', await readFile(new URL('./coding-checkpoint.mjs', import.meta.url), 'utf8')],
    ['PersonalAgent.Worker/Railway/coding-supervision.mjs', await readFile(new URL('./coding-supervision.mjs', import.meta.url), 'utf8')],
    ['PersonalAgent.Worker/Railway/coding-rebase.mjs', await readFile(new URL('./coding-rebase.mjs', import.meta.url), 'utf8')],
  ];
}
