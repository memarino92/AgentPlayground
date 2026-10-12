# 0044: Subscription models for platform coding

- Status: Accepted; implemented locally, deployment and live subscription verification pending
- Recorded: 2026-10-11
- Decision date: 2026-10-11; owner requested coding through their existing OpenAI subscription and larger task limits
- Extends: [0042](0042-chatgpt-plan-sign-in.md)

## Context

Platform coding used a fixed OpenRouter mini model and twelve requests. A live job exhausted that allowance before validation. Chat already owns encrypted, per-actor ChatGPT registrations, refresh serialization and reviewed account model discovery.

## Decision

Reuse those registrations for coding. Settings selects an account-qualified model from the current owner's reviewed catalog. Validate ownership and availability at save/start and during inference. Snapshot the selection in each job. The API alone refreshes and uses OAuth credentials; sandbox containers receive only an expiring job capability. OpenCode uses its Responses provider through the gateway. Subscription requests use streaming without storage, replay text/tool context and group local functions in a namespace. There is no paid fallback.

Allow subscription jobs up to 1,000 model requests, independent of the OpenRouter dollar reservation. Keep existing OpenRouter defaults and ceilings for existing installations. Settings changes affect new jobs. Plan usage remains subject to account limits, with explicit errors and a usage-management link. Existing runtime, artifact and maintainer publication controls remain in force.

## Alternatives

Copying refresh tokens into sandbox VMs creates competing refresh owners and exposes credentials to generated code. A deployment-wide subscription account violates actor isolation. Keeping only Chat Completions prevents use of the documented subscription inference endpoint.

## Consequences

API/Web/Worker must deploy together and the immutable coding checkpoint must be rebuilt. Bigger request limits increase plan usage and Railway compute; they do not imply unlimited entitlement. Model availability remains account-specific.

## Delivery and verification

Implemented model/account selection, gateway Responses dispatch, larger subscription request allowances, live authorization and model checks, safe errors and metadata-only HTTP status/timing logs. Synthetic validation passed API/Web tests and both pinned OpenCode Docker protocols with a real tool edit/export. Track exact evidence and remaining rollout criteria in the roadmap and [coding runbook](../runbooks/platform-coding.md). Live subscription coding requires the owner's connected account and a separately authorized live run.

Sources: [OpenAI inference](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference), [preview requirements](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations), [OpenCode provider configuration](https://docs.opencode.ai/docs/providers/).
