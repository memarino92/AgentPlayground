# Database snapshots, local imports, and recovery rehearsal

Status: **database export/restore implemented and tested on synthetic data; full application DR still unproven**. Start with [snapshot commands](snapshot-commands.md). The existing `infrastructure/backup/backup.sh` creates a PostgreSQL custom-format dump and uploads it to S3 on a loop (default 86400 seconds). The script exits on failure. Its presence does not prove the backup service is deployed, uploads are monitored, retention is configured, or any archive has been restored.

## Two different outcomes

| Mode | Preserve | Change before application startup |
| --- | --- | --- |
| Recovery rehearsal | Full application state, encrypted config, original immutable archive; transport for inspection | Isolate networking/destinations; supply separately held decryption key; decide pending-message replay policy |
| Local development | Only necessary representative data and schema | Replace production config/keys, strip private identities/content as appropriate, remove push destinations and auth state, rebuild transport |

A private local reproduction may retain sensitive content deliberately, but it is not a sanitized demo. Encrypt its storage, restrict access, and delete it when no longer needed. Pseudonymizing a profile ID does not sanitize journal text, audio, transcripts, embeddings, or OAuth tokens.

## Snapshot contract and remaining operations

The PowerShell export helper accepts a named source connection through an environment variable and an output directory. Never print the connection string. Use matching PostgreSQL client tooling and custom format; stop on any nonzero exit. Write a manifest containing UTC start/end, source alias (no credentials), server/client/extension versions, application commit, archive size, SHA-256, and backup mode. Upload archive and manifest, verify upload, and alert on stale/missing backups. Set remote access controls, storage encryption, and lifecycle retention explicitly.

`pg_dump` obtains a consistent logical snapshot of a database while it is in use. Custom archives can be inspected and selectively restored. It does not include cluster-wide roles/tablespaces; capture the required role/grant provisioning separately. A logical dump is not point-in-time recovery. References: [PostgreSQL 18 pg_dump](https://www.postgresql.org/docs/18/app-pgdump.html), [pg_restore](https://www.postgresql.org/docs/18/app-pgrestore.html).

Keep `CONFIG_ENCRYPTION_KEY` in a separately recoverable secret store with an identified key version. Include OAuth/provider credentials, infrastructure definitions, service settings, and any externally stored files in the recovery inventory. If semantic memory is configured to use a separate database, back it up too and define cross-database consistency expectations; the current uploader backs up only `DATABASE_URL`.

## Rehearse into an isolated, empty target

1. Record the selected archive, its checksum, backup age, target PostgreSQL version, pgvector version, application commit, and start time. Retrieve the corresponding configuration key separately. Verify the checksum before using the archive; `pg_restore --list` checks readability but is not proof of recovery.
2. Create a new disposable local database/container with pgvector available. Bind its database port to loopback. Do not point the restore at an existing developer database or production. Keep all API/Web/Worker services stopped and isolate external network access while inspecting restored state.
3. Inspect the archive's object list and required extensions. Provision the target owner/extension support. Restore into the empty database with `pg_restore --exit-on-error --single-transaction --no-owner --no-privileges`; intentionally remap ownership/grants for the rehearsal. Do not use `--clean` against a populated target. Check the exit status, then run `ANALYZE`.
4. Verify expected schemas/tables and scoped row counts, representative session/transcript relationships, coach assignments, role permissions, staged upload state, vector dimensions, and a known retrieval query. Validate configuration decryption with the separately held key without logging plaintext. Keep counts or sample content out of public logs when they identify private activity.
5. Inspect transport state before starting any consumer. Pending schedules, approval notifications, transcript processing, and journal sync can replay. A development clone discards transport and initializes fresh infrastructure. A true recovery rehearsal inventories pending work and tests an explicit reconciliation/replay policy against stub destinations. Do not claim full DR recovery after simply discarding the queues.
6. Start the API and Web against the isolated target with controlled provider endpoints. Verify owner sign-in, coach assignment isolation, session retrieval, and config loading. Then enable Worker and test one controlled job with external effects stubbed. For actual production recovery, separately restore required service grants and approve reconciliation before enabling outbound work or changing traffic.
7. Record finish time, actual recovery point (archive age), recovery duration, checks performed, missing dependencies, failures, and corrective work. Keep the detailed evidence privately; commit a redacted result summary using the template below.

The restore helper should enforce a local host allowlist and require an empty, explicitly named target. Integration tests must cover rejected remote targets, existing databases, missing archives, corrupt archives, bad checksums, nonzero restore exit, missing/wrong decryption keys, and successful configuration loading. Do not test guards by touching production.

## Turn a restored copy into development data

Perform this only on the disposable copy, never on the original dump:

- Replace `app.configuration_settings` with a development seed encrypted under a new local key. Review all active scopes, URLs, explicit environment overrides, and optional integrations.
- Revoke/remove restored push tokens, pending approvals, auth/Data Protection state, and other credential-bearing rows. Do not let a clone share production browser cookie keys.
- Rebuild MassTransit transport using the supported migration hosted service on a fresh database/schema. Application schema alone is not sufficient for SQL transport.
- Remove or replace private audio, journal text, transcript excerpts, messages, vectors derived from private text, original filenames, and identity mappings for a shareable demo. Re-embed synthetic content through the gateway if retrieval is being demonstrated.
- Use local OAuth clients, local allowlists, local internal/signing credentials, stubbed providers or explicitly chosen development credentials, and disabled push/sync automation.
- Validate every effective connection points to the local target before any service starts. Keep Worker stopped until this check passes.

Use the production-like private clone only to diagnose issues that synthetic data cannot reproduce. The normal contributor/portfolio path should be the synthetic seed.

## Rehearsal evidence template

```text
Date / operator:
Application commit:
Archive identifier / SHA-256 (no credentials):
Backup UTC / measured recovery point:
PostgreSQL, pgvector, client versions:
Target alias / isolation method:
Key recovery and config decryption: pass/fail
Schema, row/relationship, vector checks: pass/fail
Owner/coach authorization checks: pass/fail
Pending-message reconciliation and controlled worker test: pass/fail
External files/dependencies and grants restored: pass/fail
Start / finish / measured recovery time:
Failures / follow-up work:
Outcome: unproven / partial / successful for stated scope
```

Initial proposed objectives are a recovery point within 24 hours and completion within 2 hours. These are targets to test, not measured guarantees. Rehearse again after material schema/configuration/transport changes. A second Railway environment is optional for platform-specific deployment validation after local recovery works.
