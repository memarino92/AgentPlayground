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
