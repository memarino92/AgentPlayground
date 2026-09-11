# 0013: Retained original call audio

- Status: Proposed; retention, authorized playback and active deletion implemented on this branch
- Recorded: 2026-09-11
- Evidence: PRODUCT-01 roadmap request; `CoachCheckinService`, `ProcessCoachTranscriptConsumer`, `CoachCallCleanupService`, and recovery tests
- Extends: [0009](0009-transcription-outbox.md)

## Context

Uploads already store original bytes, MIME type, file hash, size, subject and session identity in PostgreSQL. Successful processing clears the bytes in its completion transaction. Utterances and chunks already preserve source start/end milliseconds. Deleted historical bytes cannot be recovered from transcripts.

## Decision

Keep original bytes in the existing upload row after successful processing. Keep the upload/session/profile relationship and completion/outbox transaction unchanged. Retain successful recordings without automatic expiry for this initial slice; failed uploads still expire under `FailedUploadRetentionDays`. No schema migration or copy of the recording is needed.

Evidence reads and range requests use the same subject policy as chat: Owners can access their own profile; Coaches need a current assignment. Transcript JSON/text routes now apply that policy to Owners too. Web forwards each media request with the authenticated cookie identity signed server-side; no internal credentials enter the media URL. Responses disable caching.

Owner-authorized audio deletion under DATA-04 clears bytes while preserving source identity and transcript readability, is idempotent, and serializes with processing using the upload row lock. It is allowed only for Completed/Failed uploads; pending jobs return 409 until a cancellation policy exists. Processing never restores cleared bytes. After deletion commits, subsequent audio reads return 404. Already downloaded/buffered bytes cannot be revoked. This is audio-only deletion, not deletion of transcripts or derived memory.

## Alternatives

Object storage would separate media capacity and streaming from database traffic, but requires another storage lifecycle, access policy, backup manifest and reconciliation across stores. Revisit it when measured recording volume or playback traffic warrants that work. Continuing to delete completed recordings prevents the requested evidence playback. A second database media table would duplicate an existing durable relationship without reducing byte storage.

## Consequences

The default upload limit is 25 MiB. One thousand recordings at that limit add approximately 24.4 GiB of raw audio before database, WAL and backup overhead. Actual cost depends on deployment pricing and backup retention; no cost or capacity benchmark is claimed. Monitor database size, backup size/duration and restore time before expanding retention at scale.

Full database backups include these bytes and their source links. Development restores can retain private audio and must stay private. Active deletion cannot erase older backups or provider-held copies: archive retention and provider deletion are separate policies. A restore of an older archive can resurrect deleted audio; a deletion ledger/reconciliation policy and measured backup expiry remain DATA-04 follow-ups before claiming durable erasure. PostgreSQL removal also does not promise immediate physical disk reclamation.

## Delivery and verification

This branch retains successful recordings, adds authorized evidence/audio/delete endpoints and a cookie-authenticated media proxy, and opens a transcript/player drawer from chat citation buttons or the transcript page. Retrieval returns source links with upload ID, subject and original start milliseconds. The player supports native play/pause, elapsed/total time and seek bar, speed selection, timestamp clicks and clamped 15-second skips. Missing audio/timing is explicit; no alignment or transcript timeline is rewritten. Follow-along is deferred.

PostgreSQL tests verify retained bytes, rollback, concurrent replay, lost acknowledgement, host replacement, range semantics, current assignments, cross-subject denial, pending-delete rejection and transcript availability after deletion/restart. Component tests cover citations, missing timing/audio and deletion confirmation. The synthetic smoke script validates real upload-to-processing and Web cookie/range access for three personas. Browser checks verify playback controls against a 30-second silent PCM fixture, not spoken alignment or live-provider quality.

The API currently materializes the original blob for each range request; the Web proxy streams the response body. This is bounded by the existing upload limit but warrants measurement before concurrent/high-volume playback. A recording-inclusive backup/restore rehearsal, deletion-ledger reconciliation across restores, actual archive expiry, and optional follow-along remain outstanding.
