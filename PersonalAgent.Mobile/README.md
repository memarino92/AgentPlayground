# PersonalAgent.Mobile

Android companion app for PersonalAgent focused on agent approval review.

## Build prerequisites

Use SDK `11.0.100-rc.1.26425.128` from the repository's `global.json` and install the matching RC1 Android workload from the repository root:

```powershell
dotnet workload install maui-android --version 11.0.100-rc.1.26460.1
```

The Android workload requires JDK 21 (`>=21.0` and `<22.0`); JDK 11 from older Visual Studio installations will fail. Use Microsoft OpenJDK. The installed workload's `WorkloadDependencies.json` is the source for matching Java and Android SDK versions.

Install the project-specific dependencies from PowerShell:

```powershell
dotnet build PersonalAgent.Mobile/PersonalAgent.Mobile.csproj `
  -t:InstallAndroidDependencies -f net11.0-android `
  "-p:AndroidSdkDirectory=$env:LOCALAPPDATA\Android\Sdk" `
  "-p:JavaSdkDirectory=$env:LOCALAPPDATA\Microsoft\jdk-21" `
  -p:AcceptAndroidSdkLicenses=True
```

For a user-local JDK installation, set the user environment variable `JavaSdkDirectory` to that directory and `AndroidSdkDirectory` to the SDK directory. Set `ANDROID_HOME` / `ANDROID_SDK_ROOT` for Android command-line tools, and add the JDK `bin`, SDK `platform-tools`, `emulator`, and `cmdline-tools/latest/bin` directories to your user PATH. Restart your terminal and IDE after changing user environment variables. `JAVA_HOME` is not required.

Local validation on 2026-10-02: the pinned .NET 11 RC1 Android Debug build succeeded with zero warnings and errors and produced `bin/Debug/net11.0-android/com.personalagent.mobile-Signed.apk`. Microsoft OpenJDK 21.0.8 was installed, Android platform/emulator tools were updated, and the `PersonalAgent_API_36_1` emulator was created with Windows hypervisor acceleration available. After memory-heavy WSL/editor processes were closed, the temporary Java heap override was removed and the build passed with the default heap. The emulator/app was not launched and device flows remain unverified.

MAUI Controls is centrally pinned to `11.0.0-rc.1.26451.6`. Keep the workload and package release aligned. Android SDK and Java are also required; Android API 24 remains the minimum supported device version. This upgrade does not change the existing mobile identity/release limitations in the roadmap.

## Current Scope

- Loads the existing `PersonalAgent.Web` experience in a native `WebView`.
- Registers this device's push token with `PersonalAgent` API.
- Handles incoming approval notifications and submits approve/deny decisions.

The app opens `https://pa.michaelmarino.dev` automatically in the full-height WebView. Sign in using the website's existing social-login flow; the website owns account identity and its login cookies remain in the WebView. Connection fields are under Settings. The default native API URL is `https://pa-api.michaelmarino.dev`. Website sign-in does not yet enroll the native push client; native approval registration still uses the connection settings described below.

URLs are compiled into the APK through assembly metadata. Override them at build time with MSBuild properties `MobileWebBaseUrl` / `MobileApiBaseUrl`, or set `PERSONAL_AGENT_WEB_BASE_URL` / `PERSONAL_AGENT_API_BASE_URL` in the build shell. Those environment values are consumed while building; phone runtime environment variables are not needed. Saved in-app settings take precedence. Internal API credentials are not baked into this URL configuration.

2026-10-03 verification: the personal-instance build passed with zero warnings/errors, was installed on the connected Pixel, and its app-only navigation log confirmed the initial website load succeeded. GitHub account sign-in remains a user interaction; no login credentials or phone screenshots were captured.

## Environment Variables

The URL variables above work at build time. The following legacy settings are read from the app process environment if available; host-shell values for these are not automatically transferred to the phone:

```text
PERSONAL_AGENT_API_BASE_URL
PERSONAL_AGENT_WEB_BASE_URL
INTERNAL_API_KEY
PERSONAL_AGENT_PROFILE_ID
ANDROID_PUSH_CHANNEL_ID
```

You can override `PERSONAL_AGENT_PROFILE_ID` directly inside the app using the
`Profile id` field and `Save Profile` button. The value is persisted in device
preferences and used for token registration + approvals, so you do not need to
hardcode your username in source.

You can also set `API base URL`, `Web URL`, and `Internal API key` in-app with
`Save Connection`. These values are persisted in device preferences and override
environment defaults.

Recommended Android emulator defaults for local dev:

```text
PERSONAL_AGENT_API_BASE_URL=http://127.0.0.1:5100
PERSONAL_AGENT_WEB_BASE_URL=http://127.0.0.1:5100
INTERNAL_API_KEY=dev-internal-api-key
PERSONAL_AGENT_PROFILE_ID=mobile-dev
ANDROID_PUSH_CHANNEL_ID=agent-approval-high
```

For physical devices, use your host machine LAN IP, for example:

```text
PERSONAL_AGENT_API_BASE_URL=http://192.168.1.10:5100
PERSONAL_AGENT_WEB_BASE_URL=http://192.168.1.10:5100
INTERNAL_API_KEY=dev-internal-api-key
```

### Docker backend + USB-connected phone (recommended)

