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
const supervise = process.argv[5] === 'supervise';
if (supervise) assert.ok(responses, 'The supervision proof uses the Responses protocol.');
if (repositoryBase) assert.match(repositoryBase, /^[a-f0-9]{40}$/);
const volume = `coding-smoke-${Date.now()}`;
const folder = await mkdtemp(join(tmpdir(), 'coding-smoke-'));
// Linux mkdtemp defaults to 0700; the container UID must traverse this synthetic request mount.
await chmod(folder, 0o755);
let calls = 0;
let ranFailingCommand = false;
let replayedFailure = false;
let edited = false;
let asked = false;
let questionPolls = 0;
let questionId;
const question = 'What text should the proof contain?';
const answer = 'Synthetic owner answer';
const specialists = [
  { agent: 'coding-implementation', prompt: 'Investigate the implementation fixture.', report: 'Implementation advice 271828.' },
  { agent: 'coding-validation', prompt: 'Investigate the validation fixture.', report: 'Validation advice 314159.' },
];
const started = new Set();
let launched = 0;
let resolveBoth;
const bothStarted = new Promise(resolve => { resolveBoth = resolve; });
const supervisedContent = specialists.map(specialist => specialist.report).join('\n') + '\n';
const ordinaryContent = 'Synthetic coding proof.\n';
const call = (id, name, args) => ({ tool_calls: [{ index: 0, id, type: 'function', function: { name, arguments: JSON.stringify(args) } }] });
const completion = { content: 'Synthetic coding proof complete.' };

async function supervisedReply(input, names, shell) {
  if (!names.length) return completion;
  const users = input.input.filter(item => item.role === 'user').map(item => JSON.stringify(item.content)).join('\n');
  const child = specialists.find(specialist => users.includes('You are a subagent spawned by another session.') && users.includes(specialist.prompt));
  if (child) {
    assert.ok(names.includes('read'));
    assert.ok(!shell && !names.includes('subagent') && !names.includes('owner_ask'));
    started.add(child.agent);
    if (started.size === specialists.length) resolveBoth();
    let timer;
    try {
      await Promise.race([bothStarted, new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error('Both specialists must start before either reports.')), 30000);
      })]);
    } finally {
      clearTimeout(timer);
    }
    return { content: child.report };
  }
  if (launched < specialists.length) {
    assert.ok(names.includes('subagent'));
    const specialist = specialists[launched++];
    return call('proof-' + specialist.agent, 'subagent', {
      agent: specialist.agent, description: 'Inspect the synthetic fixture', prompt: specialist.prompt,
    });
  }
  if (!edited && specialists.every(specialist => users.includes(specialist.report))) {
    assert.ok(shell);
    edited = true;
    return call('proof-integrate', shell, {
      command: "node -e \"require('node:fs').writeFileSync('coding-proof.txt', '" + supervisedContent.replaceAll('\n', '\\n') + "')\"",
      description: 'Combine both specialist reports',
    });
  }
  return completion;
}

