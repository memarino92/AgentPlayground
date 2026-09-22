# Continuous conversation

Implemented 2026-09-21. See [decision 0029](../decisions/0029-continuous-conversation.md) and [product vision](../plans/personal-assistant-vision.md).

Interactive responses now stream from the provider through an authenticated NDJSON response. The composer changes to **Stop** while a response is active. Stop or a browser disconnect cancels the in-flight model request; partial text can remain visible in the current browser but is not committed as a completed interaction. Reload to return to the last durable conversation state. The completion event replaces provisional text with the normalized persisted response and card/source metadata.

## Use

Open `/chat` to resume the same default conversation across reloads and devices. Model selection is under the composer's Model disclosure. History remains optional: browse saved conversations or search words from earlier user messages. Search returns up to five matches and links to a highlighted source message. Scheduled job and legacy links remain readable; use Back to conversation to resume talking.

Start fresh or send `/clear` to save a server-side boundary. Earlier conversational turns and heuristic memories are excluded from automatic recall, including after restart. Records and cards are retained for explicit history inspection; this is not deletion. There is no separate confirmed-fact store yet. Old cards are readable in history, but this slice does not offer a cross-history commitments dashboard.

Generated commitment cards are proposals. Track this confirms the card; Mark done completes it. Checklist checks and clarification answers persist. Sending a clarification answer uses ordinary chat; an existing draft is preserved by inserting the answer into it for review. Cards do not create notifications, execute arbitrary code or share information with another account. Existing scheduling tools remain the mechanism for actual reminders.

## Context construction

- Default conversation identity is derived from the server-resolved actor, role and subject. It does not grant access: every read/write still checks that scope. The database primary key and a PostgreSQL advisory transaction lock serialize initialization and mutations across API instances.
- Recent context uses up to 24 messages and an 18,000-character budget. Very long individual messages are excerpted; card state counts toward the budget. The normal screen loads up to 100 post-boundary messages; full retained history is available through the history view.
- Historical retrieval combines PostgreSQL English full-text search over user messages with pgvector similarity over indexed turns. It excludes recent messages, scheduled-job sessions, other actors/roles/subjects and content before a fresh-start boundary. Results include adjacent assistant text, timestamps and source references; at most five candidates fit within a separate 6,000-character evidence budget.
- Semantic recall/indexing uses the existing `AgentMemory:EnableSemanticMemory`, model and vector-space configuration. Each operation has a three-second timeout. Provider failures fall back to lexical history; indexing failure does not fail an already-saved response. No new environment variables or provider credentials are introduced.
- Existing transcript words are searchable immediately. Semantic coverage uses compatible existing user-memory records plus newly indexed conversation turns; this change does not backfill every old turn. Explicit direct tool routes remain cheap and searchable lexically without additional embedding calls.
- Historical statements are untrusted evidence, not higher-priority instructions or automatically confirmed facts. The model is instructed to respect corrections and ask about ambiguity. Source disclosures mean evidence was supplied, not proof it determined the answer.

This is a deterministic retrieval baseline. Classifier reranking, topic/episode summaries, explicit memory corrections, active-goal retrieval and measured live-model quality are follow-ups. English lexical search and fixed thresholds may miss relevant context; bare pronouns referring to a distant topic can require clarification.

## Presentation and persistence

The model can append fenced `garden-card` JSON using a small allowlist: commitment, checklist, clarification. A shared parser validates shape and bounds, assigns server IDs, resets all generated status to proposed, and removes valid blocks from display text. Invalid blocks remain readable text. No generated HTML or JavaScript is executed.

Typed presentation and recalled source references are stored in the assistant transcript row's existing metadata column in the same transaction as the interaction. Updates address the conversation, message sequence and server card ID, recheck current authorization, lock the row and require the expected revision. Stale/invalid actions return 409. Reload to recover from a stale card.

The only new schema infrastructure is a full-text index. Continuous-state properties and metadata are additive; older binaries can still read the text/model fields but do not render these cards. Rolling back UI/API code does not remove stored metadata. Existing scheduled-job records retain their read-only semantics.

## Verify locally

Use the [synthetic demo](synthetic-demo.md), then run:

```powershell
pwsh -NoProfile -File scripts/tests/Test-ContinuousConversation.ps1
```

This runs the existing 22 smoke checks and 13 continuous-chat checks against the named local synthetic environment. It adds fixture messages/cards and clears only the synthetic other-owner's conversational context; it retains the records. It does not use production data or real model/push providers.

Sign in as Demo owner, open Chat, and send `Show demo cards`. Confirm a commitment, tick a checklist item, and answer the form. Reload and verify state. Open History and search `demo cards`; the link should highlight the saved user message. Verify rapid typing, failed-send draft retention, and Start fresh.

Validation on 2026-09-21: 318 API tests passed in the full run; a subsequent 8-test context suite passed after adding the oversized-answer case and scoped-history assertion. All 61 Web tests and 31 Contracts tests passed. API/Web Linux Release containers built and became healthy; all 35 synthetic smoke checks passed. Browser checks verified card interactions/reload persistence, continuous restoration and a 390x844 layout. This is Chromium responsive verification, not physical iPhone/Safari validation. Live-model card/recall quality, production deployment, iOS push and offline capture remain unverified or unimplemented.
