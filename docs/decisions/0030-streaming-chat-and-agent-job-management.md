# 0030: Streaming chat and agent-managed scheduled jobs

- Status: Accepted
- Recorded: 2026-09-21
- Decision date: 2026-09-21
- Evidence: `AgentChatService`, `ConversationEndpoints`, `PersonalAgentClient`, `ScheduledJobManagementService`, and targeted API/Web tests
- Supersedes / superseded by: extends [0021](0021-scheduled-jobs.md) and [0029](0029-continuous-conversation.md)

## Context

Chat waited for the complete provider response even though the provider client supported streaming. The assistant could create scheduled work but could not enumerate, inspect, edit, or cancel it, leaving the Web dashboard as the only management surface.

## Decision

Stream interactive chat through the Agent Framework streaming API and an authenticated newline-delimited JSON endpoint. Flush text deltas as they arrive, then send the authoritative persisted conversation as a completion event. Browser cancellation propagates through HTTP to the agent run; partial cancelled output is not persisted as a completed interaction.

Add server-bound list, inspect, update, and cancel tools for scheduled jobs. Actor, role, and subject always come from the current access context. Recheck live actor/assignment policy in the management service and tool permission in the existing bound-function wrapper. Owners receive these tools by default; coaches do not. Updates are limited to Scheduled/Retrying jobs and may change timing plus the payload appropriate to the job type. Cancellation retains the durable record and execution evidence; no agent hard-delete tool is introduced.

## Alternatives

SignalR was unnecessary for a single request/response stream; NDJSON works with ordinary authenticated POST requests and cancellation. Server-sent events do not naturally support the existing JSON POST body. Hard deletion was rejected because it would erase execution and authorization evidence. Replacing edited jobs with new IDs was rejected because stable job identity and existing deep links are useful.

## Consequences

The UI can render model output incrementally and offers a Stop action. A disconnect or Stop cancels generation, but any external tool effect completed before cancellation cannot be undone. The final completion event replaces provisional text with the stored, normalized response and presentation metadata.

Editing timing can leave an older transport delivery queued. This is safe because execution reloads the authoritative job and refuses to run before its current due time; the database reconciler dispatches jobs moved earlier. Duplicate deliveries remain expected and serialized by the job lock.

## Delivery and verification

Implemented in API and Web with provider-to-browser streaming, cancellation, job management tools, pending-row locking, and direct-routing adapters for explicit job commands. Targeted tests cover streamed persistence/deltas, client event parsing, Stop/rendering behavior, tool metadata/routing, and list/inspect/update/cancel behavior. Live-provider pacing, browser disconnect behavior, and synthetic end-to-end smoke remain operational verification items.
