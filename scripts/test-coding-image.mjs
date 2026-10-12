// Local Docker integration proof. Uses synthetic model responses; no API key or model charges.
import { createServer } from 'node:http';
import { spawn } from 'node:child_process';
import { mkdtemp, writeFile, rm, chmod } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, dirname, resolve } from 'node:path';
import assert from 'node:assert/strict';
const image = process.argv[2] ?? 'agentplayground-coding-sandbox:1';
const repositoryBase = process.argv[3] === '-' ? undefined : process.argv[3];
const responses = process.argv[4] === 'responses';
if (repositoryBase) assert.match(repositoryBase, /^[a-f0-9]{40}$/);
const volume = `coding-smoke-${Date.now()}`;
const folder = await mkdtemp(join(tmpdir(), 'coding-smoke-'));
// Linux mkdtemp defaults to 0700; the container UID must traverse this synthetic request mount.
await chmod(folder, 0o755);
let calls = 0;
let edited = false;
const server = createServer(async (req, res) => {
  let body = '';
  for await (const part of req) body += part;
  const input = JSON.parse(body);
  assert.equal(req.headers.authorization, 'Bearer synthetic-proof-capability');
  assert.equal(input.model, responses ? 'gpt-6-astra' : 'openai/gpt-5.4-mini');
  calls++;
  const canEdit = input.tools?.some(t => (responses ? t.name : t.function?.name) === 'bash');
  const delta = canEdit && !edited
    ? { tool_calls: [{ index: 0, id: 'proof-edit', type: 'function', function: { name: 'bash', arguments: JSON.stringify({ command: "printf 'Synthetic coding proof.\\n' > coding-proof.txt", description: 'Write synthetic proof file' }) } }] }
    : { content: 'Synthetic coding proof complete.' };
  if (canEdit) edited = true;
  res.writeHead(200, { 'content-type': 'text/event-stream' });
  if (responses) {
    assert.ok(req.url.endsWith('/responses'));
    const output = delta.tool_calls
      ? [{ id: 'fc_proof', type: 'function_call', call_id: 'proof-edit', name: 'bash', namespace: 'coding', arguments: delta.tool_calls[0].function.arguments, status: 'completed' }]
      : [{ id: 'msg_proof', type: 'message', role: 'assistant', status: 'completed', content: [{ type: 'output_text', text: delta.content, annotations: [] }] }];
    let sequence = 0;
    function event(type, data) { res.write(`event: ${type}\ndata: ${JSON.stringify({ type, sequence_number: sequence++, ...data })}\n\n`); }
    const response = { id: `resp_${calls}`, object: 'response', created_at: 1, model: input.model, status: 'in_progress', output: [] };
    event('response.created', { response });
    event('response.output_item.added', { output_index: 0, item: { ...output[0], status: 'in_progress', ...(delta.tool_calls ? { arguments: '' } : { content: [] }) } });
    if (delta.tool_calls) event('response.function_call_arguments.delta', { output_index: 0, item_id: 'fc_proof', delta: output[0].arguments });
    else {
      event('response.content_part.added', { output_index: 0, item_id: 'msg_proof', content_index: 0, part: { type: 'output_text', text: '', annotations: [] } });
      event('response.output_text.delta', { output_index: 0, item_id: 'msg_proof', content_index: 0, delta: delta.content, logprobs: [] });
    }
    event('response.output_item.done', { output_index: 0, item: output[0] });
    event('response.completed', { response: { ...response, status: 'completed', output, usage: { input_tokens: 10, output_tokens: 10, total_tokens: 20, input_tokens_details: { cached_tokens: 0 }, output_tokens_details: { reasoning_tokens: 0 } } } });
    res.end();
    return;
  }
  for (const choice of [{ delta, finish_reason: null }, { delta: {}, finish_reason: delta.tool_calls ? 'tool_calls' : 'stop' }])
    res.write(`data: ${JSON.stringify({ id: 'proof', object: 'chat.completion.chunk', created: 1, model: input.model, choices: [{ index: 0, ...choice }] })}\n\n`);
  res.end('data: [DONE]\n\n');
});
await new Promise(resolve => server.listen(0, '0.0.0.0', resolve));
async function docker(args) {
  return await new Promise((resolve, reject) => {
    const child = spawn('docker', args, { windowsHide: true });
    let output = ''; let error = '';
    child.stdout.on('data', c => output += c); child.stderr.on('data', c => error += c);
    child.on('error', reject);
    child.on('exit', code => code === 0 ? resolve(output) : reject(new Error(`Docker exit ${code}: ${error.slice(-4000)} ${output.slice(-4000)}`)));
  });
}
try {
  await docker(['volume', 'create', volume]);
  const common = ['run', '--rm', '--read-only', '--cap-drop=ALL', '--security-opt=no-new-privileges', '--memory=4g', '--memory-swap=4g', '--cpus=2', '--pids-limit=512', '--add-host=host.docker.internal:host-gateway',
    '--mount', `type=volume,src=${volume},dst=/work`, '--mount', `type=bind,src=${folder},dst=/run,readonly`, '--tmpfs', '/tmp:rw,nosuid,size=536870912,mode=1777'];
  // Tiny offline Git fixture isolates the OpenCode adapter from repository/NuGet availability.
  const setup = "mkdir -p /work/repo /work/home && cd /work/repo && git init -q && git -c user.name=Proof -c user.email=proof@example.test commit --allow-empty -qm initial && chmod -R a+rwX /work && git rev-parse HEAD";
  const baseSha = repositoryBase ?? (await docker([...common, '--entrypoint', 'sh', image, '-c', setup])).trim();
  await writeFile(join(folder, 'job.json'), JSON.stringify({ repository: 'memarino92/AgentPlayground', baseSha,
    gateway: `http://host.docker.internal:${server.address().port}/v1`, capability: 'synthetic-proof-capability', instruction: 'Create coding-proof.txt containing Synthetic coding proof.',
    testProject: 'PersonalAgent.Api.Tests/PersonalAgent.Api.Tests.csproj', testFilter: 'FullyQualifiedName~AgentSkillsTests',
    model: responses ? 'gpt-6-astra' : 'openai/gpt-5.4-mini', responses }));
  if (repositoryBase) await docker([...common, image, 'prepare']);
  await docker([...common, '--user=65532:65532', image, 'code']);
  if (repositoryBase) await docker([...common, '--user=65532:65532', image, 'validate']);
  const artifact = JSON.parse(await docker([...common, '--user=65532:65532', '--network=none', image, 'export']));
  assert.ok(calls > 0); assert.ok(edited);
  assert.equal(artifact.files.find(f => f.path === 'coding-proof.txt')?.content, 'Synthetic coding proof.\n');
  console.log(`OpenCode image proof passed: ${calls} synthetic requests, tool edit and isolated artifact export${repositoryBase ? ', public clone and Linux test gate' : ''}.`);
} finally {
  server.closeAllConnections(); server.close();
  await docker(['volume', 'rm', volume]);
  assert.equal(dirname(resolve(folder)), resolve(tmpdir()));
  await rm(folder, { recursive: true, force: true });
}
