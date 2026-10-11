# ChatGPT plan connections

This integration links an eligible ChatGPT account to an existing application Owner. Sign into the app normally first. The app’s existing access policy still controls personal data and tools.

## Deploy and connect

1. Deploy matching API and Web revisions. Both Dockerfiles already include `PersonalAgent.Integrations`; watch paths must include the owning service, `PersonalAgent.Contracts/**`, `PersonalAgent.Integrations/**`, `Directory.Packages.props` and the Dockerfile. Rebuild Worker when changing the shared library. No new deployment environment variables are required.
2. Retain the existing database/encryption bootstrap. Startup creates the ChatGPT tables and a stable installation host ID. It inserts `Api / ChatGpt:ReviewedModels` only on the first installation initialization, preserving existing values.
3. Open **Settings → AI providers → Continue with ChatGPT**. Settings creates a temporary pairing code for your app identity. Starting another pairing invalidates the earlier code for that actor.
4. On the computer running your browser, from an AgentPlayground checkout with the pinned .NET SDK, run:

   ```powershell
   dotnet run --project scripts/ChatGptConnect -- https://your-instance.example
   ```

   Use the same command with the local instance URL for local development. HTTPS is required remotely. HTTP is accepted only for a loopback instance. Trust the local development certificate normally; do not disable certificate verification.

5. Paste the pairing code at the helper’s prompt. It expires after ten minutes. The helper listens on `http://127.0.0.1:1455/auth/callback` before opening the system browser. Choose the account/workspace and authorize plan usage.
6. After the helper reports success, refresh connections in Settings. A first-use acknowledgement explains plan billing. Select a model labelled **ChatGPT** in the chat composer. Selecting a ChatGPT model is explicit; connecting does not change the global default or existing saved selections.

For Railway, run the helper locally with the Railway Web URL. The Web service relays only the fixed pairing protocol to the configured API. Tokens transfer directly from the local runtime over HTTPS and are encrypted by the API. Neither browser storage nor a credential file is used. The API becomes the sole refresh owner. See [OpenAI’s VM guidance](https://developers.openai.com/siwc/token-sharing-open-source/self-hosted-vms) for transferred-session attribution limitations.

## Account selection and policy

Each registration receives a distinct stable label. Identical emails can represent different workspaces; choose the corresponding labelled model in the selector. Use **Reconnect** for an existing registration, or **Continue with ChatGPT** for another account/workspace. Reconnect reuses the client and host identifiers and validates the returning subject before replacement.

The account’s live `/v1/models` catalog must report a visible model and its slug must appear in the JSON array at `Api / ChatGpt:ReviewedModels`. The initial reviewed list is `["gpt-6.1-sol","gpt-6-astra"]`. Edit this row in Database settings after reviewing capability compatibility. Changes apply on the next catalog request; malformed arrays are rejected. `[]`, an inactive row, or deleting the row removes all new ChatGPT model choices. Restarting does not recreate a deleted policy. Existing conversations keep their selected provider; unavailable access fails visibly.

The existing OpenAI API key remains required by the application for embeddings and other API-backed features. AssemblyAI, OpenRouter and background-job defaults remain independent. Connecting a plan does not make those operations subscription-backed.

## Refresh, disconnect and rollback

Access tokens refresh near expiry. Refreshes for the same connection use a database row lock across API instances and atomically replace the token set. Keep the database master encryption key available across deployments. Do not run another process against a copy of the same refresh token.

**Disconnect** attempts OpenAI session revocation and clears access, refresh and ID tokens locally while retaining the account/client mapping. If remote revocation cannot be confirmed, Settings says so and directs the user to disconnect the app in [ChatGPT Settings → Usage](https://chatgpt.com/settings/usage). Reconnect to restore access.

To roll back provider use, select an existing API/OpenRouter model for the conversation and disconnect the ChatGPT registration. Keep the additive tables and the master encryption key until all renewable sessions have been handled. Do not attempt manual token edits or log token values. Existing database backup restrictions apply to the encrypted connection tables.

## Troubleshooting and verification

- **Older API release:** deploy the matching API/Web revisions and retry. A Web-only deployment cannot create pairings.
- **Pairing expired/already used:** create a fresh pairing. A helper consumes preparation once; avoid running two helpers with one code.
- **Port 1455 unavailable:** stop the other loopback authorization listener and start a fresh pairing. The helper uses Kestrel and does not require a Windows HTTP URL ACL.
- **Identity or returning-account mismatch:** discard the attempt. Original credentials remain intact; choose the intended account and reconnect.
- **Connected without plan permission:** reconnect and grant plan usage. Identity-only consent cannot enable inference.
- **No ChatGPT models:** check account entitlement, reviewed policy and API availability. Provider inventory failure leaves healthy global choices available.
- **Usage limit or unavailable plan:** use the composer’s **Manage usage** link. A streamed failure is not saved as a completed answer; the app does not switch to paid API billing automatically.

Run the API and Web suites plus `dotnet build scripts/ChatGptConnect/ChatGptConnect.csproj` for synthetic validation. Tests use fake providers and disposable PostgreSQL; they do not prove live account entitlement. For the live rollout, authorize an eligible account, complete one short chat and one tool-bearing chat, verify the plan indicator and usage page, then reconnect/disconnect. Verify API and Web deployment revisions before attributing any failure to the provider. Never capture credentials or unredacted authorization/callback URLs as operational evidence.

The Docker smoke test is `./scripts/tests/Test-SyntheticDemo.ps1` after starting `compose.synthetic.yml`. It covers the real Web relay, signed-owner access, coach rejection, PKCE preparation, replay rejection and replacement of abandoned pairings without contacting OpenAI. API, Web and Worker Docker images were built and all 30 smoke checks passed on 2026-10-11. The API suite passed 417 tests, the Web suite passed 82 tests and the Worker suite passed 48 tests with sandbox tests excluded. The local helper built with zero warnings. Live account authorization and Railway rollout remain pending.
