# 0049. Per-turn topic selection for continuous chat

Recorded 2026-11-19. Implemented as a default-enabled extension to [decision 0029](0029-continuous-conversation.md). The existing content-sharing setting and credential govern provider access.

## Context

The fixed recent window can carry an unrelated exchange into the next prompt. Conversely, a short follow-up such as “What about the second one?” contains little vocabulary for historical search. The maintainer requested a content- and time-aware decision for each new message.

## Decision

Ask Jev once per eligible turn to select a starting message from a bounded window or identify a new topic. Use the same selected messages for recent replay and query expansion. Keep the durable conversation ID and the user's explicit fresh-start boundary. Existing retrieval enforces access scope, ranks historical candidates and bounds the evidence sent to chat. The fixed-choice contract supplies probability and confidence for the fallback decision. [TypeSafe Choice](https://docs.typesafe.ai/primitives/choice)

## Alternatives

A fixed inactivity timeout would split delayed replies and miss immediate subject changes. Persisting episode assignments for the full archive would require backfill and a policy for revising past labels. Per-turn selection lets us evaluate the benefit before adding durable classification.

## Consequences and verification

Classification sends bounded recent content and timing metadata to the configured provider and adds one decision call to the chat path. Unavailable or uncertain decisions retain the fixed-window baseline. Synthetic tests cover selected suffixes, topic switches and confidence fallback. Live accuracy, latency and retrieval improvements require a held-out comparison. See the [roadmap](../plans/roadmap.md) and [configuration instructions](../runbooks/continuous-conversation.md).
