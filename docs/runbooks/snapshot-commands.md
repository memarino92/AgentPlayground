# Snapshot commands

The first recovery implementation exports full PostgreSQL custom archives with a checksum/manifest and restores into **new local Docker containers only**. It validates encrypted configuration using the same format as the application seed. It does not connect to a production restore target, start the application, or claim full disaster recovery.

Requirements: PowerShell 7+, a local Docker daemon, and a PostgreSQL/pgvector image matching the source's major PostgreSQL version and extension versions. The default is the repo's PostgreSQL 18 image. Credentials are read from named environment variables, never included in command-line arguments or the manifest. Native error output is withheld because it can contain private data.

## Export

In a PowerShell session, load the source URL from your secret store or enter it without recording the value in shell history:

```powershell
$env:SNAPSHOT_DATABASE_URL = [Net.NetworkCredential]::new('', (Read-Host 'Source PostgreSQL URL' -AsSecureString)).Password
$snapshot = ./scripts/export-database-snapshot.ps1 -SourceName railway-production
```

This is a read-only database export. It creates `snapshots/<alias>-<UTC>-<unique>.dump` and `.dump.json`. The manifest records source alias, timestamps, PostgreSQL/client/extension versions, exporter commit, byte count, and SHA-256. `ExporterCommit` identifies the checkout running the command, not necessarily the deployed application; record the deployed commit separately during a production rehearsal. Copy the archive and manifest together. Incomplete exports never receive a valid manifest.

Use `-ConnectionEnvironmentVariable NAME` for another credential variable, `-OutputDirectory PATH` for private storage, and `-Image IMAGE` for compatible tooling. URLs must include user, password, host, and database; `sslmode` is the supported query parameter. Unknown URL options are rejected rather than silently dropped.

The export client runs inside Docker. For a database published on your Windows/Mac host, use `host.docker.internal` in the source URL. On Linux, use a `localhost` source URL and `-Network host` to reach a database bound to host loopback. For a remote source, default bridge networking works when the source is reachable and permits your connection.

Archives, manifests, and any private metadata require protected storage. A checksum detects corruption; it does not authenticate an untrusted archive. Use trusted backups only. Existing S3 backups from `infrastructure/backup/backup.sh` have no manifest and are not yet accepted by this helper; automated manifest generation/upload remains follow-up work.

## Recovery rehearsal

Supply the configuration key corresponding to the archive, separately from the database backup:

```powershell
$env:SNAPSHOT_CONFIG_ENCRYPTION_KEY = [Net.NetworkCredential]::new('', (Read-Host 'Snapshot configuration key' -AsSecureString)).Password
$recovery = ./scripts/restore-database-snapshot.ps1 -SnapshotPath $snapshot.ArchivePath -ContainerName garden-restore-rehearsal
```

The name must begin `garden-restore-` and must not exist. Restore rejects remote Docker daemons, checksum/size failures, incompatible PostgreSQL/extension versions, missing garden configuration, and invalid decryption keys. It creates an empty target, restores atomically with ownership/grants remapped, verifies encrypted settings, and analyzes the database. Failed restores remove only the exact container created by that invocation.

Recovery mode uses `--network none`, exposes no port, and retains pending transport messages/configuration. Inspect through `docker exec`; do not connect application services until the [recovery runbook](database-recovery.md) checks and pending-work policy are completed. Keep application grants and external dependencies in the recovery inventory; this logical restore deliberately does not reproduce source roles/grants.

A report under `snapshots/restores/` records archive hash, target ID, timestamps, duration, extension versions, and encrypted-setting verification count. It explicitly states that application recovery has not been tested.

## Development copy

```powershell
$development = ./scripts/restore-database-snapshot.ps1 -SnapshotPath $snapshot.ArchivePath -ContainerName garden-restore-development -Mode Development -Port 55432
$connection = Get-Content (Join-Path $development.ReportDirectory 'local-connection.json') -Raw | ConvertFrom-Json
$env:DATABASE_URL = $connection.DatabaseUrl
```

Development mode binds PostgreSQL to `127.0.0.1` and:

- Clears all restored application configuration and credentials, mobile tokens, approval records, and Web Data Protection keys.
- Removes the restored `transport` schema so application startup can create fresh MassTransit infrastructure.
- Disables push/web search and role permissions for notification, sync, and scheduling tools.
- Preserves sessions, transcripts, vectors, coach assignments, and other domain data. **This remains private data, not an anonymized/public demo.**

The reset supports the default schema/table names only and refuses configuration that specifies a custom layout. Overrides supplied only through production environment variables cannot be inferred from a dump; review source layout before import. Domain content may itself contain private information or credentials; this is an operational state reset, not content sanitization.

Before starting apps, follow [local development](local-development.md) to seed fresh local configuration under a new local encryption key. The generated `local-connection.json` contains the new container password and belongs in private storage. For the existing seed script's Docker `-Apply` mode, use the generated URL with `host.docker.internal` in place of `localhost` on Docker Desktop. The source key is only for validation; never use it as the local app's new key. Remove conflicting legacy connection overrides and keep push/journal automation disabled until explicitly configured for development.

The snapshot command does not automatically start API/Web/Worker or delete successful restore containers. Stop a rehearsal with `docker stop <container-name>`; retain or remove its data deliberately after inspecting the report. The only automatically deleted containers are failed restore attempts and synthetic test fixtures created by the test script.

## Verification

```powershell
pwsh -NoProfile -File scripts/tests/Test-DatabaseSnapshots.ps1 -UnitOnly
pwsh -NoProfile -File scripts/tests/Test-DatabaseSnapshots.ps1
```

The full suite creates synthetic data with the real configuration seed, exports it, exercises both restore modes, validates vector retrieval and retained data, and rejects existing targets, remote Docker, missing/bad keys, checksum failures, malformed archives, port conflicts, and unsupported custom layouts. It removes its own containers and keeps synthetic evidence under ignored `.artifacts/snapshot-tests/`. The PR workflow runs this suite on Linux; a Windows Docker Desktop run is recorded in the roadmap.
