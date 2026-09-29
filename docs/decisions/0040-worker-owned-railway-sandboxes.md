# 0040: Worker-owned Railway sandbox lifecycle

- Status: Accepted; implementation in this change, live Railway verification pending
- Recorded: 2026-09-29
- Decision date: 2026-09-29
- Evidence: maintainer requested fewer deployed services and an agent-callable sandbox capability; `PersonalAgent.Worker/Sandboxes`, `AutomationTools.RunSandboxAsync`, and the existing SQL transport contracts
- Supersedes: the separate controller service placement in [0036](0036-railway-automation-sandboxes.md). Its isolation, gateway and lifecycle policy remain in force.

## Context

The separate automation runner was introduced when the controller needed Docker daemon authority. Production execution now uses Railway's isolated VMs, so the controller needs no host Docker socket. Running a separate controller service adds deployment, configuration and monitoring work. The Worker already consumes durable background messages.

## Decision

Move both C# program and coding-job consumers, the Railway SDK adapter and the cleanup reconcilers into `PersonalAgent.Worker`. Keep the existing MassTransit queues, durable lease/result stores, API-owned authorization, saga/outbox result handling, model gateway and PR publisher. The Worker uses its normal Shared/Worker configuration scope and encrypted Railway settings. The old runner must stop before the Worker starts consuming its queues.

Expose `run_railway_sandbox` as an owner-only, default-disabled agent tool for a one-time bounded C# program. A stable UUID request key is its automation ID. The API persists a single-step, unscheduled run and outgoing saga message in one database transaction; retries with the same source return the same run. The existing `save_automation` tool covers recurring recipes, while `start_coding_job` continues to cover repository changes and draft PR creation. These are two sandbox workloads owned by one Worker deployment, with distinct runtime policies.

## Alternatives

- Retain a separate service: preserves deployment and credential isolation, but adds an operator-managed service whose original Docker privilege is gone in production.
- Offer an arbitrary shell or image tool: would bypass the pinned compiler/coding images, limits and authorization. New workload types must declare their own policy and durable result contract.
- Put execution in the API: would couple long-running provider calls and cleanup to the request/saga host.

## Consequences

Worker replicas receive Railway controller authority and include Node and the pinned SDK package. Worker deployment or outages now affect sandbox dispatch and cleanup as well as its other jobs. Existing database claims and leases still prevent known duplicate execution and retry known cleanup; Railway idle teardown remains the fallback for unknown create outcomes. The local synthetic Docker socket is enabled only by the opt-in Compose override. Production Worker does not mount it.

## Delivery and verification

The Worker build and fake-SDK/Node tests exercise the moved controller. The one-shot API test checks durable dispatch and request-key replay. The existing sandbox, coding and saga tests remain regression gates. A live Railway lifecycle, Worker restart during execution, and one draft-PR coding run remain acceptance checks; local tests do not establish those provider outcomes. See [automation operations](../runbooks/automation-programs.md) and [coding operations](../runbooks/platform-coding.md).