const server = createServer(async (req, res) => {
  let body = '';
  for await (const part of req) body += part;
  const input = JSON.parse(body);
  assert.equal(req.headers.authorization, 'Bearer synthetic-proof-capability');
  if (req.url.endsWith('/question')) {
    assert.equal(input.text, question);
    questionId ??= input.id;
    assert.equal(input.id, questionId);
    questionPolls++;
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ answer: questionPolls >= 2 ? answer : null }));
    return;
  }
  assert.equal(input.model, responses ? 'gpt-6-astra' : 'openai/gpt-5.4-mini');
  const callNumber = ++calls;
  const names = input.tools?.map(t => responses ? t.name : t.function?.name) ?? [];
  const shell = names.find(t => t === 'shell' || t === 'bash');
  const toolResults = responses
    ? input.input?.filter(item => item.type === 'function_call_output').map(item => item.output)
    : input.messages?.filter(message => message.role === 'tool').map(message => message.content);
  let delta = completion;
  if (supervise) {
    delta = await supervisedReply(input, names, shell);
  } else if (!asked && names.includes('owner_ask')) {
    asked = true;
    delta = call('proof-question', 'owner_ask', { text: question });
  } else if (!ranFailingCommand && shell && JSON.stringify(responses ? input.input : input.messages).includes(answer)) {
    ranFailingCommand = true;
    delta = call('proof-failure', shell, { command: "printf 'Synthetic missing dependency.\\n'; exit 1", description: 'Produce a recoverable command failure' });
  } else if (ranFailingCommand && !edited && shell && JSON.stringify(toolResults ?? []).includes('Exit code: 1')) {
    replayedFailure = true;
    edited = true;
    delta = call('proof-edit', shell, { command: "printf 'Synthetic coding proof.\\n' > coding-proof.txt", description: 'Write synthetic proof file' });
  }
  res.writeHead(200, { 'content-type': 'text/event-stream' });
  if (responses) {
    assert.ok(req.url.endsWith('/responses'));
    const id = delta.tool_calls ? 'fc_proof_' + callNumber : 'msg_proof_' + callNumber;
    const output = delta.tool_calls
      ? [{ id, type: 'function_call', call_id: delta.tool_calls[0].id, name: delta.tool_calls[0].function.name, arguments: delta.tool_calls[0].function.arguments, status: 'completed' }]
      : [{ id, type: 'message', role: 'assistant', status: 'completed', content: [{ type: 'output_text', text: delta.content, annotations: [] }] }];
    let sequence = 0;
    function event(type, data) { res.write(`event: ${type}\ndata: ${JSON.stringify({ type, sequence_number: sequence++, ...data })}\n\n`); }
    const response = { id: 'resp_' + callNumber, object: 'response', created_at: 1, model: input.model, status: 'in_progress', output: [] };
    event('response.created', { response });
    event('response.output_item.added', { output_index: 0, item: { ...output[0], status: 'in_progress', ...(delta.tool_calls ? { arguments: '' } : { content: [] }) } });
    if (delta.tool_calls) event('response.function_call_arguments.delta', { output_index: 0, item_id: id, delta: output[0].arguments });
    else {
      event('response.content_part.added', { output_index: 0, item_id: id, content_index: 0, part: { type: 'output_text', text: '', annotations: [] } });
      event('response.output_text.delta', { output_index: 0, item_id: id, content_index: 0, delta: delta.content, logprobs: [] });
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
    model: responses ? 'gpt-6-astra' : 'openai/gpt-5.4-mini', responses, canAsk: !supervise, supervise }));
  if (repositoryBase) await docker([...common, image, 'prepare']);
  const exportArgs = [...common, '--user=65532:65532', '--network=none', image, 'export'];
  const clean = JSON.parse(await docker(exportArgs));
  assert.equal(clean.baseSha, baseSha);
  assert.deepEqual(clean.files, []);
  await docker([...common, '--user=65532:65532', image, 'code']);
  if (repositoryBase) await docker([...common, '--user=65532:65532', image, 'validate']);
  const artifact = JSON.parse(await docker(exportArgs));
  assert.ok(calls > 0); assert.ok(edited);
  if (supervise) {
    assert.deepEqual([...started].sort(), specialists.map(specialist => specialist.agent).sort());
  } else {
    assert.ok(asked); assert.ok(questionPolls >= 2); assert.ok(replayedFailure);
  }
  assert.equal(artifact.files.find(f => f.path === 'coding-proof.txt')?.content, supervise ? supervisedContent : ordinaryContent);
  console.log('OpenCode image proof passed with ' + calls + ' synthetic requests and offline artifact export. '
    + (supervise ? 'Two parallel specialist reports were integrated.' : 'The owner question and tool failure were replayed.')
    + (repositoryBase ? ' The public clone and Linux test gate passed.' : ''));
} finally {
  server.closeAllConnections(); server.close();
  await docker(['volume', 'rm', volume]);
  assert.equal(dirname(resolve(folder)), resolve(tmpdir()));
  await rm(folder, { recursive: true, force: true });
}
