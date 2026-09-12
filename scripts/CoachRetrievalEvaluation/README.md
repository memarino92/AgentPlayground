# Coaching model evaluation protocol (scorer v2)

This is a local, opt-in paid evaluation, not an application service or automatic promotion workflow. Freeze a JSON case file before running candidates. Keep private cases and outputs under ignored snapshots/ and .artifacts/. Never commit private transcripts, IDs, keys, traces, or raw model answers.

Run the actual AgentChatService, tool registry/binder, embedding provider, PostgreSQL search and session persistence on a local development clone. Only coaching search is enabled; semantic recall and unrelated side effects are disabled. Fresh sessions are used per case/repeat. Provider/model-policy changes are not applied to production. Default settings match the app; no evaluation-specific effort/temperature tuning is applied; the application uses Luna's required tool-compatible setting.

The case schema is EvalCase in Program.cs. Supply expected source IDs and timestamp tolerance, required concept patterns, forbidden patterns, optional expected recency/filename, and no-evidence cases. Treat pattern checks as proxies: they cannot establish full semantic correctness or distinguish every negation. Review raw answers against source utterances before a recommendation. Do not revise expectations after seeing outputs merely to increase a score. Keep a new version for revised rubrics. These initial cases are a tuning set, not held-out generalization evidence.

For the first comparison, run six fixed cases (latest exact question, latest paraphrase, general guidance, exercise transition, missing recording, and empty subject) three times on each model. Record every trial; provider errors and timeouts fail instead of disappearing from denominators. Repeats measure variability, not independent evidence from a larger corpus. Report pass counts by check as well as all-check passes. Required: relevant cue, supported source/timing, exact same-turn citation membership, correct tool mode/scope, and useful missing-data behavior.

Environment variables:

- COACH_EVAL_CONNECTION: local PostgreSQL clone connection string (remote hosts rejected).
- OPENAI_API_KEY: process-only provider credential.
- COACH_EVAL_CASES: private JSON dataset path (Version=1 or 2, Name, Cases).
- COACH_EVAL_MODELS: comma-separated reviewed IDs.
- COACH_EVAL_REPEATS: 1-10, default 3.

Run from the repository root:

```powershell
dotnet run --project scripts/CoachRetrievalEvaluation/CoachRetrievalEvaluation.csproj
```

The runner writes a unique ignored output directory with a dataset hash, source hashes, model inventory, settings, response model IDs, raw answers, tool calls/results, per-check scores, observed latency, chat usage, results.json, report.md, and junit.xml. Exit code 1 means at least one trial failed. JUnit can be uploaded as CI test evidence in a deliberately configured private live-eval job; ordinary tests never invoke paid models. Raw private artifacts must not be uploaded to a public CI job.

Latency includes the application round trip and embeddings. Usage is chat tokens reported by the provider; missing values remain null per trial. Embedding token usage and dollar costs are not measured. API list availability does not guarantee an inference request will succeed. Per-trial timeout is 120 seconds. The runner uses the existing cloned schema and does not migrate or seed source data; new evaluation chat sessions remain in that disposable clone.

An optional in-memory credential path accepts COACH_EVAL_SECRET_ROW (a JSON object with scope and encrypted value) and COACH_EVAL_CONFIG_KEY instead of OPENAI_API_KEY. It uses the application configuration crypto implementation. The runner does not retrieve production secrets or connect to production itself.

## Rubric v2 and offline rescoring

The first scorer was calibrated against actual output and application citation parsing. V2 accepts bare evidence URLs and valid Markdown whitespace, and the private missing-data cases use a sentence-start affirmative-attribution pattern so a statement such as "I cannot report what your coach said" is not misclassified. These corrections do not weaken expected source IDs or timestamp requirements. Original v1 artifacts remain unchanged; apply v2 to every saved trial, not just a preferred model.

To rescore without API credentials or provider calls, set COACH_EVAL_CASES to the version-2 case file and COACH_EVAL_RESCORE to a completed evidence directory, then run the same executable. It creates a new rubric-v2 directory with its own dataset/scorer hashes, results, report, and JUnit. This is deterministic rescoring of saved answers, not a new model run. Clear COACH_EVAL_RESCORE before a new live run.

Current application compatibility: Luna function tools on Chat Completions use reasoning_effort=none, as required by an observed provider HTTP 400. The runner adds no further model-specific settings. The evaluated aliases are recorded alongside actual response model IDs in the trace; aliases may change, so subsequent comparisons must retain their manifests. The snapshot export manifest remains the corpus provenance; raw source snapshots and case copies should be retained with private run evidence.
