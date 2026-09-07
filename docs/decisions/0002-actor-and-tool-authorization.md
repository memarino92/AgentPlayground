# 0002: Carry actor identity and authorize tools by role

- Status: Accepted, retrospective
- Recorded: 2026-09-07
- Decision date: Not reconstructed; implementation commit `2dc6b22`
- Evidence: `SignedActorFilter`, `AgentAccessContext`, `ToolAccessService`, `PostgresCoachAssignmentStore`, Web authentication extensions

## Context and decision

The application serves an owner and coaches with different data access. Web authentication assigns roles using GitHub/Google allowlists. Web forwards signed actor headers to the private API. Chat access separates actor, role, and subject profile; coach access resolves athlete assignments on the server. Tool permissions use a database-backed role matrix with defaults and an admin UI.

This records the implemented direction. The inferred rationale is to support shared use without treating every signed-in user as the owner.

## Alternatives and consequences

A shared profile or client-supplied role would make actor attribution and coach isolation unreliable. Independent identity infrastructure for each service would add operational work. The existing server-to-server signature mechanism is retained, with the internal API key and actor signing key kept on trusted servers.

RBAC is not uniform across all endpoints: mobile registration, approvals, and scheduling still rely on the internal API key and request identities. Android stores that shared key in preferences. Scheduled task execution signs requests as Owner. These are outstanding trust-boundary issues, not capabilities this record claims are solved.

## Delivery and verification

Tool access tests and scoped chat checks exist. Add negative tests for cross-profile access, unauthorized approval decisions, and role propagation through scheduled work. Modernize mobile authentication before treating it as a public client; do not distribute the actor signing key to make the existing API calls work. See [the roadmap](../plans/roadmap.md).
