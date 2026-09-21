# A personal assistant that grows with us

Recorded 2026-09-21 from the maintainer's product discussion. This document records direction, not a claim that the capabilities below exist. Delivery status and acceptance criteria belong in [the roadmap](roadmap.md).

## Product purpose

Build a digital personal assistant that reliably takes things off our minds. The digital garden metaphor means tending, correcting, pruning and reshaping knowledge and behavior over time. Optimize for a one-person agile team delivering useful increments to the maintainer and spouse; avoid infrastructure or autonomy without a demonstrated use.

The first everyday loop is capture -> organize -> follow through -> learn. Example: capture an invitation, confirm its details, track preparations, surface them at the right time, and stop reminding after completion. Other candidates: returns, errands, shopping lists, maintenance, appliance manuals, warranties, waiting on replies, project next steps, calendar/task integration and useful daily briefs. Home Assistant remains theoretical; simulated home actions must not be presented as real integration.

## One conversation

Opening the assistant should reveal one chat box. People should be able to change topics and return to something a week later without choosing sessions. Internal storage boundaries remain useful, but are not the product's organizing demand on the user.

Build each turn from bounded recent conversation, relevant historical evidence, scoped durable memory, active commitments when implemented, and fresh tool results. Combine semantic and keyword retrieval; tags and classifiers are hints, never the sole path to history. Clearly distinguish something said, something believed, and something agreed to do. Tentative plans must not silently become facts or reminders.

Eventually organize history into overlapping, evidence-linked episodes. Disclose older context when it materially informs a reply, accept corrections, and ask a small clarification when interpretation is ambiguous. Do not claim perfect recall. Each person has private chat; household sharing must be explicit and authorized.

`/clear` means start fresh, not delete. Persist a boundary that excludes previous conversational context from automatic recall. Keep history available for explicit inspection. Saved facts/commitments and deletion need distinct semantics; the initial implementation must state exactly which sources it suppresses.

## Three loops

1. Execution: inspect, reason, act, verify within bounded tool/model work.
2. Follow-through: persist a desired outcome, owner, scope, evidence, progress, permitted actions, budget and next wake-up condition. Wake on relevant events or deadlines; wait otherwise. A delivered notification is not proof a goal was achieved.
3. Improvement: outcome/correction -> reproducible case -> candidate -> evaluation -> review -> limited rollout -> retain or roll back.

MassTransit delivers events; PostgreSQL stores authoritative domain progress. Use Agent Framework for model/tool orchestration, while choosing the durable domain workflow owner deliberately. See [0005](../decisions/0005-durable-garden-workflows.md); that broader workflow remains proposed.

## Classifiers, prediction and learning

Candidate Jev decisions: capture category, relevance to an active goal, correction versus addition, digest versus interruption, uncertain/duplicate/stale memory, and suitable presentation. Deterministic code retains authorization, argument validation and state transitions. Larger models handle open-ended interpretation and explanation. Evaluate classifier changes independently against rules and current behavior; schema validity is not semantic correctness.

Record decision-time inputs or protected references, eligible choices, selected action, model/prompt/policy versions, later corrections/outcomes, latency and cost. Keep private content out of operational logs. Unknown outcomes stay unknown; clicks are not usefulness. Many events from two people do not imply many independent training examples. Begin with narrow ranking/timing predictions and simple baselines, use temporal held-out data and per-person reporting, and consider ML.NET for tabular models.

Self-improvement starts with correctable memory, then evaluated prompts/retrieval/routing, then software changes as reviewable PRs. Keep held-out evaluations outside the candidate's editable workspace. Separate interactive budgets from experiment budgets; prefer recurring, measurable problems over constant speculative self-analysis.

## Mobile and interface

The spouse uses an iPhone. Prioritize a responsive web experience/Home Screen app; native iOS is deferred. MAUI shares code but iOS distribution still requires macOS/Xcode, signing and provisioning. Hosted macOS CI could support later TestFlight distribution. Web push is a separate implementation from existing Android push. An installable Blazor Server app does not become offline-capable without local queuing and synchronization. Native share-in support needs separate validation.

Proposed mobile destinations: Today, Inbox, Ask. Start with in-app capture, clear save status, reliable sign-in, exact notification destinations and a small set of useful actions. Resolve existing mobile shared-key identity problems before broader native distribution.

Improve chat with readable layout, mobile keyboard behavior, recovery/draft preservation, progress, and later streaming/cancellation. Introduce typed commitment cards, checklists and clarification forms, followed by composed timelines/comparisons and persistent artifacts. Render validated data through trusted components; never grant generated UI arbitrary code execution or bypass server authorization. Presentation of a proposed commitment does not mean a reminder was scheduled.

Keep Blazor initially. If it measurably constrains the desired experience, compare a React chat prototype against the same API contracts before a larger migration.

## Cloud experiments

Railway is a candidate for temporary application previews. A coding worker needs its own isolated execution environment and scoped credentials; a second app environment alone is not a code sandbox. Use synthetic data, separate databases/keys/outbound destinations, a capped experiment budget and cleanup. Produce a PR, evaluation report and preview; promotion is controlled separately. Revisit [0003](../decisions/0003-local-parity-and-recovery.md) when a concrete experiment needs hosted validation. Do not clone production secrets or reuse production services in experimental previews.

## Delivery order agreed in this discussion

1. Record this vision and its tradeoffs.
2. Deliver one default conversation and automatic bounded context building.
3. Deliver interface improvements and typed commitment/checklist/clarification interactions.
4. Add reliable capture/follow-through, daily usefulness and explicit household sharing in small tested slices.
5. Grow evaluated improvement and isolated cloud experimentation from observed needs.

Measure successful capture/retrieval/completion, corrections, missed obligations, unwanted interruptions, latency and cost. Each iteration should exercise a real scenario, with synthetic cases for repeatable verification. No live-model quality improvement is established by deterministic integration tests alone.

## Source evidence

- [Current architecture](../architecture.md), [Jev plan](jev-integration.md), [mobile/workflow roadmap](roadmap.md).
- [Agent Framework checkpoints](https://learn.microsoft.com/en-us/agent-framework/workflows/checkpoints) and [human input](https://learn.microsoft.com/en-us/agent-framework/workflows/human-in-the-loop).
- [TypeSafe smart-home example](https://docs.typesafe.ai/demos/smart-home); [Home Assistant Bring integration](https://www.home-assistant.io/integrations/bring).
- [A2UI](https://a2ui.org/) and [AI SDK generative UI](https://ai-sdk.dev/docs/ai-sdk-ui/generative-user-interfaces).
- [Railway preview environments](https://docs.railway.com/guides/preview-deployments-with-pr-environments).
- [MAUI deployment](https://learn.microsoft.com/en-us/dotnet/maui/deployment/), [Apple web push](https://developer.apple.com/documentation/usernotifications/sending-web-push-notifications-in-web-apps-and-browsers), [TestFlight](https://developer.apple.com/help/app-store-connect/test-a-beta-version/testflight-overview/).
