# Public-release review

Reviewed 2026-09-07. This is a source review and local validation record; repository visibility and production resources were not changed.

## Completed cleanup and rationale

| Removed/changed | Evidence |
| --- | --- |
| Test-event contracts, three consumers, registrations, generated-message chat tool and prompt, model-backed event generator, facade method | No current UI publisher; remaining uses were the exploratory agent/consumer chain and its own tests |
| Demo-only worker harness tests | Exercised only the retired logging consumers; real scheduling/journal/transcript tests remain |
| Four `Placeholder_Passes` smoke tests | Asserted only that `true` is true |
| Project-level API/Web Dockerfiles | Omitted shared Contracts and central package/build files; active Compose and Railway script definitions use root Dockerfiles |
| Four obsolete API DTOs and Web `AuthenticationOptions` | No remaining references; the old coaching DTOs predate Worker transcript processing, and authentication binds current options directly |
| MAUI `dotnet_bot.svg` | Template asset with no application reference |
| Root `.dockerignore` | `COPY . .` previously included ignored seed/environment/credential files in build context; Git ignore does not protect Docker context |
| Root README and decision docs | Replace stale playground/test-button instructions with actual architecture, direction, and constraints |

Retiring the test consumers does not delete old PostgreSQL queue/subscription records or tool-permission override rows. They are no longer used by this code. Inspect and retire that obsolete topology during a controlled deployment; do not reset production transport to remove demo queues. Keep real scheduling/approval records intact.

## Source publication work still open

- Run a dedicated secret scanner over all history/branches intended for publication and the final working tree; review findings and rotate any exposed credentials. This review ran only targeted token/private-key pattern searches over tracked content and reachable local history (excluding `.opencode`); those searches found no matches. They are not a full secret audit, and locally unavailable history was not examined.
- Review journals, sample audio/transcripts, logs, screenshots, source comments, personal identifiers, and tool configuration history for material that should remain private. No production data was downloaded during this review.
- Choose a project license and review third-party notices before advertising reuse. No license was invented or added on the maintainer's behalf.
- Add a reproducible synthetic demo and screenshots, then verify the README from a clean checkout. Local config/Compose parity remains roadmap item 1.
- Add CI secret detection and Android build verification. Existing PR CI covers the four backend test projects only.

These publication checks are distinct from readiness to distribute the Android client or expose private API routes publicly. Android credential handling and incomplete actor checks on mobile/approval/scheduling routes remain high-priority product work.

## Validation in this review

- API: 39 tests passed.
- Contracts: 7 tests passed.
- Web: 12 tests passed.
- Worker: 7 tests passed.
- Total: 65 passed, zero failed or skipped. Test commands build their server dependencies; Docker-backed integration tests ran through these suites.
- Android build: blocked by missing Android SDK API 36 (`android-36/android.jar`, XA5207). No claim of mobile runtime/release validation.
- Production snapshot/restore, full Compose startup, provider-switch integration, and Android device flows were not executed.

Existing uncommitted `AgentPlayground.Contracts.Tests.csproj` edits were preserved, and tests include those changes. Pre-existing `.opencode/package.json` and `.opencode/package-lock.json` changes were preserved in the user tooling backup and migrated dependency configuration. See [personal tooling](personal-tooling.md).

Reusable tooling was also removed from the repo: 95 skills now live in the shared user skills directory; OpenCode agents/plugins/Roslyn settings live in global OpenCode configuration. Original files and prior global config were archived, and 278 copied files were hash-verified. No interactive tool discovery claim is made until a fresh session loads them.
