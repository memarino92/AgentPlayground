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

Transcript content, evidence and audio now require the Owner's own subject or a current Coach assignment. Some older administration listings remain global Owner capabilities; this change does not make every administrator endpoint tenant-isolated.

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

## Retained audio fixture

Run `pwsh -NoProfile -File scripts/tests/Test-RetainedAudioEvidence.ps1` against the running demo. It runs the existing 22 checks, uploads a valid 30-second silent PCM recording, completes speaker review, verifies retained timing/audio and checks authenticated Web ranges for all three personas (30 checks total). It prints a source link seeking to the coach utterance at four seconds. Sign in as Demo owner to inspect playback. The legacy upload route still requires an `.m4a` filename; this synthetic fixture deliberately uses that suffix with `audio/wav` bytes. It tests playback/transport, not speech alignment or format validation. Avoid rapidly repeating the suite because the API rate limit also applies to test requests.

Use **Read and listen to evidence** on the transcript page to open the drawer. Test speed, seeking, timestamp buttons and skips at both recording ends. Missing historical audio remains readable. Removal requires an explicit confirmation in the drawer and applies only to Completed/Failed calls. The script retains its synthetic recording for browser inspection and does not claim a backup/restore rehearsal.

## Limits

This is a local demonstration, not a hosted deployment configuration. Its keys/passwords are deliberately public and synthetic. Demo startup requires Development mode and an allowlisted dedicated database. Production mode refuses an enabled demo flag.

AI results are fixed fixtures; vectors use stable token hashes, not a learned embedding model. This verifies routing and persistence, not answer quality. API/Web need a browser-facing Docker network for port publishing and are not firewall-isolated from outbound traffic; synthetic providers, disabled Tavily/push, and disabled Worker journal sync avoid those integrations. The Compose file imports no `.env.compose` or private credential mounts.

The smoke test does not prove production backup recovery, remote-provider behavior, crash-safe domain completion/outbox delivery, OAuth/WebView behavior, or Android readiness.