If `PersonalAgent` runs in Docker on your dev machine, this is the fastest setup:

1. Start backend containers:

```bash
OPENAI_API_KEY=your-key docker compose up -d postgres personalagent-api
```

2. Create USB reverse tunnel so phone `127.0.0.1:5100` maps to host `localhost:5100`:

```bash
adb reverse tcp:5100 tcp:5100
adb reverse --list
```

3. Keep mobile app defaults:

```text
PERSONAL_AGENT_API_BASE_URL=http://127.0.0.1:5100
PERSONAL_AGENT_WEB_BASE_URL=http://127.0.0.1:5100
```

This avoids LAN firewall issues and works consistently for local testing.

Android 9+ blocks plain HTTP by default. This app explicitly enables cleartext
traffic in `Platforms/Android/AndroidManifest.xml` for local `http://127.0.0.1`
development paths (USB reverse and LAN testing).

## Firebase Setup

1. Keep `google-services.json` at repository root (it is gitignored).
2. The project auto-links it into Android builds when the file exists.
3. Ensure Firebase Cloud Messaging is enabled for your Android app in Firebase console.

This app now integrates `Plugin.Firebase.CloudMessaging` + `Plugin.Firebase.Core`
for token and message callbacks.

Current implementation status:

- Registration requires a real Firebase token; old placeholder tokens are ignored. Token renewal re-registers the device, as do saved profile/connection changes.
- Android creates a high-importance approval channel and forwards cold/warm notification tap intents to the Firebase plugin.
- Approval payloads include type, profile, expiry, and review details. Multiple requests queue independently, duplicate receipt/tap events are deduplicated, and requests are reviewed only for the selected profile while the app is active.
- Before offering Approve/Deny, the app fetches the current server record. Expired/already-decided requests are dismissed; the database also rejects expired decisions atomically.
- Choose Later to retain the request. Failed or uncertain submissions show an error and retain the request for refresh/retry via Approve Pending. Requests are queued in memory; after process death, a notification tap restores that request, but there is no persistent inbox/recovery listing yet.

The approval APIs still use the existing shared internal API key. Trusted device enrollment/revocation (TRUST-06), automatic tool approval enforcement (TRUST-02), and durable agent continuation (DEBT-03) remain backlog work. An approval decision records and publishes the decision; it does not itself resume a paused agent operation.

Validation on 2026-10-02: 20 mobile routing/token tests and four targeted API tests passed, including a real PostgreSQL check of expiry, profile scoping, and duplicate decisions. The Android Debug build passed with zero warnings/errors and the default Java heap. Deploy the updated API and client together: older approval payloads without the type/profile/expiry fields are ignored by the new client. Firebase delivery, notification permissions, and device lifecycle behavior still require the live checklist below.

Current package graph now restores/builds without `NU1608` suppression.

## Build

```bash
dotnet build PersonalAgent.Mobile/PersonalAgent.Mobile.csproj \
  -p:AndroidSdkDirectory=$ANDROID_SDK_ROOT \
  -p:JavaSdkDirectory=$JAVA_HOME
```

If your shell already exports `ANDROID_SDK_ROOT` and `JAVA_HOME`, these MSBuild
properties are auto-detected from the project file.

For reliable on-device debug installs from CLI, this project enables embedded
assemblies in Debug (`EmbedAssembliesIntoApk=true`) to avoid fast-deploy
assembly lookup crashes on physical devices.

## Publish APK

```bash
dotnet publish PersonalAgent.Mobile/PersonalAgent.Mobile.csproj \
  -f net11.0-android -c Debug \
  -p:AndroidSdkDirectory=$ANDROID_SDK_ROOT \
  -p:JavaSdkDirectory=$JAVA_HOME
```

Generated APK path:

`PersonalAgent.Mobile/bin/Debug/net11.0-android/publish/com.personalagent.mobile-Signed.apk`

Install on an attached emulator/device:

```bash
adb install -r PersonalAgent.Mobile/bin/Debug/net11.0-android/publish/com.personalagent.mobile-Signed.apk
```

## Reviewer Checklist

For live delivery, provide the gitignored `google-services.json` for `com.personalagent.mobile`, configure API `PushNotifications` settings and Firebase service credentials through the existing encrypted settings flow, and allow Android notifications. Rebuild/install the client after adding Firebase configuration. This checkout's automated tests do not establish Firebase delivery.

Exercise foreground receipt, a notification tap while backgrounded, and a tap after terminating the app. Review multiple requests for the same profile, choose Later, then reopen Approve Pending. Verify a different profile cannot review the request, an expired/already-decided request cannot be submitted, and offline submission displays an error without claiming success. Reconnect and refresh before retrying an uncertain decision.

Platform-independent mobile tests run on desktop/CI:

```powershell
dotnet test PersonalAgent.Mobile.Tests/PersonalAgent.Mobile.Tests.csproj
```

1. Start backend + push infra:

```bash
docker compose up -d postgres personalagent-api
```

2. Bridge USB device to local API:

```bash
adb reverse tcp:5100 tcp:5100
```

3. Open app, set your profile id, tap `Save Profile`, then tap `Register Device`.
4. From web chat, ask agent to send a mobile notification to your profile id.
5. Verify notification appears on phone lock screen/notification tray.
