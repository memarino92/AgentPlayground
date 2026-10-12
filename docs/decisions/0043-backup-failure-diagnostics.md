# 0043: Backup failure diagnostics and Sentry alerts

- Status: Accepted; implementation validated locally, deployed job verification pending
- Recorded: 2026-10-11
- Decision date: 2026-10-11
- Evidence: maintainer request for verbose cron backup logs, Sentry notification, and investigation of partial dumps; `PersonalAgent.Backup/Program.cs`, `BackupDiagnostics.cs`, `BackupUpload.cs`
- Extends: [0039](0039-scheduled-sftp-database-backups.md), [0041](0041-actionable-sentry-errors.md)

## Context

The maintainer reports intermittent SSH backup failures and abandoned partial dumps. The existing job logs only the exception type and leaves remote partials on failure. The exact network/server cause has not been established.

## Decision

Reuse the existing encrypted, active Sentry revision for the one-shot Backup service. Load once per run, sanitize diagnostics before console/Sentry output, and explicitly flush before exit. Include stage durations, run identity, verbose dump diagnostics, SSH validation, upload progress, byte counts, and native exception context. Reporting failure must preserve the backup outcome.

Clean the current interrupted upload using a fresh pinned connection. Under the existing database lock, remove strictly named abandoned regular partials older than 24 hours by both filename and modification time during due upload attempts. Verify remote size before the final rename and advance success only after finalization. Add SSH keepalives without changing host key trust or introducing automatic upload retries.

Configure a Sentry alert for each production Backup error, including recurring issues and cleanup errors, using the owner's preferred notification channel.

## Alternatives

- A separate DSN/bootstrap secret would report during database outages but duplicates the administration flow; keep database-first configuration.
- Retrying transfers automatically increases run duration and can obscure failure evidence; continue retrying on the next cron start.
- Deleting every partial could remove active or unrelated uploads; require exact names, regular-file checks, and a conservative age cutoff.

## Consequences

Useful SSH and dump diagnostics reach console logs and Sentry with access credentials masked. Known secret replacement supplements shared pattern masking. Abandoned uploads are bounded during later successful connection attempts. Database bootstrap outages, missing cron starts, hard kills, and Sentry outages still require independent freshness monitoring. Host key pinning remains mandatory on cleanup connections.

## Delivery and verification

Validation on 2026-10-11: all 9 backup tests and 15 shared integration tests pass, including explicit one-shot SDK flushing into an in-memory transport, useful exception stacks, credential masking, disconnect/reconnect cleanup, remote-size validation, rename failure, and conservative abandoned-file selection. Backup and Web builds pass without warnings. The live Sentry alert was created and read back enabled; deployed backup revision, real upload, failure event ingestion, and notification receipt remain unverified. See [the runbook](../runbooks/sftp-backups.md) and [roadmap](../plans/roadmap.md).
