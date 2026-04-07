# PersonalAgent.Mobile

Android companion app for PersonalAgent focused on secure 2FA approvals.

## Current Scope

- Loads the existing `PersonalAgent.Web` experience in a native `WebView`.
- Registers this device's push token with `PersonalAgent` API.
- Handles incoming approval notifications and submits approve/deny decisions.

## Environment Variables

Set these before launching the app (or in your run profile):

```text
PERSONAL_AGENT_API_BASE_URL
PERSONAL_AGENT_WEB_BASE_URL
INTERNAL_API_KEY
PERSONAL_AGENT_PROFILE_ID
ANDROID_PUSH_CHANNEL_ID
ENABLE_LOCAL_APPROVAL_SHORTCUT
```

Recommended Android emulator defaults for local dev:

```text
PERSONAL_AGENT_API_BASE_URL=http://127.0.0.1:5100
PERSONAL_AGENT_WEB_BASE_URL=http://127.0.0.1:5100
INTERNAL_API_KEY=dev-internal-api-key
PERSONAL_AGENT_PROFILE_ID=mobile-dev
ANDROID_PUSH_CHANNEL_ID=agent-approval-high
ENABLE_LOCAL_APPROVAL_SHORTCUT=false
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

- Device registration uses Firebase token when available, with placeholder token as fallback for local dev.
- Approval prompt routing is driven by Firebase data messages by default.
- Incoming Firebase data payloads are bridged into the native approval prompt routing.

Set `ENABLE_LOCAL_APPROVAL_SHORTCUT=true` only when you want the Request 2FA
button to inject a local pending request without waiting for push delivery.

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
  -f net10.0-android -c Debug \
  -p:AndroidSdkDirectory=$ANDROID_SDK_ROOT \
  -p:JavaSdkDirectory=$JAVA_HOME
```

Generated APK path:

`PersonalAgent.Mobile/bin/Debug/net10.0-android/publish/com.personalagent.mobile-Signed.apk`

Install on an attached emulator/device:

```bash
adb install -r PersonalAgent.Mobile/bin/Debug/net10.0-android/publish/com.personalagent.mobile-Signed.apk
```
