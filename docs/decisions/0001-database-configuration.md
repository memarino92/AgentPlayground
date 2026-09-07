# 0001: Store application configuration and encrypted secrets in PostgreSQL

- Status: Accepted, retrospective
- Recorded: 2026-09-07
- Decision date: Not reconstructed; implementation commit `34d65e2`
- Evidence: `AgentPlayground.Contracts/Configuration/`, `scripts/seed-configuration.ps1`, service startup files

## Context and decision

The implementation loads active `Shared` and service-scoped settings from `app.configuration_settings`. Service rows override shared rows. Secrets are encrypted with a separately supplied base64 32-byte `CONFIG_ENCRYPTION_KEY`; `DATABASE_URL` locates the configuration database. Both bootstrap variables must be set together. If neither is supplied, the existing local configuration path remains available.

Reducing configuration drift across Railway services is inferred rationale, rather than a recovered historical discussion. The implementation is the evidence for the accepted decision.

## Alternatives and consequences

Per-service environment variables remain useful for bootstrap and supported overrides, but duplicate application configuration. A dedicated secret manager is a possible later choice; none is introduced here.

Database access and the decryption key are now startup dependencies. A database dump alone cannot recover encrypted settings without the matching key. The provider loads at startup; editing rows does not provide live reload. The database provider is appended after normal configuration providers, while explicit environment lookups in option binders can still take precedence. Do not assume all keys have identical override behavior.

## Delivery and verification

Configuration loading, seed encryption, and crypto tests exist. `WorkerExtensions` still reads GitHub settings through older direct bindings, so binding consistency needs a follow-up. Validate scope precedence and real application startup after restore, not only encryption round trips. Track key custody, local re-seeding, and recovery in [0003](0003-local-parity-and-recovery.md).
