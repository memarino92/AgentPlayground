# 0008: Run a synthetic local garden on real storage and delivery

- Status: Accepted; implementation and validation in this change
- Recorded: 2026-09-10
- Decision date: 2026-09-10
- Evidence: Maintainer approved the synthetic local environment slice; `compose.synthetic.yml`, `SyntheticEnvironment`, API `Development/`, Web `SyntheticAuthentication`, `Test-SyntheticDemo.ps1`

## Context

Manual local startup required OAuth and provider credentials. Compose omitted current signing/transcription settings, mounted Firebase unconditionally, and used a fixed Worker startup delay. This prevented a fresh checkout from demonstrating the product without private configuration.

## Decision

Use an explicit `SyntheticDemo:Enabled` flag together with Development mode and a dedicated local `agentplayground_demo` database. API initializes encrypted demo configuration before the ordinary configuration provider loads it, then seeds synthetic conversations, memory, a coach assignment, and a pending speaker review. Refuse an existing unmarked database and preserve existing demo rows on restart.

Keep PostgreSQL, pgvector, signed actor requests, scoped authorization, and MassTransit delivery real. API-local adapters produce deterministic chat, token-hash vectors, fixed journal extraction, and a two-speaker transcript. Add a chat-client factory and journal parsing interface to select adapters without client or wire-contract changes.

Web offers three predefined sample identities with cookie authentication and antiforgery-protected sign-in. This route is registered only in the guarded demo mode. Production OAuth remains the normal path. Public demo credentials belong only to this disposable local environment.

## Alternatives

Requiring development OAuth/provider accounts would retain setup and cost barriers. In-memory stores would bypass the configuration, vector, and transport behavior this slice needs to exercise. A production clone is useful for private diagnosis, but cannot serve as a public synthetic fixture.

## Consequences

The demo needs Docker, but no host .NET SDK or provider keys. PostgreSQL and Worker share an internal network; API/Web also attach to a browser network to support loopback-published ports on Docker Desktop. This is not an outbound firewall for API/Web. Their selected synthetic adapters and disabled push/search/sync prevent normal demo operations from using external providers.

Token-hash vectors verify storage, account scope, and nearest-neighbor execution, not semantic retrieval quality. Fixed chat does not choose tools, and fixed extraction does not evaluate arbitrary source material. Keep the embedding space confined to the dedicated demo database.

Full application startup exposed two pre-existing integration defects: Worker registered a scoped request-client dependency as a singleton, and API denials used authentication-handler-based `Forbid()` without an authentication handler. Use a scoped transcription service and explicit HTTP 403 results.

## Delivery and verification

See [synthetic demo](../runbooks/synthetic-demo.md) for commands, personas, checks, and limitations. CI starts the stack and runs the same HTTP smoke test. This implements the synthetic local parity slice of roadmap item 1; production-archive recovery, outbox guarantees, mobile identity, and model quality evaluations remain separate work.
