# 0055: Durable mixed-workflow children (option B)

- Status: Accepted for the owner-selected option B; implemented and verified locally. Railway acceptance remains pending.
- Recorded: 2026-11-22
- Decision date: 2026-11-22
- Evidence: Owner request; decision [0054](0054-resumable-supervised-work.md); `AutomationStateMachine`, `AutomationStepConsumer`, `AutomationRegistration`, `CodingJobStore`.

## Context

The existing request-level supervisor persists serial recipe steps with an EF PostgreSQL saga/outbox. Coding already has an actor-scoped stable request key and its own durable job/budget/Worker outbox. Public research is bounded read computation, but a single parent cursor prevents independent research and coding from overlapping.

The supplied public evidence does not contain the official OpenCode v2.3.2 `subagent-report.ts` source. It is not evidence of native durable inbox/outbox guarantees. This design relies on this repository's persistence and tests, not an inferred OpenCode implementation.

## Decision

Opt in with `parallelChildren: true` on a one-time supervised recipe. Only independent `public_research` and `coding_job` steps overlap. Other actions remain source-order barriers. Optional `dependsOn` lists distinct earlier IDs; coding `evidenceSteps` and `when` imply dependencies. Earlier-only references bound the graph and exclude cycles. Recipes without the option retain their existing serial behavior, keys and message defaults.

Use the existing step rows as the child inbox: stable child UUID, owning run/definition (actor, subject and chat), status, fenced attempt, lease, deadline and terminal output. The saga lock serializes integration; the EF consumer outbox commits output, effects, progress and outgoing dispatch together. Distinct duplicate messages and superseded attempts cannot integrate twice. A committed research completion in the outbox is replayed, not recomputed.

Commit a research/coding claim before provider I/O. Reconciliation under the saga lock also locks step rows before reading leases, so it cannot overwrite a concurrently renewed claim. Bounded keyset sweeps visit all active parents and stale watchdog candidates; due scheduling excludes already-active definitions. Recovery redelivers only eligible expired work, bounds interrupted child dispatch to three attempts within five minutes, and polls existing coding jobs without starting a new job or budget. The coding job's own overall deadline, cancellation and shared provider allowance remain authoritative. Read research calls can repeat after uncertain interruption; this is not exactly-once provider billing. Each attempt retains existing question, token, output and 45/60-second limits.

Revalidate live owner, subject, action and selected workflow-model grants on execution, reconciliation and integration. No result authorizes new capabilities. Terminal parents stop new dispatch/integration; existing coding cancellation remains terminal child evidence. Parent failure commits coding-cancellation intent through the consumer outbox. The coding store retains an actor/request-key cancellation tombstone, serialized with job creation, so cancellation acknowledged before repository preparation finishes still prevents a late-created job from being claimed. An action failure/revocation terminates the parent and blocks unfinished descendants; this does not promise rollback of an already-running external action or publication.

## Alternatives

- Keep serial request execution: simplest, but does not satisfy concurrent independent children.
- Add a separate workflow engine or generic subagent platform: duplicates saga, authorization and outbox boundaries.
- Share a mutable coding checkout or create multiple coding children: expands writer/budget scope; retain one coding child and the existing sole-writer publication gates.
- Persist individual research questions: deferred; this slice checkpoints the bounded research-step result, not partial provider phases.

## Consequences

One additive automation-schema migration and an additive cancellation table initialized through the existing coding-store bootstrap; no new production settings, transport, provider credentials or deployment changes. Recovery can repeat bounded public reads but not parent effects or coding identity. Non-child actions retain conservative existing timeout behavior rather than acquiring unsafe generic retries. Parent pause still only affects future scheduling; it is not redefined as cancellation.

## Delivery and verification

Implemented in the existing saga, step consumer/store and scheduler, with the additive `DurableWorkflowChildren` migration. `AutomationTests.ParallelChildrenRestartReplayPreservesDependenciesAndIdentity` runs against PostgreSQL and the real MassTransit SQL transport with deterministic search/model/publisher boundaries. Its four cases cover queued and Worker-claimed coding at API restart, research dependency evidence and parent-failure cancellation. It expires only the interrupted research lease, verifies the same coding identity/attempt/budget, waits for stale/duplicate messages to be consumed, and asserts one report exactly equal to both committed outputs. `CommittedResearchCompletionReplaysWithoutCallingProviderAfterRestart` interrupts parent integration after the research completion outbox commits, rebuilds the host and proves delivery without another provider call. Other regressions cover cancellation before coding creation, concurrent claim/recovery locking, live grant revocation at integration, attempt/deadline eligibility, program watchdogs and scheduler fairness. Existing serial restart/outbox/report behavior is retained; serial duplicate assertions now also wait for inbox delivery.

Fresh local evidence on 2026-11-22: five SQL restart/replay cases passed; the combined focused automation/workflow/research suite passed 66 tests, zero failures/skips. An additional 23 coding tests and four Web automation tests passed. The direct Android project build succeeded with zero warnings/errors. Exact commands and acceptance criteria are in the [roadmap](../plans/roadmap.md); test bootstrap is in the [automation runbook](../runbooks/automations.md#verification-without-docker). No live provider, Railway, push, PR or deployment was exercised. Railway checkpoint acceptance from 0054 remains separate and unverified.
