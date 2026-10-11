# 0042: Connect per-user ChatGPT plans through local OAuth pairing

- Status: Accepted; implemented, live account verification pending
- Recorded: 2026-10-10
- Decision date: 2026-10-07; maintainer requested subscription-backed OpenAI access, then confirmed both local and Railway support
- Evidence: `ChatGptOAuth`, `ChatGptConnectionStore`, `ChatGptChatClient`, `scripts/ChatGptConnect`, and [operations](../runbooks/chatgpt-plan-sign-in.md)
- Extends: [0016: Database chat model policy](0016-database-chat-model-policy.md) and [0032: OpenRouter routing](0032-openrouter-auto-routing.md)

## Context

OpenAI’s September 2026 Sign in with ChatGPT release supports eligible subscription-backed Responses requests. Its open-source flow uses dynamic public-client registration, PKCE and an HTTP callback on `127.0.0.1`. That callback reaches the browser’s computer, including when the application runs on Railway. The existing app uses independently authorized GitHub/Google identities, API-owned providers, encrypted database settings and Chat Completions.

Sources: [registration](https://developers.openai.com/siwc/token-sharing-open-source/sign-in), [self-hosted VMs](https://developers.openai.com/siwc/token-sharing-open-source/self-hosted-vms), [inference](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference), and [account lifecycle](https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions).

## Decision

Link ChatGPT accounts to existing authenticated Owners. Preserve the application’s account authorization and session boundary. The general website identity-only flow has a separate partner availability policy; this implementation uses the open-source plan flow.

Settings issues a short-lived, one-time pairing capability bound to the actor and, on reconnection, the connection version. A local .NET helper starts the exact loopback listener, exchanges the OAuth code locally, validates identity and sends the credentials over HTTPS to the paired instance. HTTP is allowed only for a loopback instance. The API independently verifies the ID token against its saved nonce before committing the connection. Credentials never enter the app’s browser storage or a local credential file. The public Web relay has a fixed API destination, request-size limits and per-IP rate limiting.

Each installation persists its own host ID. Registrations remain separate by issued client ID, including accounts with identical emails. Credentials and pending transactions use the existing authenticated encryption. Database row locks serialize rotating refresh tokens across API instances and with disconnection. Reauthorization checks subject, client ID and version before replacing anything. Disconnect retains the mapping for later sign-in and reports unconfirmed remote revocation.

Add a qualified `chatgpt:<connection-id>:<model-slug>` provider behind `IChatClient`. Discover account-specific availability and intersect it with database-owned `ChatGpt:ReviewedModels`. Seed GPT-6.1 Sol and GPT-6 Astra once without changing global defaults; subsequent policy edits and deletion remain authoritative. Read policy live, and reject malformed edits in Settings. Existing API providers, scheduled-job defaults and credentials remain independent.

Use the SDK’s Responses adapter, always stream with storage disabled, replay context and tool outputs, and require a completed terminal event. Opt into the pinned SDK’s experimental Responses surface only in this adapter and its transport tests. No project-wide warning suppression is added. Never silently retarget a subscription conversation after disconnection, quota failure or provider outage. Show plan use and a usage-management link in the composer.

## Alternatives

- Remote browser callback: fails the documented loopback registration contract.
- Copy tokens into a Settings form or user secrets: risks browser exposure, manual identity mixing and competing refresh owners.
- Share a deployment-wide subscription token: would charge the wrong actor and violate account isolation.
- Replace application login: deferred because identity-only website registration has different availability and must preserve existing authorization.
- Move every provider to Responses: unnecessary to deliver subscription access and would broaden compatibility risk.

## Consequences

Connecting requires a local helper and an eligible ChatGPT account. The instance owns the renewable session after transfer; the helper retains no token file. Database backups now contain encrypted OAuth credentials and require the existing encryption-key protections. OpenAI’s self-hosted guide notes limitations on transferred-session host attribution and revocation of plan access. Embeddings, transcription, background jobs and existing API providers retain their separate credentials and billing.

## Delivery and verification

Implemented: Settings setup and account management, paired local OAuth, encrypted persistence, per-actor discovery/dispatch, serialized refresh, disconnect, first-use acknowledgement, plan indicators, live reviewed policy and CI helper build. Tests use signed synthetic identities, fake OpenAI transports and disposable PostgreSQL. They cover identity/callback rejection, consent separation, replay, expiry, workspace separation, encrypted reads, ownership, refresh concurrency, revocation, provider outage, policy removal, storage-free streaming, interrupted streams and the tool-call round trip. Web tests cover initial setup, onboarding dismissal, API mismatch and revocation feedback.

Live Plus/Pro authorization, live model entitlement and a Railway deployment remain unverified. See [the rollout procedure](../runbooks/chatgpt-plan-sign-in.md) and [remaining acceptance criteria](../plans/roadmap.md).
