# 0021: Authorized, durable scheduled jobs

- Status: Accepted direction; implementation in progress
- Recorded: 2026-09-12

## Context and decision

Implement TRUST-01 and DEBT-01 together with a scheduled-agent-task slice of FLOW-02. Existing TaskId becomes the application job identity. API owns the job record, authorization, execution state and result; MassTransit remains responsible for delivery. Worker requests execution by ID and cannot provide an execution role or replace stored instructions.

Persist originating signed actor, email, subject, source conversation, timing, instruction, result conversation and attempts. Resolve current scheduling authority from active Shared/Web database sign-in allowlists, current coach assignments and tool permissions at creation, execution and each scheduled tool invocation. Database policy is authoritative for background work: legacy environment-only sign-in access is insufficient. Missing trusted actor records never acquire Owner authority.

Use a PostgreSQL session advisory lock per job, short state transactions, and a durable Running marker before agent execution. Duplicate delivery returns the committed result. A restart can recover a saved assistant response; interrupted execution without a saved response becomes NeedsReview, because an external effect may already have happened. Do not automatically replay uncertain effects. Failures before Running can retry with bounded attempts. Persist terminal state and completion notification in one outbox transaction. Reconcile due jobs periodically so exhausted transport retries or an API restart do not strand jobs.

List/detail endpoints enforce current subject access. Coaches see only jobs they scheduled for currently assigned subjects; subject owners can see jobs for themselves. Conversation links remain actor-scoped. Cancel only pending/retrying jobs under the execution lock. Show attribution, timing, state, outcome and attempts in Web. Recurrence, editing, manual retry and a general workflow engine are deferred.

## Alternatives and consequences

Transport IDs alone cannot represent business completion. Retaining a saved role would preserve revoked access. Replaying an entire interrupted agent turn could repeat paid or external effects; conservative review sacrifices automatic progress to preserve safety. This is not a general exactly-once external-effects guarantee or a resumable Agent Framework session.

Existing queued messages without an application record are recorded as Blocked legacy jobs when delivered, without executing their instruction. Their claimed subject is retained for owner inspection, their scheduler is explicitly unknown, and no completion notification is sent. Undelivered legacy schedules cannot be enumerated from application state before their first delivery.

## Delivery and verification

Planned milestones: persistence and policy; execution/recovery and cancellation; dashboard and integrated tests. Verify real PostgreSQL concurrency, rollback/outbox, restart, duplicate delivery, revocation, cross-subject access, forged messages, legacy delivery and UI states. Update this section with actual results before completion.

Evidence: SchedulingService, AgentTaskExecutionService, AgentTaskSchedulerConsumer, SignedActorFilter, ServiceCollectionAuthenticationExtensions, CoachCallOutbox and roadmap TRUST-01/DEBT-01/FLOW-02.
