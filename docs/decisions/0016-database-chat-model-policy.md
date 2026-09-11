# 0016: Database-owned chat model policy

- Status: Accepted; implemented
- Recorded: 2026-09-11
- Evidence: maintainer requested removal of appsettings model ownership; `ChatModelCatalog`, `DatabaseSettingsStore`
- Extends: 0001, 0004, 0011

## Context and decision

The model catalog discovers provider availability at runtime but captures a startup-bound policy assembled from appsettings and database rows. Make active Shared/Api `ChatModels:*` database rows the sole production policy source. Preserve Api-over-Shared precedence, existing administrator edits, and the provider capability allowlist. Seed initial model rows only when no model policy rows exist, including inactive rows in that check. The seed is migration input, never a runtime fallback. Synthetic mode retains an explicit fixed test policy.

Read policy on each catalog request and invalidate provider results when policy changes. Empty policy is authoritative and disables new chat selection; invalid policy/database failure must not revive removed models. Saved sessions retain their model. The generic database editor can edit existing rows, so preseeded model IDs can be changed there; general row creation remains separate work.

## Alternatives and consequences

Restart-only database binding would retain stale choices after editing. Globally reloading configuration would affect unrelated startup consumers. A dedicated policy table/form would duplicate the existing editor. Reading the small policy on each catalog request adds a database read, but keeps revocations independent of the provider cache. Initial migration requires the same database permissions/bootstrap as existing settings.

## Delivery and verification

Implemented: automatic insert-only seeding, database-only reads, runtime cache invalidation, and settings-page lifecycle labels. PostgreSQL tests cover both current and older settings schemas, precedence, preserved/inactive/empty policy, edits through the existing editor, restart behavior, and invalid policy. Provider failure does not resurrect removed choices. Invalid policy or database failure blocks catalog requests rather than falling back to appsettings. The initialization marker prevents reseeding even after all model rows are deleted. Existing database model entries are never extended automatically: administrators retain ownership of eligibility and defaults. See [runtime operations](../runbooks/runtime-chat-models.md).
