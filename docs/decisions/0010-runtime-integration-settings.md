# 0010: Configure integrations through validated application settings

- Status: Accepted; Sentry first slice implemented
- Recorded: 2026-09-10
- Decision date: 2026-09-10; maintainer authorized implementation
- Evidence: maintainer request; `AgentPlayground.Contracts/Configuration/PostgresConfigurationSource.cs`, `ConfigurationValueResolver.cs`, and `PersonalAgent.Web/Components/Pages/IntegrationsAdmin.razor`
- Related: [0001: Database configuration](0001-database-configuration.md); adds a dedicated runtime store while preserving existing startup-only consumers

## Context

The maintainer wants to enter integration settings in the app and validate/reload them, beginning with Sentry. PostgreSQL already stores encrypted secrets and scoped configuration. The configuration provider loads at startup and does not publish change notifications. Explicit environment-variable lookups can override database values. The existing integration administration page manages role/tool access and coach assignments, not integration credentials.

## Decision

Use a registered integration settings model and administration flow, with Sentry as the first implementation. The following describes the accepted direction; the delivery section distinguishes the implemented slice from remaining extensions. It does not commit to hot-reloading every SDK.

Each registered integration defines typed fields, required/conditional validation, secret handling, service scope, effective-value precedence, and an application policy: live update, controlled client reinitialization, or restart required. New supported integrations contribute a definition and adapter in code; users configure their values without editing deployment variables or SQL. Arbitrary key entry cannot install an integration or make an unbound setting take effect.

Provide an authorized application-administrator page, enforced again by API endpoints. Explicitly define deployment-level administration: being an owner of one subject must not automatically authorize editing shared service credentials. Reuse encrypted database storage. Do not return stored secrets to the browser; show configured/missing state and separate keep, replace, and clear actions. Bootstrap database access and `CONFIG_ENCRYPTION_KEY` remain outside this UI.

The user flow is: open an integration, enter settings, receive field validation, save a candidate revision, validate it server-side, and apply it. Show saved revision, validation time/result, effective source, and applied revision per service instance. Make “saved,” “validated,” “applied,” and “restart required” distinct states. An environment override must be visible as an override rather than falsely claiming the database value is active.

Reject invalid candidates before activation. Use optimistic concurrency to avoid overwriting another edit. Apply compatible settings from an atomically validated snapshot, retaining the last working runtime configuration on failure. A database reload alone is insufficient: each options consumer/client must explicitly support updates. For multiple services, send revision identifiers rather than secrets, use durable reconciliation so missed notifications converge, and acknowledge application per instance. Do not claim a globally atomic cutover; show partial application and offline instances. Reverting configuration creates a new revision and repeats validation/application; credential revocation may prevent an old value from working.

Keep change history with actor, scope, changed field names, revision, and outcomes; exclude secret values from audit logs and telemetry. Define encrypted revision retention and schema migration before adding history tables.

### Sentry first slice

Expose enabled state, project DSN, environment, and the supported sampling controls selected for the first SDK integration. Derive release identity from the deployed build. Distinguish the ingestion DSN from management/upload tokens; do not require broad account credentials for basic error reporting.

Validate field syntax and combinations immediately, then on the server. Offer an explicit **Send test event** action using synthetic content; show the event identifier and transport outcome without claiming confirmed issue ingestion unless verified. Bound timeouts and reject unsafe outbound destinations for configurable endpoints. Validation/probes must not send application journals, transcripts, prompts, or secrets.

First verify lifecycle behavior with the chosen Sentry SDK and host integration: initial enablement, DSN replacement, disabling, reinitialization, in-flight event handling, and duplicate instrumentation. Prefer validated apply without restart where supported; label and report restart requirements where startup-bound components prevent it. Never expose an unrestricted process restart endpoint as a substitute for configuration application. Start with API, then demonstrate propagation and status for Web and Worker.

## Alternatives

- Keep scripts/deployment variables: retain for bootstrap and recovery, but they do not provide the requested in-app workflow.
- Add a Sentry-only form: smaller initially, but duplicates validation, encrypted persistence, and apply status for the next integration.
- Reload all settings and clients indiscriminately: cannot guarantee safe behavior for startup-bound services or consistent effective values.

## Consequences

The integration setup experience becomes reusable and inspectable. It adds lifecycle, authorization, revision, and distributed-application responsibilities. The first slice covers Sentry and shared settings machinery; migrating every existing provider setting is separate work.

## Delivery and verification

Implemented: a separate `AgentPlayground.Integrations` host-infrastructure library, encrypted immutable revisions, explicit administrator allowlist, field validation, save/apply/reload/test endpoints and Blazor UI, 15-second durable pointer reconciliation, per-instance status, and replaceable Sentry 6.10.0 clients in API/Web/Worker. Existing AI provider ownership remains unchanged. Error export is limited to metadata through one logging provider; raw exception and log contents are excluded. No global SDK initialization or general configuration reload is used.

The additive integration schema has its own versioned transaction under an advisory lock; broader database migration work remains open. Revisions retain encrypted values plus author/time; promotions retain actor/time. Historical encrypted revisions are retained with the database and backups. UI history/revert, field-level audit presentation, additional integrations, self-hosted Sentry, tracing, and alerts remain follow-ups. Validation is performed synchronously on save and apply; no external credential-validity claim is made. The test action reports queue acceptance, not confirmed ingestion.

Local verification: 90 API tests, 16 Web tests, and 19 Worker tests passed, along with 22 synthetic application checks and 13 integration checks including a Worker process restart. The real SDK test uses an in-memory transport. See [setup and operational limits](../runbooks/integration-settings.md). Live Sentry project receipt is not yet verified.

Track `CONFIG-01` and `OPS-01` in the [roadmap](../plans/roadmap.md#selectable-backlog). Acceptance: an administrator configures Sentry through the UI, tests it with a synthetic event, and sees accurate applied/restart status. Invalid settings preserve working behavior; secrets are not echoed; unauthorized users and stale edits are rejected; overrides are explained; offline/restarted instances converge; SDK lifecycle tests demonstrate supported reload behavior and no duplicate reports.

Source guidance checked 2026-09-10:

- [Microsoft options pattern](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/options?view=aspnetcore-10.0): `IOptionsMonitor` provides current values and change notifications; consumers must use the appropriate lifetime.
- [Microsoft change tokens](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/change-tokens?view=aspnetcore-10.0): configuration changes need a notification mechanism.
- [Sentry SDK options source](https://github.com/getsentry/sentry-dotnet/blob/main/src/Sentry/SentryOptions.cs): DSN and environment are SDK configuration options; their presence does not establish safe runtime host reconfiguration.
