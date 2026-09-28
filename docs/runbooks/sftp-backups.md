# SFTP database backups

The owner settings page is **Settings → Database backups**. Enter the SFTP host (without `sftp://`), port, username, password, existing absolute remote directory, and the server's SHA-256 SSH host key fingerprint. The password is encrypted in `app.configuration_settings` and is never returned to the browser. Leaving it blank when editing keeps the stored password. Choose a 1–365 day interval and enable backups.

The backup job is a separate Railway service named `personalagent-backup`, built from `Dockerfile.personalagent-backup` with repository root as build context. Set its cron schedule to `0 3 * * *` (03:00 UTC daily). Set `DATABASE_URL` and `CONFIG_ENCRYPTION_KEY` as Railway references to the same bootstrap values used by API. These two bootstrap values cannot be database-first: the job needs them to read and decrypt its database settings. The service needs outbound access to the SFTP host. No destination credentials belong in Railway variables.

For subsequent CLI releases, `scripts/deploy-railway.ps1 -IncludeBackup` includes the backup service after it has been created in the project.

Each run loads current `Backup` settings, obtains a database advisory lock, and checks `app.database_backup_state`. Its first enabled run uploads immediately; later daily starts upload when the selected number of UTC calendar days has passed since the last successful upload. Failed runs exit nonzero and retry on the next daily start. A disabled job exits successfully without connecting to SFTP.

The uploaded file is `agentplayground-<UTC>-<unique>.dump`. The job uploads it with a `.partial` suffix and renames it when complete. Use a PostgreSQL 18/pgvector compatible target for restoration and retain `CONFIG_ENCRYPTION_KEY` separately. The guarded local restore procedure is in [snapshot commands](snapshot-commands.md). This service does not yet produce that procedure's manifest, so use its archive directly with `pg_restore`; the full recovery rehearsal remains outstanding.

If the job fails, check Railway's backup service logs, database connectivity, destination directory permissions, available temporary disk space, and the configured host key fingerprint. The log deliberately reports a generic failure so credentials are not exposed.
