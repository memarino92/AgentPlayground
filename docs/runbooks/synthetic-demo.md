# Synthetic local demo

Requires Docker Desktop with Linux containers and Docker Compose v2. The first build downloads .NET and PostgreSQL images and NuGet packages. Application execution needs no OAuth, AI, Firebase, user secrets, private seed file, or host .NET SDK.

From the repository root:

```powershell
docker compose -f compose.synthetic.yml up --build --detach --wait --wait-timeout 180
```

Open [the demo sign-in page](http://127.0.0.1:15000/login). API is bound to `127.0.0.1:15100`; PostgreSQL has no published port. API readiness follows schema/seed initialization; Web and Worker depend on it. The smoke test verifies Worker delivery explicitly.

## Walkthrough

1. Choose **Demo owner**. Open the seeded conversation or start a new chat and ask, “What is my favorite exercise?” The deterministic reply recalls the deadlift fact from this account's stored vector memory.
2. Open coach check-ins to inspect the seeded two-speaker review. Upload a small `.m4a` file to exercise the actual API -> Worker -> transcription gateway path. In demo mode the bytes always produce the same two-speaker example; no audio leaves the application.
3. Assign speaker roles and inspect the resulting transcript. The existing Worker extraction/tagging pipeline and gateway embeddings run locally.
4. Sign out and choose **Assigned coach**. This identity can see `demo-owner` data through the existing assignment checks. **Other owner** has a separate conversation and no assignment to that athlete.

The persona mapping is fixed: `demo-owner`, `google:synthetic-coach` / `coach@example.test`, and `demo-other`. Synthetic sign-in does not accept arbitrary role or profile claims. It requires a form antiforgery token.

Owner accounts retain the application's existing global transcript-administration access. The other-owner scenario verifies private chat and subject-scoped endpoints; it does not make the Owner administrator role tenant-isolated. Coach access remains assignment-scoped.

## Verify

With PowerShell 7 installed:

```powershell
pwsh -NoProfile -File scripts/tests/Test-SyntheticDemo.ps1
```

The script checks API/Web startup, configuration-backed credentials, unsigned actor rejection, owner and coach isolation, stored chat/vector recall, real bus transcription delivery to speaker review, and cookie sign-in for all three personas. Each run adds a synthetic chat and upload. CI runs this script against a fresh volume.

Validation on 2026-09-10: 22 smoke checks passed on Windows Docker Desktop from an empty volume and after API recreation with retained data. Interactive browser sign-in, chat creation, and recalled-memory display also passed. All 118 server tests passed, including the five PostgreSQL transcription job tests. Remote CI execution remains pending.

```powershell
docker compose -f compose.synthetic.yml logs --tail 100
docker compose -f compose.synthetic.yml down
```

`down` preserves demo data. To explicitly discard only the demo's volume and start clean:

```powershell
docker compose -f compose.synthetic.yml down --volumes
docker compose -f compose.synthetic.yml up --detach --wait --wait-timeout 180
```

The default project name is `agentplayground-demo`; its volume is separate from `start-postgres.ps1` and `docker-compose.yml`. Do not override the project/database names or mount private data into this stack. An existing database without the synthetic marker is rejected. Seed inserts are repeatable and retain edits on restart.

## Limits

This is a local demonstration, not a hosted deployment configuration. Its keys/passwords are deliberately public and synthetic. Demo startup requires Development mode and an allowlisted dedicated database. Production mode refuses an enabled demo flag.

AI results are fixed fixtures; vectors use stable token hashes, not a learned embedding model. This verifies routing and persistence, not answer quality. API/Web need a browser-facing Docker network for port publishing and are not firewall-isolated from outbound traffic; synthetic providers, disabled Tavily/push, and disabled Worker journal sync avoid those integrations. The Compose file imports no `.env.compose` or private credential mounts.

The smoke test does not prove production backup recovery, remote-provider behavior, crash-safe domain completion/outbox delivery, OAuth/WebView behavior, or Android readiness.
