# Runtime integration settings

The first supported integration is Sentry error reporting. Its settings can be entered, validated, saved, and applied in Web at `/admin/integration-settings`. API, Web, and Worker each own a replaceable Sentry client; these four settings do not require a process restart. This does not reload existing OpenAI, transcription, messaging, or authentication settings.

## Bootstrap and access

1. Use the normal database configuration bootstrap: `DATABASE_URL` and the matching `CONFIG_ENCRYPTION_KEY` on each service. With local user secrets, `Messaging:ConnectionString` is also supported if the encryption key is supplied externally. Do not enter bootstrap credentials in the UI.
2. On API, set `INTEGRATION_SETTINGS_ADMINISTRATORS` to a comma-separated list of allowed GitHub logins. The equivalent startup setting is `IntegrationSettings:AdministratorIds`; the environment variable takes precedence. The default is no administrators. Only signed Owner actors on this allowlist can access these endpoints. Profile ownership alone grants no deployment administration rights.
3. Sign in normally, open **Sentry settings** in the menu, and configure Sentry. Use **Application settings** for existing database rows. The synthetic Compose environment already permits `demo-owner`; `demo-other` is deliberately denied.

## Existing database settings

Open **Application settings** (`/admin/settings`). Every existing row from `app.configuration_settings` is shown, including inactive rows and keys not listed in the seed script. Search by key or select a service scope. Shared rows provide defaults; service-specific rows override them. Unknown scopes are shown verbatim and only take effect if a consumer loads that scope.

Edit text values, or choose **Keep**, **Replace**, or **Clear** for encrypted secrets. Clear stores an encrypted empty string; unchecking **Use this setting at startup** instead makes the provider ignore that row and may reveal a fallback value. The editor preserves the stored secret classification and never returns encrypted values or decrypted secrets to the browser.

**Save all changes** includes changed rows hidden by the current filter. The batch is atomic. A stale row rejects the entire save with HTTP 409, including changes made outside the UI. Refresh explicitly discards drafts. Values remain text: structural limits are checked, but credentials, URLs, JSON and application-specific constraints are not verified by this editor.

After saving, restart affected services through the deployment platform. Shared settings may require restarting API, Web and Worker. Coordinate matching internal API and actor-signing keys before restarting services; verify sign-in and service connectivity afterward. A browser refresh or Sentry reload does not reload these settings. The page does not claim saved values are currently running, and deployment overrides may still win. `DATABASE_URL` and `CONFIG_ENCRYPTION_KEY` remain external bootstrap configuration.

This editor updates existing rows in place; it does not create/delete keys, change encryption flags, retain immutable history, or provide rollback. Keep database recovery procedures available before rotating credentials. Sentry's revision history and live application behavior below remain separate.

No Sentry management token is needed for basic error reporting. Enter the project's ingestion DSN. The first slice supports hosted endpoints shaped like `o123.ingest.sentry.io`, `o123.ingest.us.sentry.io`, or `o123.ingest.de.sentry.io`, with HTTPS on port 443, a public key, and a numeric project ID. Self-hosted/legacy endpoints require a separately reviewed destination policy.

## Save, apply, and test

- **Validate and save** checks the fields and their combinations, encrypts a new immutable revision, and leaves the active revision unchanged. A secret has explicit Keep, Replace, and Clear actions; read responses never include its value. A DSN is required when reporting is enabled.
- **Apply saved revision** promotes precisely that saved revision. Conflicting edits receive HTTP 409 instead of overwriting newer work. API attempts a reload immediately; other services read the active pointer every 15 seconds. Failed validation/client creation retains the previous working client.
- **Refresh / discard unsaved edits** loads the latest settings and acknowledgements. **Reload active settings** retries application in API; it does not promote unsaved edits or restart processes.
- **Send test event from API** explicitly queues synthetic content through the active API client. A returned event ID means the SDK accepted the event locally, not that Sentry ingested it. Confirm receipt in the Sentry project. Disabled reporting, sampling, a full queue, or transport failure can prevent delivery. Use error sample rate 1 when testing.

