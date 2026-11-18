# Sentinel

**A local AI account, credit, and usage dashboard for Windows and Android.**

Sentinel brings supported provider reports, local coding-session records, and optional Ollama measurements into one app. It runs locally: provider credentials and activity records are stored on the device, and Sentinel does not operate a remote account server or collect telemetry.

## Downloads

The repository includes the current v1.1.0 builds:

- **Windows:** [`Sentinel.exe`](Sentinel.exe) — self-contained x64 desktop application. Microsoft Edge WebView2 Evergreen Runtime is required.
- **Android:** [`Sentinel.apk`](Sentinel.apk) — install on Android 8.0 (API 26) or newer. Android may ask you to allow installation from your file manager or browser.

The Android APK is signed with a local release key. Its signature is not a public developer identity; future builds can update this APK only if they are signed with the same key. The signing key is intentionally not included in this repository. The Windows build is unsigned.

See [`Sentinel-validation.json`](Sentinel-validation.json) for the recorded validation results and artifact SHA-256 hashes. Verify a downloaded file before use:

```powershell
Get-FileHash .\Sentinel.exe -Algorithm SHA256
Get-FileHash .\Sentinel.apk -Algorithm SHA256
```

## Features

- Overview, Accounts, Activity, Catalog, and Settings views, with light and dark themes.
- Provider-reported balances and usage, with unknown or stale values kept distinct from zero.
- Local Codex and Claude Code token activity where supported local records are available.
- Optional Ollama model status and request metering through Sentinel's local proxy.
- Encrypted local credential storage using Windows DPAPI or Android Keystore-backed encryption.
- OpenRouter authorization flows; other provider reports require provider credentials with appropriate access.

Provider coverage and permissions vary. A dashboard login alone does not grant billing access. Consult [`docs/PROVIDERS.md`](docs/PROVIDERS.md) and the in-app provider details. Sentinel is a reporting tool; it does not move money or represent a transferable balance. Usage reports are provider-reported snapshots and should not be added together across repeated refreshes.

## Build from source

### Windows

Requirements: Windows, .NET 8 SDK, and Microsoft Edge WebView2 Evergreen Runtime to run the app.

```powershell
.\build-windows.ps1
```

The self-contained x64 output is written to `windows/publish/Sentinel.exe`.

### Android

Requirements: JDK, Python 3, Android SDK platform 36, and Android build-tools 36.0.0. The build script expects the SDK at `%LOCALAPPDATA%\Android\Sdk` by default; pass `-SdkRoot` to use another location.

```powershell
.\build-android.ps1
```

The script writes build intermediates under `android/build`. It creates a local signing key under `android/signing` if one is not present. **Back up and protect that key and its password securely.** They are ignored by Git and must never be committed. Without the original key, a later APK cannot update an installation made from the current signed APK.

## Validation

The checked-in [`Sentinel-validation.json`](Sentinel-validation.json) records the release validation results and hashes for the published artifacts. Test sources are in `tests/`; run them in the environment appropriate for each suite. Building from source is separate from the recorded validation of the included binaries.

## Privacy and security

- Never commit API keys, signing keys, passwords, local vaults, or account/session data.
- Windows protects the local vault with current-user DPAPI; Android uses Android Keystore-backed encryption and disables app backup.
- Sentinel does not intentionally collect prompts or model responses for its local usage reporting. See the app's provider descriptions for the exact data each feature reads.
- Only enter provider credentials into Sentinel if you trust the device and understand the permissions granted to those credentials.

Report security vulnerabilities privately to the repository owner rather than posting credentials or exploit details in a public issue.

## License

Sentinel is licensed under the MIT License; see [`LICENSE`](LICENSE).
