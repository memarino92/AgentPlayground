# 0003: Prove recovery locally before adding another hosted environment

- Status: Accepted; snapshot/restore slice implemented
- Recorded: 2026-09-07
- Context: Maintainer request; predominantly single-user application with occasional family/coach access

## Decision

Keep the existing Railway production deployment and use a reproducible local PostgreSQL environment for development and isolated recovery rehearsals. Defer another Railway environment until local checks cannot validate a specific hosted behavior.

Use a full custom-format logical dump for recovery and a separate sanitization step for local development. The original archive remains immutable. A development clone must use local configuration and keys, fresh messaging infrastructure, and no production push destinations or external automation credentials. Maintain a synthetic seed for public demos and contributors who cannot access private data.

## Alternatives and consequences

A permanent staging environment adds secrets, data synchronization, and cost without resolving the need for repeatable restores. It becomes worthwhile for deployment, proxy, or OAuth changes that cannot be reproduced locally. Synthetic data alone is insufficient for diagnosing production data shape; an unsanitized production clone is unsafe to start with workers enabled.

Daily logical backups imply a potential loss window approaching the backup interval, plus failures. A continuous recovery mechanism is a separate future decision if that loss becomes unacceptable. Proposed initial objectives are recovery point within 24 hours and recovery within 2 hours; measure and accept or revise these after a rehearsal.

## Delivery and verification

The backup uploader exists. Export and guarded local restore helpers now support full recovery and a development state reset; see [snapshot commands](../runbooks/snapshot-commands.md). Synthetic restore tests verify checksums, vectors, encrypted settings, retained records, isolation, and failure guards. A real production backup and full application recovery are **not yet demonstrated**. The [recovery runbook](../runbooks/database-recovery.md) defines the first deliverable, key custody, transport replay policy, and evidence required before calling recovery proven.
