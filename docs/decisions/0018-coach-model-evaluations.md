# 0018: Repeatable local coaching model evaluations

- Status: Accepted; implemented; first comparison completed
- Context: Maintainer asks to verify low-cost model compatibility with formal testing evidence.

## Decision

Keep a repository-owned, opt-in evaluation executable that exercises the application's chat, tool-binding, retrieval, embedding, and session-persistence path against a local database clone. Run a versioned private case corpus across explicit model IDs with repeated fresh sessions. Disable unrelated tools and semantic recall so the evaluation scope is explicit. Do not change production model policy as a side effect of evaluating a candidate.

Freeze case expectations and source hashes before the comparison. Persist per-check results, raw answers, tool calls/results, actual response model IDs, latency, available token usage, a manifest, a summary and JUnit evidence. Failed provider requests remain failures in the denominator. Preserve missing usage as unavailable. Source data and raw traces remain ignored/private; commit the runner, scorer tests, protocol, and aggregate findings only.

## Alternatives and consequences

Ad hoc replay is useful for diagnosis but does not establish consistent cross-model behavior. Provider-hosted evals could manage datasets and dashboards, but a local runner directly exercises this repository's current .NET orchestration and database retrieval. LLM-only grading introduces another model and judge calibration problem; this first slice uses deterministic criteria plus separately recorded source review. Pattern checks are explicitly limited proxies, not a groundedness proof.

Six cases repeated three times do not constitute eighteen independent tasks. The initial cases are a tuning set; no held-out quality or statistical significance claim is made. A future private CI job may consume JUnit, but default CI and unit tests do not make paid calls or upload private artifacts. No automatic promotion is configured.

## Implementation and validation

The runner is `scripts/CoachRetrievalEvaluation`; scorer tests exercise wrong source/timing, unsupported claims, absent tool calls and fabricated citations. 108 live trials completed: 72 baseline plus 36 post-fix. Versioned offline rescoring corrects citation-parser and negated-attribution false positives while retaining original scores. Post-fix Luna passed 17/18 and GPT-5.4 Mini 16/18 under the corrected deterministic rubric; remaining failures concern exact citations. 172 API/scorer tests pass. See the evaluation runbook for scope and limits. API keys enter only through process environment, optionally as an encrypted configuration row plus its separate master key; plaintext is not written to artifacts.
