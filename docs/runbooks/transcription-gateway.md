# Transcription gateway

API owns the transcription adapter, provider configuration and the `transcription_jobs` table in the agent-memory schema. Worker requests transcription with an upload ID and subject profile over MassTransit, then owns utterance persistence, speaker review and downstream processing. Audio bytes never enter the bus. API checks the upload/profile pair against server storage before any provider access or cached-result read. This is an internal trusted-bus capability; it does not add a public endpoint or change upload authorization.

## Configuration and rollout

1. Let old Worker transcription consumers finish before replacing Worker. The old implementation never persisted provider IDs, so interrupting it cannot guarantee recovery without duplicate submission. Pause new uploads during the rollout.
2. Run `scripts/migrate-assemblyai-configuration.ps1` to preview, then add `-Apply` to migrate. It reads `DATABASE_URL` and `CONFIG_ENCRYPTION_KEY` from the environment, or accepts `-ValuesPath scripts/seed-configuration.values.ps1` to use the existing private bootstrap values. It reads all Worker `AssemblyAi:*` rows directly from PostgreSQL, decrypts secret values and re-encrypts them for Api using the application's crypto implementation. Use `-KeepSource` while the old Worker is still deployed to copy settings into Api without removing Worker settings. It preserves activation flags and writes rows in one transaction, without printing secrets or generating SQL files. An existing matching Api row is retained; a conflicting row aborts the whole migration. Reruns are safe. Changing only the scope column would invalidate encrypted values.
3. If using environment variables or user secrets, move `ASSEMBLYAI_*` / `AssemblyAi:*` settings to the API process/project. API validates these options on startup. Worker no longer needs provider credentials.
4. Start API with infrastructure creation enabled to add `transcription_jobs`, then start the new Worker. API agent-memory storage and Worker coach-checkin storage must use the same database and schema, as required by the existing staged-upload pipeline.
5. Verify a synthetic single-speaker upload reaches processing and a multi-speaker upload pauses for speaker review. No live-provider verification is included in automated tests.

### Migration commands

```powershell
# Preview using existing private bootstrap values (no database changes).
./scripts/migrate-assemblyai-configuration.ps1 -ValuesPath ./scripts/seed-configuration.values.ps1

# Prepare Api while retaining configuration needed by the old Worker.
./scripts/migrate-assemblyai-configuration.ps1 -ValuesPath ./scripts/seed-configuration.values.ps1 -Apply -KeepSource

# Finish the move after draining and replacing the old Worker.
./scripts/migrate-assemblyai-configuration.ps1 -ValuesPath ./scripts/seed-configuration.values.ps1 -Apply
```

Omit `-ValuesPath` when the bootstrap environment variables are already set. The helper requires the repository's .NET 10 SDK and restores its normal repository dependencies; it connects directly to PostgreSQL and does not require Docker or psql. Configuration writers may wait briefly during the transaction; readers remain available. Stop old Worker processes before applying because their startup configuration will no longer be in the Worker scope. Restart API afterward. A network failure during commit can leave the outcome uncertain; rerun the preview to inspect the remaining move count.

## Recovery semantics

