# 0024: Adopt .NET 11 RC1

- Status: Accepted; server validated, Android build blocked by disk space
- Recorded: 2026-09-13
- Decision date: 2026-09-13
- Evidence: maintainer request to adopt the installed .NET 11 RC1 SDK; `global.json`, project targets, central package versions and service Dockerfiles

## Context

The application targeted .NET 10 without an SDK pin. The maintainer installed SDK `11.0.100-rc.1.26425.128` and requested the upgrade. The four server/test project builds were clean with their existing net10.0 targets under that SDK. The initial full solution build was blocked by missing Android workloads.

## Decision

Target net11.0 throughout server, shared, test and helper projects, and net11.0-android for the companion. Pin the exact SDK in `global.json` with prereleases enabled and roll-forward disabled so local development, CI and container builds use the reviewed release. Pin ASP.NET Core and the framework-aligned Extensions packages to `11.0.0-rc.1.26425.128`, and MAUI Controls to `11.0.0-rc.1.26451.6`.

Keep independently versioned Agent Framework, Microsoft.Extensions.AI, OpenTelemetry and other dependencies at their existing versions unless compatibility requires a change. Preserve warnings-as-errors. Do not adopt unrelated preview features or alter authorization, configuration storage or workflow semantics as part of the upgrade.

Use the published `sdk:11.0.100-rc.1` and `aspnet:11.0.0-rc.1` container tags. Copy `global.json` before restore in all service images. CI installs the SDK from the same file. The mobile workload command is documented separately because server builds do not require mobile workloads.

## Alternatives and consequences

Remaining on .NET 10 LTS would reduce prerelease maintenance but would not meet the maintainer's request. An SDK-only upgrade would leave the application on the old runtime. Floating preview SDK/image versions would make later builds change without a reviewed upgrade.

RC1 requires another deliberate SDK/package/image update for RC2 or GA. Deployment hosts must meet .NET 11 hardware requirements. Local Windows and Linux-container validation cannot prove Railway behavior or Android device workflows. No database migration is introduced by the framework retargeting; rollback uses the previous application commit/images.

## Compatibility review

- Blazor already calls `UseAntiforgery`; preserve token validation and verify the synthetic sign-in/form flow. RC1 analyzers required cascading authentication state in components and guarded JS interop. The route view is keyed by authentication state so a changed identity recreates subject-bound content; the API client subscribes to state changes and unsubscribes on disposal. Circuit disconnects are handled without swallowing other JS failures.
- The telemetry export allowlist already uses modern HTTP semantic-convention names and removes private attributes/events. Retain it and run the redaction/correlation regressions against the new hosting instrumentation.
- Background-service failures under StopHost now fault RunAsync/StopAsync rather than reporting successful shutdown. Preserve that failure signal; existing reconciliation/outbox loops retain their bounded retry handling.
- No direct EF Core, OpenAPI object model, compression/archive, DSA or custom pipe usage was found in application source for the reviewed changes. Existing Android minimum device API is already 24.
- Do not infer framework-session durability, production recovery or mobile identity readiness from a successful runtime upgrade.

## Delivery and verification

Server validation passed: 23 Contracts, 218 API, 56 Web and 30 Worker tests (327 total), including new identity-change/sign-out and circuit-disconnect regressions. Both helper projects build, as do all three Linux Release service images. Synthetic checks passed: 30 application/audio checks, 13 integration-settings checks with Worker restart, and 14 notification checks. Browser checks verified owner sign-in, first-send chat persistence/reply, audio seek/playback and sign-out followed by coach transcript access without owner audio controls. Android build validation is blocked by local disk capacity. The RC1 workload installer first failed on an Emscripten MSI cache payload; the cache-free retry consumed the remaining space while updating existing workloads. Installation was interrupted, and a subsequent project restore failed with an explicit insufficient-space error. Two incomplete Android runtime NuGet cache folders were removed. Workload installation must be completed after freeing space; no full solution/Android build or device deployment is claimed. Keep local binary logs and synthetic output in ignored `.artifacts`, with no private data or credentials in Git.

## Sources

- [RC1 release and package inventory](https://github.com/dotnet/core/blob/main/release-notes/11.0/preview/rc1/11.0.0-rc.1.md)
- [.NET 11 compatibility index](https://learn.microsoft.com/en-us/dotnet/core/compatibility/11)
- [ASP.NET Core 11 compatibility index](https://learn.microsoft.com/en-us/aspnet/core/breaking-changes/11/overview)
- [Background-service failure propagation](https://learn.microsoft.com/en-us/dotnet/core/compatibility/extensions/11/ihost-runasync-stopasync-throw-backgroundservice-failure)
- [Blazor antiforgery middleware](https://learn.microsoft.com/en-us/aspnet/core/breaking-changes/11/blazor-server-side-rendering-deferred-cross-site-request-forgery-protection)
- [Hosting HTTP telemetry changes](https://learn.microsoft.com/en-us/aspnet/core/breaking-changes/11/http-activity-otel-semconv)
