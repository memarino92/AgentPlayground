# 0005: Grow agent behavior through durable, evaluated workflows

- Status: Proposed
- Recorded: 2026-09-07
- Evidence: `AgentChatService`, transcription consumers, approval services, `AgentTaskExecutionService`

## Context and decision

The system already has retrieval, scoped tools, scheduled tasks, and approvals. However, chat creates a fresh framework session per turn and reconstructs user/assistant messages; only model selection is serialized as session state. Scheduled tasks open a chat session and send an instruction. This is useful orchestration, but not yet a resumable multi-step workflow.

Use the coach check-in flow as the first durable Agent Framework workflow: transcribe, wait for speaker attribution, extract structured cues, validate grounding, request review when needed, then persist approved knowledge. Persist workflow checkpoints and pending human requests so a restart can resume the same work. Keep MassTransit as delivery infrastructure with idempotent step boundaries; distinguish transport retry from workflow replay.

After that works, add a garden review workflow that proposes memory corrections and follow-up tasks. Store outcome feedback and versioned prompts; evaluate proposed changes on synthetic or privately held cases before human approval and rollout. “Self-improving” means measurable, reviewable changes with rollback, not unrestricted production code/configuration modification.

## Alternatives and consequences

Continue ordinary tool-calling chat for conversational requests. Adding multiple agents to every request increases cost and coordination without necessarily improving quality. Use specialist agents only when a benchmark shows value, for example independent grounding checks of extracted coaching claims.

Framework checkpoints and human-in-the-loop primitives are documented by Microsoft. Compatibility with the repo's pinned packages must be verified in a small C# implementation before choosing APIs or changing package versions:

- [Workflow checkpoints](https://learn.microsoft.com/en-us/agent-framework/workflows/checkpoints)
- [Human-in-the-loop workflows](https://learn.microsoft.com/en-us/agent-framework/workflows/human-in-the-loop)

## Delivery and verification

Proposed, not implemented. Acceptance: restart while awaiting review; reject a stale/unauthorized decision; resume once after an authorized decision; prevent duplicate final writes on redelivery; report step latency, model/tool usage, grounded extraction quality, and review corrections. Use those results to decide whether more agents help.