The status table distinguishes the saved revision, selected active revision, each instance's applied revision, failed/pending application, and missing or stale heartbeats. An instance not heard from for 45 seconds is shown as offline or not reporting. Multi-service application is eventually consistent, not an atomic global cutover. The main view groups current instances under API service, Web application, and Background worker. Mixed or pending live replicas remain visible in the summary. Instance IDs and previous runs are available in a collapsed technical-details section; the newest 100 instance records are returned.

The saved settings apply across this deployment. Per-service environment overrides are supported for `SENTRY_ENABLED`, `SENTRY_DSN`, `SENTRY_ENVIRONMENT`, and `SENTRY_SAMPLE_RATE`. Nonempty overrides win over the database and are named in the instance status without their values. Startup `appsettings` keys under `Sentry` are not a second hidden input path. To manage a field exclusively through the app, remove its environment override using normal deployment configuration and restart that host.

## Error capture and data handling

Sentry SDK 6.10.0 runs through a privately owned client and a single `ILoggerProvider` per host. Error/Critical logs are captured with fixed redacted text, service, logger category, numeric event ID, exception type, and current trace ID if present. Raw messages, log arguments, exception messages/stacks, request bodies, journal content, and transcript content are not passed to the SDK. This intentionally limits diagnostic detail in the first slice. Sentry's own logs and integration infrastructure logs are excluded to prevent reporting loops.

Release identity uses the entry assembly's informational version. Build releases with source revision metadata to distinguish deployments; a build without that metadata may only identify the assembly version. SDK replacement drains/disposes the old client with a bounded two-second shutdown timeout. Export failures cannot fail application logging, but delivery is best effort and events can be lost on shutdown/outage. This is not a durable telemetry outbox. OpenTelemetry/OpenInference instrumentation and alert configuration remain separate backlog work.

## Persistence and recovery

`AgentPlayground.Integrations` owns an additive version-1 migration under a PostgreSQL advisory transaction lock, recorded in `app.integration_schema_versions`. All hosts may invoke it safely; the version is recorded only after transactional success. This establishes migration ownership for these tables, not for the rest of the repository.

- `app.integration_revisions`: encrypted configuration snapshots with author and creation time.
- `app.integration_heads`: saved and active pointers.
- `app.integration_applications`: promotion actor, revision, and time.
- `app.integration_instances`: runtime acknowledgements and heartbeats.

The store reuses the existing configuration crypto, with `Shared` scope and `Integrations:Sentry` authenticated context, without changing `app.configuration_settings` or reloading its startup-only consumers. Revisions and promotion history are retained until an explicit future retention policy/migration removes them; clearing a DSN clears the current value, not historical encrypted revisions or backups. Treat these tables and backups as sensitive. Restore with the same external encryption key. Key rotation and history browsing/revert UI are not included; manually correcting values and applying creates a new revision.

If bootstrap or database access fails, the page returns a safe unavailable response, and the worker retries without replacing its last working client. If an override is invalid, the instance reports failed application and retains the previous client. Use ordinary service logs and deployment diagnostics to investigate; settings errors do not expose decrypted values.

## Verification

For a disposable synthetic environment with reporting disabled and no configured DSN:

```powershell
docker compose -f compose.synthetic.yml up --build --detach --wait --wait-timeout 180
./scripts/tests/Test-SyntheticDemo.ps1
./scripts/tests/Test-IntegrationSettings.ps1 -RestartWorker
```

The integration smoke suite saves a fake DSN with reporting disabled, verifies authorization and conflicts, applies across all services, restarts the synthetic Worker, and clears the fixture through a new applied revision. It refuses to overwrite preconfigured credentials and sends no Sentry events. CI runs both smoke suites.

API tests also exercise encryption, concurrent migration, SDK envelope redaction/queueing, enable/replace/disable/re-enable, stale revisions, and service authorization. Web component tests verify secret keep/clear behavior, invalid form submission, secret clearing after save, and stale instance presentation. Live project ingestion remains a manual check with the user's DSN.

For direct HTTP examples, see `PersonalAgent/IntegrationSettings.http`. The internal key and signed actor headers are required: sign the UTF-8 payload `actor + "\nOwner\n\n" + unixTimestamp` with HMAC-SHA256 using the server actor signing key and encode as uppercase hexadecimal. Refresh the timestamp/signature within five minutes; keep real values out of committed files. The synthetic smoke script demonstrates this with public fixture credentials.
