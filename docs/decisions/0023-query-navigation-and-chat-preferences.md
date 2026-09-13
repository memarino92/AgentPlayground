# 0023: Query navigation and chat preferences

- Status: Accepted; implementation recorded retrospectively
- Recorded: 2026-09-13
- Decision date: 2026-09-13
- Evidence: maintainer request; `Chat`, `ScheduledJobs`, `Settings`, `CoachCheckinsAdmin`, `AgentChatService.SetModelAsync`

## Context

Selecting a job or chat did not consistently update the URL. Following a conversation link and returning with Back lost the selected job/filter. Separate owner and coach recording pages duplicated transcript presentation. The chat picker changed local state without updating the persisted session model.

## Decision

Use query parameters for selected records, list filters and settings sections. Reconstruct views from those parameters on navigation; do not push the current URL again. URL values identify data and never authorize access. Existing signed-actor and subject checks remain authoritative.

Show a local chat draft immediately. Persist a chat when sending its first message, reuse its ID on retries, and keep an empty saved chat when New chat is clicked after an unsuccessful send. Persist explicit model changes in existing session JSON through an authenticated endpoint, serialized with message generation and disallowed for scheduled-job records. Remember the last explicit choice in protected local browser storage scoped by actor and athlete; use it for new chats only while available in the catalog. This preference is browser-local; each saved chat's model is server-owned.

Use one recording page for owners and coaches with `AuthorizeView` around owner controls. Reuse the evidence player inline, with a shared compact transcript timeline and optional follow-along enabled by default. Consolidate integration access into Settings.

## Alternatives

Keeping selection only in component/browser storage cannot reproduce links or Back navigation. Creating a saved chat on every page load generates blank records. Separate transcript pages duplicate rendering and access-control affordances. A new preference database table was unnecessary for remembering a browser's last selection; existing session JSON already stores the per-chat model.

## Consequences

No database migration, provider change, or new environment variable is required. The API validates model availability and chat ownership. Failed message attempts can leave an empty saved chat, which is reused while selected. Existing historical empty chats are not bulk-deleted. Recording dates are displayed only when the established filename timestamp parses, without inventing a timezone or substituting upload time.

## Delivery and verification

Implemented on `feat/navigation-chat-transcripts`. Web regression tests cover draft creation, persistent model requests, chat/job URL restoration, duplicate history prevention, owner controls, coach read-only presentation and live transcript refresh. API tests verify model persistence without messages, unavailable models, other subjects and scheduled-job read-only enforcement. See [navigation runbook](../runbooks/navigation.md) for URL contracts and browser checks.
