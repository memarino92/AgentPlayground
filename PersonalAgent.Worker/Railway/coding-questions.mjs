import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { createInterface } from 'node:readline';
import { setTimeout as delay } from 'node:timers/promises';

const job = JSON.parse(readFileSync(process.argv[2] ?? '/run/job.json', 'utf8'));
const tool = {
  name: 'ask',
  description: 'Ask the owner a focused question in the original conversation and wait for their answer.',
  inputSchema: { type: 'object', properties: { text: { type: 'string', minLength: 1, maxLength: 2000 } }, required: ['text'] }
};

async function ask(text, signal) {
  if (typeof text !== 'string' || !text.trim() || text.length > 2000) throw new Error('Enter one question of 1–2000 characters.');
  const body = JSON.stringify({ id: randomUUID(), text });
  while (!signal.aborted) {
    let response;
    try {
      response = await fetch(job.gateway + '/question', {
        method: 'POST', redirect: 'error', body,
        headers: { authorization: 'Bearer ' + job.capability, 'content-type': 'application/json' },
        signal: AbortSignal.any([signal, AbortSignal.timeout(10000)])
      });
    } catch {
      await delay(5000, undefined, { signal });
      continue;
    }
    if (response.ok) {
      const { answer } = await response.json();
      if (typeof answer === 'string') return answer;
    } else if (response.status !== 429 && response.status < 500) {
      throw new Error('The job can no longer ask this question. It may have expired or reached its question limit.');
    }
    await delay(5000, undefined, { signal });
  }
  throw new Error('Question cancelled.');
}

const pending = new Map();
const input = createInterface({ input: process.stdin });
input.on('close', () => { for (const controller of pending.values()) controller.abort(); });
input.on('line', async line => {
  let request;
  try { request = JSON.parse(line); } catch { return; }
  const { id, method, params } = request;
  if (method === 'notifications/cancelled') pending.get(params?.requestId)?.abort();
  if (id === undefined) return;
  const send = payload => process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id, ...payload }) + '\n');
  if (method === 'initialize') return send({ result: { protocolVersion: '2025-11-25',
    serverInfo: { name: 'coding-questions', version: '1.0.0' }, capabilities: { tools: {} } } });
  if (method === 'ping') return send({ result: {} });
  if (method === 'tools/list') return send({ result: { tools: [tool] } });
  if (method !== 'tools/call' || params?.name !== tool.name)
    return send({ error: { code: -32601, message: 'Unknown request' } });
  const controller = new AbortController();
  pending.set(id, controller);
  try {
    const answer = await ask(params.arguments?.text, controller.signal);
    send({ result: { content: [{ type: 'text', text: answer }] } });
  } catch (error) {
    send({ result: { isError: true, content: [{ type: 'text', text: controller.signal.aborted ? 'Question cancelled.' : error.message }] } });
  } finally {
    pending.delete(id);
  }
});