The upload ID is the durable public job reference. A committed unique row grants one submission attempt per upload across concurrent requests and process restarts. API stores the provider job ID before replying, and each later request performs at most one provider status lookup. Pending replies include a suggested delay. The provider protocol follows [AssemblyAI's documented transcript statuses](https://www.assemblyai.com/docs/pre-recorded-audio/check-transcript-status).

The deadline is persisted at first submission (default 15 minutes). It includes time while API or Worker is unavailable and is not reset on replay. Polling transport errors are retried on later requests until the deadline; terminal failures contain neutral messages. Terminal results are cached in PostgreSQL and the first terminal result wins. Upload deletion cascades to the job row.

A crash or lost response between provider submission and saving the provider ID is ambiguous. API does **not** automatically submit again. A live overlapping request sees Pending; after the deadline, an unconfirmed submission requires operator review. A caught submission failure records that requirement immediately. This trades automatic recovery in that narrow interval for avoiding duplicate paid jobs; it does not claim provider-side exactly-once delivery.

For an ambiguous job, inspect provider-side job history privately before taking action. Do not delete the tracking row and replay blindly. There is no automated reconciliation or cancel-provider-job endpoint in this slice. Host cancellation stops local polling and leaves persisted state for redelivery; it does not cancel a remote provider job. Transport request failures escape Worker's domain failure handler for the configured message retry/error-queue policy. Recover messages from the error queue after restoring API availability.

Worker now commits utterances, transcript text, status, and outgoing messages together in `coach_call_outbox`. Speaker-review continuation uses the same transaction. Processing locks the matching upload/profile/session row and commits summaries, chunks, Completed status and the completion event together, retaining the original audio bytes. Commands for a completed stage are ignored, including concurrent redelivery. Replaying a speaker-review request does not revise roles once processing has begun.

### Retained recordings

Completed uploads retain original bytes in `coach_call_uploads`, linked to their subject and session. Drain and replace older Workers before relying on retention: the earlier consumer deletes bytes at completion. No migration restores previously deleted recordings. Failed-upload cleanup still clears only Failed audio older than `CoachCheckins:FailedUploadRetentionDays` (default seven days), running every twelve hours. Completed recordings have no automatic expiry in this initial slice.

Monitor database capacity and backup size/duration: the default 25 MiB upload limit is a per-file bound, not an aggregate quota. Full database snapshots include retained bytes and source links; development restores can contain private recordings. A retained-audio restore rehearsal remains outstanding. Audio playback and authorized deletion endpoints are not yet implemented. Future active deletion cannot erase previous archives or provider copies; restoring an older archive can restore deleted recordings. See [storage and lifecycle proposal](../decisions/0013-retained-call-audio.md) for capacity assumptions and DATA-04 follow-ups.

The Worker dispatcher polls pending outbox rows every two seconds when idle or after an error. It uses row locks with `SKIP LOCKED` so multiple Workers can dispatch safely. Processing commands go directly to the processing queue; events are published. The dispatcher deletes a row only after transport acceptance. A crash or lost acknowledgement before deletion can repeat a delivery with the same message ID; domain status guards prevent duplicate transcripts and final writes even when the broker delivers it again. Delivery order across messages is not guaranteed.

Cancellation and SQL/transport failures roll back the domain transaction and escape to MassTransit's configured retry/error-queue policy. After retries are exhausted, restore the dependency and replay the error-queue command; processing remains resumable with audio intact. A confirmed terminal transcription failure commits Failed and its failure event atomically. Embedding requests may repeat after a rollback before processing commits; this change does not give external embedding calls exactly-once semantics.

### Outbox rollout and diagnosis

Run API schema initialization (`AgentMemory:CreateInfrastructure=true`) before replacing Worker; this adds the outbox table and pending-message index without rewriting existing uploads. API and Worker must share the domain database/schema. Drain old Workers before switching versions: old consumers do not participate in the new locking/outbox protocol. Keep the Worker running to deliver outbox messages, including speaker-review continuations queued by API.

Inspect pending row count and oldest `created_at` in the configured domain schema's `coach_call_outbox`; do not export its payloads into logs. Persistent dispatcher errors leave rows intact and appear as `Coach call outbox delivery failed` in Worker logs. Restore database/bus access and the dispatcher retries automatically. Do not delete pending rows as a recovery shortcut.

Previously stranded Processing uploads have no outbox history. This migration does not blindly enqueue historical uploads. Reconcile their transcript, session, status and error-queue evidence before issuing a targeted processing command. The new processing guard makes a command for an already Completed upload harmless.

Validation uses disposable PostgreSQL, replacement API/Worker services, SQL failure injection and lost-acknowledgement replay. It asserts unchanged transcript/chunk identities, one provider submission, rollback of audio/status/results, and no repeat embedding request after committed completion. A PostgreSQL MassTransit test stops and recreates a host with a queued command and verifies completion plus hosted outbox draining. These are not OS process-kill or live-provider tests. See [decision 0009](../decisions/0009-transcription-outbox.md).
