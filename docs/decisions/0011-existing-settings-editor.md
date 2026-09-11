# 0011: Edit existing database configuration in the app

- Status: Accepted; implemented
- Recorded: 2026-09-11
- Decision date: 2026-09-11, maintainer requested editing all existing settings after deploying PR #40
- Evidence: maintainer feedback; `PostgresConfigurationSource`, `DatabaseSettingsStore`, `DatabaseSettings.razor`
- Extends: [0010](0010-runtime-integration-settings.md), preserving [0001](0001-database-configuration.md) startup semantics

## Context

The Sentry form did not meet the expectation of editing settings already stored in `app.configuration_settings`. Existing startup consumers do not all support reconfiguration. Sentry acknowledgements describe only its dedicated runtime settings.

## Decision

Provide an administrator-only editor of every existing configuration row, including custom keys, inactive rows and arbitrary scopes. Preserve row identity and secret classification. Read responses omit secret values; explicit replacement encrypts with the existing scope/key context. Null means keep; empty means clear. Save changed rows as one transaction, using PostgreSQL row versions to reject concurrent changes, including writes from seed scripts. Maintain production `updated_at` when present and support older synthetic tables without that column.

Show service names, filtering and grouped keys. Label every save as requiring a restart of affected services. Do not infer live values or remote environment overrides from stored rows. Use the existing signed Owner and deployment administrator filters, including internal API authentication. Relabel the Sentry table to state its actual scope.

## Alternatives

- A fixed field registry would omit custom database keys. Use direct inventory for this editor.
- Reloading all services' configuration would require changes to startup-bound consumers and coordinated credential rotation. Defer it.
- Migrating legacy rows into Sentry's revision schema would break existing readers and operational scripts. Edit in place instead.

## Consequences

Administrators can manage existing values without SQL. This is a text editor with structural request validation, not provider credential or application-format validation. Incorrect values can prevent the next startup; recovery still uses deployment configuration and the database. Secret classification remains the database's responsibility. New rows, deletion, reclassification, immutable legacy history/rollback, semantic validation, and confirmed runtime state remain future work. Sentry retains its separate validation, revisions and live reload.

## Delivery and verification

One Settings page at `/admin/settings` contains both the database editor and Sentry controls. The old `/admin/integration-settings` route is an alias. Each section retains its own save state and lifecycle controls. PostgreSQL tests cover production and older synthetic schemas, encryption, secret retention/clearing, custom and inactive rows, external-write conflicts and batch rollback. API tests cover unauthorized actors; Web tests cover edits across filters, secret cleanup and conflict feedback. See [operations](../runbooks/integration-settings.md) and `CONFIG-01` in the [roadmap](../plans/roadmap.md).
