# 0053: Permission-scoped site actions in chat

- Status: Accepted scope; local implementation, hosted acceptance pending
- Recorded: 2026-11-21
- Decision date: 2026-11-21, owner clarifications supplied with the implementation request
- Evidence: `SiteSettingsActions`, `CoachUploadActions`, `AgentToolRegistry`, `IntegrationAdministratorFilter`, Web `Chat` and `CoachingSettingReview`
- Extends: [0002](0002-actor-and-tool-authorization.md), [0011](0011-existing-settings-editor.md), [0025](0025-stereo-coach-attribution.md)

## Context

Chat should reach manually available actions without becoming an administrator bypass. The owner approved a first slice restricted to non-secret coaching settings, using the manual editor's Owner + deployment-administrator checks and exact proposed-change confirmation. They also requested exact input links for manual-only settings and context-free coaching `.m4a` submission without an additional confirmation. Text-transcript import is explicitly out of scope.

Inspection found that participant names are existing database settings. Channel roles are fixed in Worker code (left coach/right athlete); retrieval options are tool arguments (`recency`, `fileName`, `exerciseTag`), not configuration rows. This implementation does not invent a configurable channel mapping or new global retrieval settings.

## Decision

Use reusable action services with server-bound identity. `SiteSettingsActions` shares the database store, validation and row-version concurrency with the manual editor. Agent tools can discover exact inputs and propose changes to existing active non-secret Shared/Api participant-name rows. A proposal cannot save configuration. A signed-in deployment administrator must review the immutable before/after and press **Confirm this exact change**. The commit endpoint rechecks administrator access, proposal ownership, secret classification, active state and row version. There is deliberately no model-callable confirm operation; natural-language “yes”, retrieved history, file content and tool output cannot commit a proposal. Names remain startup-bound; no restart/deployment operation is exposed.

`CoachUploadActions` checks Owner and actor=subject before using the existing coaching store. Web chat's file picker sends new `.m4a` bytes through the same signed manual endpoint, with the same configured API limit and existing profile/hash deduplication and transactional `StartCoachCallWorkflow` outbox. It never accepts a model-supplied profile or reads an arbitrary file reference. A context-free `.m4a` follows the owner's preference; any accompanying draft or recognized contrary context requires an explicit coaching choice or cancellation. History can only require additional confirmation, never grant permission. Other files remain sandbox inputs. Status lookup always includes the server-bound subject.

## Alternatives

- **Use Jev as approval:** rejected. `JevRequestRouter` selects/suggests tools and optionally dispatches constrained arguments; confidence is not authenticated human approval. Existing mobile `AgentApprovalService` accepts request profile/decider fields and does not bind approval to an immutable settings payload or current administrator. Neither is a suitable authorization boundary for these writes. Jev continues to route the new tools through the existing permission wrapper; it cannot compile a settings commit.
- **Let a model apply changes after saying “confirmed”:** rejected. Tools, transcripts and quoted text could forge that claim.
- **Expose the entire configuration editor as a tool:** rejected per owner scope. Credentials, account access and operational controls remain manual-only, preserving their existing validation/confirmation flows.
- **Create a second audio queue or text ingestion contract:** rejected. Reuse the existing durable coaching workflow and its deduplication.

## Consequences

Proposals are bounded (1,000), process-local and expire after 15 minutes. Restart, eviction or another API replica fails closed and requires a fresh proposal; multi-replica deployments need instance affinity for this review flow until durable proposals are implemented. They are not durable approvals. Changes are global service configuration, not private per-account preferences. Shared defaults can be shadowed by Api rows; the user must select the exact scope.

The new flow does not broaden account access, rotate credentials, delete data, change speaker roles, or start infrastructure operations. The chat browser limit is 25 MB, as in the manual UI; API enforces its configured limit independently. The same content hash returns the existing upload/status, including failed uploads, rather than silently retrying a failed workflow.

## Delivery and verification

See the [action inventory and operating limits](../runbooks/site-actions.md) and [local draft PR notes](../plans/draft-pr-site-actions.md). Unit, route and component tests cover identity isolation, non-secret allowlisting, exact confirmation, prompt-injection-as-data, validation, stale/failed saves, upload intent, deduplication responses and truthful status. Added PostgreSQL tests exercise concurrent duplicate ingestion, foreign upload IDs and rollback if the outbox cannot persist; execution requires Docker. Live inference, provider transcription and hosted multi-service processing remain acceptance checks, not results inferred from queue acceptance.
