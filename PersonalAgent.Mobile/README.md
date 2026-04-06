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
```

Recommended Android emulator defaults for local dev:

```text
PERSONAL_AGENT_API_BASE_URL=https://10.0.2.2:5001
PERSONAL_AGENT_WEB_BASE_URL=https://10.0.2.2:5000
PERSONAL_AGENT_PROFILE_ID=mobile-dev
ANDROID_PUSH_CHANNEL_ID=agent-approval-high
```

## Firebase Setup

1. Keep `google-services.json` at repository root (it is gitignored).
2. The project auto-links it into Android builds when the file exists.
3. Firebase native token callbacks are not wired yet in this build; the app uses a placeholder token to exercise backend registration and 2FA flow.

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
