# 0028: Skip unchanged journal files using Git blob checkpoints

- Status: Accepted; implemented
- Recorded: 2026-09-20
- Decision date: 2026-09-20
- Evidence: maintainer approval in this task; `SyncWorkJournalConsumer`; `SyncWorkJournalConsumerIntegrationTests`

## Context

Journal sync downloaded and parsed every Markdown file before comparing extracted entries with stored content. The comparison avoided repeat embedding requests but still incurred parsing requests for unchanged files.

## Decision

Persist the last successfully processed Git blob SHA in `work_journal_file_sync`, keyed by repository owner/name, branch, and full file path. Compare GitHub directory-listing SHAs before downloading or requesting parsing. Owner and repository names are normalized to lowercase; branch and path remain case-sensitive.

Verify the downloaded bytes against the listed Git blob SHA before parsing, since a branch can change between listing and download. Commit entry writes and the checkpoint in one database transaction. Successful files with no changed entries, including empty parse results, also receive checkpoints. Missing SHA/path metadata falls back to processing without a checkpoint. Failed files remain eligible on the next sync; successful files can be skipped independently.

## Alternatives

- Keep comparing only extracted entries: retains unnecessary model parsing calls.
- Track a repository commit: unrelated changes would invalidate journal files, and partial failures require additional per-file state.
- Use timestamps: the existing GitHub contents listing already supplies content identity without timestamp inference.

## Consequences

The first sync after deployment establishes checkpoints. Subsequent syncs still fetch the directory listing but skip downloads and model calls for unchanged files. Existing entry comparisons still avoid embeddings when a changed file produces unchanged entries.

Checkpoints are disposable derived state. Parser changes require explicit invalidation to reprocess unchanged source files. Clearing journal entries also requires clearing corresponding checkpoints. Entry identity remains filename/date-based; this change does not add multiple-source entry isolation, deletion reconciliation, recursive traversal, or concurrent-sync serialization.

## Delivery and verification

The Worker creates the checkpoint table alongside existing journal infrastructure. PostgreSQL integration tests cover initial sync, unchanged and changed SHAs, unchanged extracted content, empty results, source scoping, missing metadata, and retries after parse, embedding, hash mismatch, and partial database write failures. See the [roadmap](../plans/roadmap.md) for validation evidence and the [journal sync runbook](../runbooks/journal-sync.md) for invalidation.

Source contract: [GitHub repository contents API](https://docs.github.com/en/rest/repos/contents).
