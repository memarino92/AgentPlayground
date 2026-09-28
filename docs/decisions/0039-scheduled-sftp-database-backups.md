# 0039: Scheduled SFTP database backups

- Status: Accepted; deployed disabled, live SFTP verification pending
- Recorded: 2026-09-28

## Context

The repository has a looping S3 dump container and guarded local snapshot/restore helpers, but no owner-facing destination settings or deployed SFTP backup service. The owner asked for a simple Railway cron job that reads upload settings from the same encrypted PostgreSQL configuration used by API, Web, and Worker.

## Decision

Run a dedicated one-shot .NET service each day through Railway cron. It loads `Backup` scoped configuration from `app.configuration_settings` using `DATABASE_URL` and `CONFIG_ENCRYPTION_KEY`. An owner and integration administrator-only settings page creates or updates the SFTP host, port, username, password, pinned SHA-256 SSH host key, remote directory, enable switch, and interval in days. The password is encrypted with the existing configuration crypto and omitted from reads.

The job uses PostgreSQL 18 `pg_dump` custom format. It holds a PostgreSQL advisory lock, skips runs before the configured interval, uploads a `.partial` file over SFTP, renames it after upload, and records the last successful upload in `app.database_backup_state`. It exits after one attempt. A failed run exits nonzero and does not advance the successful timestamp. It requires a host key fingerprint before connecting.

## Alternatives and consequences

MassTransit sagas and Railway sandboxes add lifecycle and credentials handling without helping this one-step infrastructure job. A permanently running loop consumes resources and loses its interval anchor on restart. Railway cron provides a daily wakeup; the database timestamp controls the selected number of UTC calendar days.

This is a full logical dump of one database, not point-in-time recovery. The database includes encrypted configuration, but restoring it also requires the separately held configuration key. The SFTP destination must exist and permit file creation and rename. This slice does not add retention, upload alerts, or automated restore rehearsals; those remain recovery work in the roadmap.
