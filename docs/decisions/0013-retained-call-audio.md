# 0013: Retained original call audio

- Status: Proposed; retention foundation implemented on this branch
- Recorded: 2026-09-11
- Evidence: PRODUCT-01 roadmap request; `CoachCheckinService`, `ProcessCoachTranscriptConsumer`, `CoachCallCleanupService`, and recovery tests
- Extends: [0009](0009-transcription-outbox.md)

## Context

Uploads already store original bytes, MIME type, file hash, size, subject and session identity in PostgreSQL. Successful processing clears the bytes in its completion transaction. Utterances and chunks already preserve source start/end milliseconds. Deleted historical bytes cannot be recovered from transcripts.

## Decision

Keep original bytes in the existing upload row after successful processing. Keep the upload/session/profile relationship and completion/outbox transaction unchanged. Retain successful recordings without automatic expiry for this initial slice; failed uploads still expire under `FailedUploadRetentionDays`. No schema migration or copy of the recording is needed.

Before exposing playback, add subject-authorized evidence reads and range requests, and owner-authorized audio deletion under DATA-04. Deletion should clear bytes while preserving source identity and transcript readability, be idempotent, and serialize with processing using the upload row lock. Processing must never restore cleared bytes. Pending jobs need an explicit cancellation/deletion policy before permitting their deletion. These endpoints are future work, not delivered by this retention foundation.

## Alternatives

Object storage would separate media capacity and streaming from database traffic, but requires another storage lifecycle, access policy, backup manifest and reconciliation across stores. Revisit it when measured recording volume or playback traffic warrants that work. Continuing to delete completed recordings prevents the requested evidence playback. A second database media table would duplicate an existing durable relationship without reducing byte storage.

## Consequences

The default upload limit is 25 MiB. One thousand recordings at that limit add approximately 24.4 GiB of raw audio before database, WAL and backup overhead. Actual cost depends on deployment pricing and backup retention; no cost or capacity benchmark is claimed. Monitor database size, backup size/duration and restore time before expanding retention at scale.

Full database backups include these bytes and their source links. Development restores can retain private audio and must stay private. Active deletion cannot erase older backups or provider-held copies: archive retention and provider deletion are separate policies. A restore of an older archive can resurrect deleted audio; a deletion ledger/reconciliation policy and measured backup expiry remain DATA-04 follow-ups before claiming durable erasure. PostgreSQL removal also does not promise immediate physical disk reclamation.

## Delivery and verification

This branch removes successful-processing byte cleanup and tests byte identity after transaction rollback, concurrent completion, lost acknowledgement and replacement service/transport hosts. Failed cleanup must continue to spare completed and pending uploads. Playback, authorized deletion, citation/drawer integration, and a recording-inclusive backup/restore rehearsal remain outstanding. Older calls with null audio remain unchanged; future evidence responses must report unavailable audio and never invent missing timestamps.
