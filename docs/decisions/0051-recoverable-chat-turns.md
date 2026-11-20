# 0051. Server-owned chat turns

Accepted for the second chat activity PR, 2026-11-19. Extends [continuous conversation](0029-continuous-conversation.md) and its saved presentation.

## Context

A Blazor circuit could follow an active turn across navigation, but a new circuit had no way to identify or observe it. Canceling the streaming HTTP request could also interrupt execution after a tool had already changed something.

## Decision

Web and Android assign a UUID on Send. PostgreSQL admits one active run per conversation and remembers the submitted UUID. A hosted service in the API claims the run with a database advisory lock and uses the existing agent, scoped tool registry and transcript writer. A restarted observer reads committed text and tool events from the beginning of that run. The final transcript supplies the authoritative answer. Stop sets an explicit cancellation request.

The run references a conversation ID. The current default conversation, future named chats and privately implemented tools can use the same execution model. Neither the journal nor the clients need to know a tool's provider or arguments to display its activity.

## Alternatives

Circuit-scoped background work would lose its identity on refresh. Moving the agent into the existing Worker service would require moving API-owned provider, authorization and tool dependencies alongside it. The API-hosted service keeps one execution path while allowing multiple API replicas to share the queue.

## Consequences and verification

A queued run can be claimed after a restart. If the executing process disappears after claiming it, another instance records the interruption and known tool statuses. An external operation may already have succeeded when its completion event is missing. Recovering that case automatically would require a provider-specific operation identity and reconciliation contract.

The stream journal adds database writes and polling reads. Text is batched, with tool transitions committed before dispatch and after return. Finishing a run removes its transient text deltas after the transcript is saved. Run rows and their tool events belong to the conversation and cascade on session deletion. The gated two-worker regression and the local browser refresh walkthrough exercise reattachment and count tool invocations.
