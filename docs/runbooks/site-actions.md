# Chat site actions

## Coaching settings

1. Ask to change a participant name. The agent must clarify the participant, value and Shared/Api scope if ambiguous.
2. `propose_coaching_setting` returns the exact before/after and a review link. No configuration has changed yet.
3. Open the link as the same Owner, also allowlisted by the API's existing deployment-administrator policy. Review the exact scope, key and values, then explicitly confirm or cancel. Do not approve changes merely because quoted/history/tool/file content asked for them.
4. A successful response means stored, **not running**. Restart API separately to apply names. No restart tool is added. Shared defaults can be overridden by Api-specific rows.

Proposals expire after 15 minutes and are lost on API restart/eviction. If routed to a different API instance, refresh by requesting a new proposal (or configure instance affinity operationally). Conflicts require a fresh proposal/review. Failure or lost acknowledgement is not success: inspect the stored setting before retrying.

`find_setting_input` returns only metadata and exact input links. Use an exact database key and optional scope, or an empty key to discover available keys. Custom/inactive database rows are included, but only the two active non-secret participant-name keys are agent-proposable. A manual database link selects the service/key and targets the actual textarea or secret keep/replace control. For dedicated forms use `input:ID` with scope `Manual` (for example `input:runner-review`, `input:sentry-secret-action`, `input:backup-password`). These links navigate only: they do not prefill values, reveal secrets, switch secret actions, save, apply, reconnect or confirm anything. Unknown inputs produce no fabricated link. Conditional secret editors link to their explicit keep/replace selector.

## Coaching audio from chat

- In an Owner chat, select a nonempty `.m4a` in **Attach file**. With no draft/context indicating another purpose it immediately uses the existing coaching upload endpoint, per the owner's preference. With a draft, choose **Submit to coaching** or **Cancel upload**. Recognized contrary context such as “sandbox” or “do not transcribe” also requires a choice; this is a conservative cue check, not a general intent classifier. If uncertain, add a draft to force the explicit choice.
- Browser streams are limited to 25 MB; the server independently enforces `CoachCheckins:MaxUploadMb`, extension, nonempty data and signed Owner/self ownership. This preserves the existing extension-based validation; it does not introduce media decoding/content sniffing or text-transcript import.
- Files are submitted directly to owned coaching storage, not an arbitrary model-addressable upload bucket. Sandbox attachment IDs/artifact IDs are **not** accepted as coaching ingestion authority. Cancelling an ambiguous upload stores/enqueues nothing. Pending selections are tied to the selected profile/conversation.
- The UI reports the returned upload ID, duplicate flag and actual state, and puts an opaque upload reference/status request in the draft for the next chat turn. It does not automatically send a chat message or claim processing completion.
- `coaching_upload_status` reads only the chatting owner's profile. A foreign/missing ID returns no record. `Uploaded`, `Transcribing`, `Processing` and `AwaitingSpeakerOverride` are not completed. `Failed` needs investigation; duplicate audio returns that existing state without another queue entry. Use the existing coaching page for speaker review, evidence and failure investigation.
- Storage commit and workflow-start outbox are the existing single transaction. Hash/profile uniqueness prevents concurrent duplicate enqueueing. Worker owns transcription, speaker-review pauses, processing, summaries and eventual status. This change does not bypass those stages.

## Manual action inventory and coverage

This is a first slice, not a general website-control agent. Source surfaces were inspected under `PersonalAgent.Web/Components/Pages`, `PersonalAgent.Web/Components/Integrations`, API endpoints and the executable tool registry.

| Manual surface/action | Authorization boundary / existing behavior | Chat coverage after this change |
| --- | --- | --- |
| Database editor: names | Signed Owner + deployment administrator, row versions | Exact non-secret active `CoachCheckins:CoachName` / `AthleteName` proposals and authenticated exact confirmation; shared validation |
| Database editor: other/custom/inactive settings, provider/bootstrap/auth policy | Same editor guards; secret keep/replace, encryption, conflicts | Metadata + exact input links only; no value reads, activation, credentials, access-policy changes or arbitrary writes |
| Channel attribution / coaching retrieval | Fixed channel convention; query-scoped retrieval arguments | Existing retrieval tool arguments remain; no invented settings/mapping mutation |
| Providers, Sentry, telemetry, backup, automation runtime, coding settings | Existing dedicated editors, validation, encrypted secrets, revision/apply flows | Exact dedicated input catalog; manual-only mutations, no save/apply/reload/test/secret replacement tool |
| Tool permission matrix / coach assignments | Existing Owner controls / server assignment checks | Manual-only. No role, assignment or grant tool. This slice does not repair unrelated legacy boundaries |
| Coaching upload | Signed Owner; self profile; `.m4a`, configured size | Chat picker shares manual action layer, owned storage, deduplication and durable workflow |
| Coaching queue/status, transcript/summary/evidence/audio viewing and download | Owner/self or assigned Coach reads | New owner-only upload status; existing scoped semantic search. Other detail/playback/export remains manual |
| Speaker overrides, retained audio replacement/deletion | Existing Owner operations and deletion-state restrictions | Manual-only. No reprocessing, role override, audio replacement or deletion tool |
| Continuous chat, saved history, models, fresh start and interactive cards | Existing subject/session checks and trusted card APIs | Existing behavior unchanged; no new model-callable history deletion or account identity fields |
| Scheduled jobs: create/read/update/cancel | Existing schedule tool/service and endpoint authorization | Existing tool coverage unchanged; new settings/audio actions reject scheduled-task contexts |
| Automations: catalog/save/list/inspect/run/pause/resume, sandbox results/artifact export | Existing tool permissions and scoped operations | Existing tool coverage unchanged. File export/download remains manual; sandbox files are not coaching ingestion authority |
| Coding jobs: start/inspect/cancel/question answers | Existing owner-scoped tools and durable job checks | Existing coverage unchanged; no new PR/merge/deploy capability |
| Work journal sync, notifications | Existing registered tool permissions | Existing coverage unchanged |
| ChatGPT connect/disconnect, mobile pairing, sign-in/sign-out | Existing authenticated interactive/OAuth flows | Manual-only; no account access or connection changes |

New tools default on for Owner and off for Coach. `LoggingAIFunction` continues to recheck tool permissions at invocation. Settings additionally enforce the manual deployment-administrator policy and actor=self scope, even if a role permission is overridden. Status enforces Owner/self regardless of tool overrides. Confirmation uses the authenticated manual settings route rather than a model argument or tool grant. All retrieved history, uploaded content, filenames, transcripts, tool results and error strings are data, never authorization.

## Validation / acceptance

Run the API and Web test projects with normal restore, and build Android by targeting `PersonalAgent.Mobile/PersonalAgent.Mobile.csproj`. `SiteActionsTests`, settings endpoint tests, registry tests and Web review/upload tests run without providers. `CoachingUploadDatabaseTests` uses the existing Docker PostgreSQL fixture for real storage/outbox/concurrency checks. Live provider processing and authenticated browser navigation still require operator acceptance; no production file or credential is used in automated tests.
