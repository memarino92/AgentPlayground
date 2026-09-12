# Coaching retrieval baseline

Baseline v1 lives in `PersonalAgent.Tests/Services/CoachRetrievalEvaluationTests.cs`; processing coverage lives in `PersonalAgent.Worker.Tests/Services/CoachTranscriptProcessingServiceTests.cs`. Fixtures are synthetic and contain no private recording names or transcript excerpts. Fourteen retrieval/chat cases cover legacy data and application wiring.

Run with Docker available:

```powershell
dotnet test PersonalAgent.Tests/PersonalAgent.Tests.csproj --filter FullyQualifiedName~CoachRetrievalEvaluationTests --logger trx
dotnet test PersonalAgent.Worker.Tests/PersonalAgent.Worker.Tests.csproj --filter FullyQualifiedName~CoachTranscriptProcessingServiceTests --logger trx
```

The real PostgreSQL/pgvector search runs against three-dimensional stub embeddings. The relevant untagged yoke cue has a worse vector distance than six unrelated chunks. Passing requires the cue and authorized timestamped citation to survive top-five retrieval with lowercase, padded, and inflected exercise hints. Recording-scoped cases cover case-insensitive filenames, absent/unknown exercise hints, a missing file, literal wildcard input, and a subject with no records. Any other subject's private marker fails the case. Processing must retain a second-utterance cue, its source window, and a yoke tag. TRX captures case outcomes and elapsed test duration; it is not a production latency benchmark.

For a future live-model evaluation, use the synthetic question “What should I change during my yoke carries?” followed by “Check practice-a.m4a.” The expected answer identifies inhaling while approaching the pickup and restarting promptly after the turn, with the returned source citation. Grade both required cues, citation validity, unsupported additions, and whether a filename correction triggers scoped retrieval. An empty corpus must produce an evidence limitation rather than a claim that the coach never gave advice. Transcript instructions must not override system or authorization rules.

Three cases verify vector ranking with a paraphrased query and no matching tag (null, empty, and unknown hints). These use deliberately assigned vectors to test ranking mechanics, not embedding-model understanding. Two cases exercise `AgentChatService`, its real function-invocation loop, bound registry tool, PostgreSQL session persistence, and search. Each recreates the application services and starts two fresh chats against previously stored untagged chunks. The scripted chat client requests the tool with and without a filename, attempts to supply another subject, and checks that only authorized evidence reaches its response. The source tags must remain empty afterward: no reprocessing or repair writes are allowed.

No live model evaluation was run for this change. Such a run must record the model, settings, prompt/code version, tool arguments, retrieved IDs, answer, latency, and available usage/cost (unavailable is not zero). Conflicting corrections, malicious transcript content, held-out questions, and measured production-scale ranking comparisons remain LAB-01/MEMORY-02 work. No automatic promotion is configured.

After merging, deploy/restart the API and start a new chat. The search change needs no re-indexing, re-upload, or Worker run for existing chunks. New Worker processing adds the yoke tag independently. An uploaded recording with no processed chunks still cannot be searched by this tool; use the authorized transcript/processing status view to investigate it. The original reported production conversation was not replayed; the automated evidence uses a synthetic corpus.

## Recording recency iteration

Search accepts `recency=latest|recent|relevance` (default recent). Latest selects the newest filename timestamp before retrieving chunks; an unindexed newest call cannot silently fall back to an older call. Exact filename takes precedence. Unknown recording dates prevent definitive latest-call selection; upload time is never substituted. Recent uses a bounded 60-day age penalty within exercise relevance groups, while relevance preserves semantic ranking for historical questions.

Filename/date provenance and individual utterance links accompany candidate evidence. This exposes exercise transitions but does not constitute semantic exercise classification. Chat instructions require initial-answer citations and forbid attributing a cue solely from nearby exercise mentions.

The expanded synthetic suite covers recency, batch upload order, date validity, missing newest-call chunks, unrelated newest-call content, historical scope, and separate utterance timestamps. No re-indexing or Worker change is required. A private production snapshot was restored locally and authorized live replays ran with gpt-4o-mini and text-embedding-3-small through the application chat/tool path. The fixed cases cover an explicit latest-call request, a latest-feedback paraphrase, general exercise guidance, and a historical exercise-attribution question. Private answers, tool arguments/results, usage, and timings remain under ignored artifacts. The final local answer recovers the intended recent cue; this is bounded case evidence, not a held-out quality benchmark. Earlier iterations exposed omitted optional arguments, lost topic context, and hallucinated evidence hostnames. Optional arguments now have defaults, retrieval includes four preceding utterances, and answer citation repair accepts only exact destinations found in this turn's tool results. Chat token usage was recorded; dollar cost and embedding usage were not measured. See [decision 0017](../decisions/0017-coach-recording-recency.md).

Final local verification: 159 API tests passed, followed by 33 targeted retrieval/citation tests after the final two hostname-repair cases. Four final live questions returned the intended recent cue or correct historical exercise attribution with usable recording links. Exact utterance selection and answer brevity still vary; one link begins less than a second before its supporting coach utterance. This is a tuning set, not a held-out benchmark.

## Formal low-cost model comparison (2026-09-12)

The repository now has an opt-in [evaluation runner and protocol](../../scripts/CoachRetrievalEvaluation/README.md), with versioned private datasets, repeats, per-check grades, response/tool traces, source/dataset hashes, token usage, latency, source snapshots and JUnit results. It supports offline rescoring without model calls. Default CI tests the scorer and application with synthetic providers; it does not send private data or run paid evaluations. See [decision 0018](../decisions/0018-coach-model-evaluations.md).

108 live trials were completed: four models × six cases × three repeats (72), followed by two affected models × the same six cases × three repeats (36). Rubric v2 rescored all saved outputs consistently after correcting valid bare/whitespace citation parsing and a negated-attribution false positive. Original v1 evidence remains preserved. These cases are a tuning set, not held-out generalization evidence.

| Model / evaluated candidate | Passes (all checks) | Median successful latency |
| --- | --- | --- |
| GPT-4o Mini / baseline | 14/18 | 2.16 s |
| GPT-5 Mini / baseline | 16/18 | 20.32 s |
| GPT-5.4 Mini / post-fix | 16/18 | 1.81 s |
| GPT-5.6 Luna / post-fix | 17/18 | 3.59 s |

These rows identify different application candidates explicitly. Luna initially failed all 18 baseline trials: the provider rejected function tools with its default reasoning on Chat Completions. The application now sets reasoning effort to none for Luna tool calls; moving to Responses is a separate migration. GPT-5.4 Mini exposed escaped slash citations, now repaired only against exact same-turn tool destinations. Both post-fix models passed all nine breathing/recency cases. Luna's remaining failure linked to the preceding exercise explanation rather than the exact requested cue; GPT-5.4 Mini omitted links twice. GPT-5 Mini was slower and once included adjacent exercise guidance in a general answer. Exact citation and semantic-quality work is not complete.

The live account listed GPT-5.4 Mini and Luna but not a GPT-5.5 Mini ID. Reviewed model references: [GPT-5.4 Mini](https://developers.openai.com/api/docs/models/gpt-5.4-mini), [Luna](https://developers.openai.com/api/docs/models/gpt-5.6-luna). Model inventory alone did not establish inference compatibility, as the Luna failure demonstrated. All keys remained process-only, all raw evidence remains private, and production policy/deployment were unchanged. Final API/scorer suite: **172 passed**.

Pre-PR review added guards against zero-model live runs and empty saved-output rescoring being reported as passing evaluations. Final API/scorer verification after that guard: **173 passed**.
