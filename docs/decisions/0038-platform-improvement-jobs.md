# 0038: Platform improvement jobs

- Status: Accepted for explicit chat-to-PR slice; locally implemented, live acceptance pending
- Recorded: 2026-09-27
- Decision date: 2026-09-27
- Evidence: maintainer authorized implementation, retained merge control, and selected OpenCode/OpenRouter for paid execution with a $5 live-proof model cap.

## Context

The assistant should implement platform source, prompt and skill changes and propose them as PRs. A later milestone should select small backlog work daily. Existing recipe automations and the Railway C# runner provide scheduling and isolated execution, but their short single-program profile is unsuitable for repository development. See [0034](0034-agent-automations.md) and [0036](0036-railway-automation-sandboxes.md).

## Decision

Deliver an owner-only, default-disabled explicit coding job first. Store encrypted provider/App settings, actor, stable request identity, instruction, approved repository/base SHA, checkpoint/image, limits, sandbox identity, artifact, validation and PR identity in PostgreSQL. Separate execution state from cleanup. Current owner/tool permission is checked at submission, during model access and before publication.

A committed job row is durable dispatch intent. The coordinator resends Queued rows via SQL transport; an atomic database claim allows one Running job across replicas. This slice uses a long-running bounded consumer and a separate cleanup reconciler, rather than embedding repository work in the existing automation saga/outbox. A claimed job is never automatically rerun after an uncertain execution; its deadline closes it. This is simpler for explicit jobs but does not yet provide recipe child-job composition.

A pinned OpenCode CLI runs inside an immutable Docker image on a separate isolated Railway checkpoint. Coding/testing is unprivileged with bounded resources, a read-only root and no controller socket. The code receives a capability for an API-owned model gateway, never the real OpenRouter, GitHub, Railway or production database credentials. The gateway restricts the model/provider, input types, output size, prices and atomic request count. Every attempted call reserves $0.40, never refunded on uncertainty; at most twelve calls bound model spend to $4.80 per job. Railway usage remains separate. No provider fallback is allowed.

A fresh offline export container reads the candidate diff against the pinned base. The API publisher validates the bounded regular-text artifact and test results, then uses GitHub data APIs without executing candidate code. A repository-scoped App creates a stable feature branch and draft PR. Deterministic commits and a stable job marker reconcile lost responses without duplicate PRs or force overwrites. Required approval with no App bypass is verified at start and publication using classic protection or a supported repository ruleset. No merge/deploy operation is exposed.

Settings use the existing encrypted database administration flow with masked secret presence, explicit keep/replace/clear actions, optimistic revisions and live disablement. Jobs retain private artifacts/checks and expose progress/cancellation in an owner dashboard. The runtime remains disabled pending setup and live acceptance. See the [operations runbook](../runbooks/platform-coding.md) for exact limits and recovery procedures.

## Provider choice and source evidence

The maintainer preferred subscription-funded Codex if supported, then explicitly selected OpenCode and OpenRouter whenever paid inference is required. AgentPlayground is public. We do not copy saved ChatGPT credentials into the worker or silently substitute OpenAI API funding.

Sources checked 2026-09-27:

- [Codex authentication](https://learn.chatgpt.com/docs/auth) and [CI/CD authentication](https://learn.chatgpt.com/docs/auth/ci-cd-auth): separate subscription/API modes; saved account CI credentials are not the supported public-repository path used here.
- [OpenCode providers](https://opencode.ai/docs/providers/), [configuration](https://opencode.ai/docs/config/) and [CLI](https://opencode.ai/docs/cli/): custom compatible endpoint, inline settings and structured non-interactive execution.
- [OpenRouter provider routing](https://openrouter.ai/docs/guides/routing/provider-selection): provider allowlisting, fallback control and price ceilings. Calls fail if no endpoint satisfies these constraints.
- [GitHub rules REST API](https://docs.github.com/en/rest/repos/rules): effective branch rules and ruleset bypass actors support the publication preflight.

## Subsequent milestones

Recurring backlog selection remains proposed. Reuse the existing scheduler through a durable child-job action, with explicit eligibility/size metadata in the repository roadmap, atomic backlog claims, outstanding-PR caps, no-work outcomes and merge/closure reconciliation. P1D is an interval, not a timezone-aware calendar appointment. Opening a PR does not complete a backlog item.

Review follow-up, bounded CI repair, pinned upstream skill packages (including dotnet/MassTransit), feedback evaluation and runtime skill editing remain separate. Generic personal tooling must not become a repository build prerequisite. Source, bundled skills and prompts can already be proposed through the reviewed coding path; generated changes cannot alter the current run's trusted image, publisher or budget.

## Alternatives and consequences

Running arbitrary repository commands in the existing C# action would inherit unsuitable lifetime/resource limits. Putting a broad GitHub token in the candidate container would weaken merge control. A complete learning/skill-management system first would delay the concrete PR workflow. OpenCode/OpenRouter follows the maintainer's paid-provider choice while keeping publication independent of model credentials.

The chosen slice adds durable lifecycle and recovery code. It supports only public repositories and bounded text changes. Linux validation must use an appropriate selected project/filter: no Docker socket, private test services or mobile SDK is provided. The default test filter is a smoke check, not full platform validation. Lost infrastructure can prevent artifact recovery; idle timeout backs up cleanup for unidentified sandboxes. Live provider quality and live Railway behavior require an acceptance run.

## Read-only ruleset verification (2026-09-28)

Live preflight found that GitHub omits `bypass_actors` for the publisher's Administration:read installation token. The [REST documentation](https://docs.github.com/en/rest/repos/rules#get-a-repository-ruleset) limits that field to callers with write access to the ruleset. Treating omission as an empty list is unsafe; granting administration-write would let the publisher weaken its own merge restrictions.

Keep read-only administration. A deployment administrator may attest that the bypass list contains only individual humans by storing the repository ruleset ID and exact `updated_at` in database-backed coding settings. Start and publication still verify an active, applicable repository rule requiring approval and compare its current revision. A visible forbidden bypass always overrides attestation. Changing repository/App identity requires clearing verification first; changing verification also blocks publication of jobs holding the old settings. This relies on GitHub's revision timestamp and the administrator's review, rather than independently retrieving the hidden list. Timestamp granularity is seconds; do not edit rulesets while jobs are active. A changed or missing revision fails closed and requires review again. No additional publisher privilege is granted.

## Verification status

Local API/Web/runner and fake-SDK tests plus a Docker OpenCode smoke proof cover the implementation. The Docker proof uses synthetic model responses and demonstrates a real tool edit and separate artifact export with no model spend. Lost GitHub response recovery uses the real job settings store and a fake GitHub server. No live Railway/OpenRouter coding run or generated PR is claimed. The repository's existing ruleset required zero approvals when inspected; setup must strengthen that policy and provision the GitHub App before enablement. Delivery criteria remain in the [roadmap](../plans/roadmap.md#platform-improvement-jobs--first-slice-2026-09-27).
