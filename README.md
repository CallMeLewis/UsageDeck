<div align="center">
  <img src="src/UsageDeck.App/Assets/AppIcon.png" width="112" alt="UsageDeck icon">
  <h1>UsageDeck</h1>
  <p>A native Windows tray app for keeping an eye on AI coding usage, limits, and reset times.</p>
  <p>
    <a href="https://github.com/CallMeLewis/UsageDeck/releases/latest"><img src="https://img.shields.io/github/v/release/CallMeLewis/UsageDeck?display_name=tag&amp;sort=semver" alt="Latest release"></a>
    <a href="https://github.com/CallMeLewis/UsageDeck/actions/workflows/ci.yml"><img src="https://github.com/CallMeLewis/UsageDeck/actions/workflows/ci.yml/badge.svg" alt="CI status"></a>
  </p>
</div>

UsageDeck brings usage from several coding assistants into one compact WinUI 3 window. It lives in the notification area, refreshes quietly in the background, and keeps each provider's data separate and easy to scan.

## Highlights

- One compact view for every enabled provider, plus an optional **All** summary.
- Used or remaining quota percentages, reset countdowns or exact local times, freshness, and error states.
- Optional official service-status monitoring for enabled providers, with incident warnings on affected tabs.
- Per-provider Windows notification rules for limit thresholds and resets, Codex reset credits, provider incidents, sign-in requirements, repeated refresh failures, and recoveries. Settings also reports Windows delivery status and can send a test notification.
- Temporary notification pauses from the tray or Settings for 30 minutes, 1 hour, 2 hours, 4 hours, or until the following morning, with an immediate resume action.
- Automatic refresh every 1, 5, 15, or 30 minutes, with manual refresh at any time.
- System, light, and dark themes with optional Mica.
- Settings stored per Windows user in `%LOCALAPPDATA%\UsageDeckData\settings.json`, outside the installer-owned application directory.
- UsageDeck-branded application, installer, executable, and update packages.
- Built-in updates through versioned Velopack releases.
- Optional background start at Windows sign-in, with UsageDeck kept quietly in the notification area.

## Supported providers

| Provider | Source |
| --- | --- |
| Codex | Installed Codex CLI app server |
| Claude Code | Anthropic usage API using Claude CLI credentials, with an authenticated `/usage` terminal fallback |
| Antigravity | Backend quota through the signed-in `agy` CLI |
| GitHub Copilot | Authenticated GitHub CLI (`gh`) |
| Kiro | `kiro-cli` |
| Amp | `amp` CLI |
| Z.AI | Account-wide personal Coding Plan quota API |
| TheClawBay | Official quota API through a configured key, or `theclawbay usage --json` through the signed-in CLI |

## Install

UsageDeck requires **Windows 11 24H2 or later on x64**.

1. Open the [latest release](https://github.com/CallMeLewis/UsageDeck/releases/latest).
2. Download the Windows Setup executable, or choose the portable ZIP if you do not want an installed copy.
3. Start UsageDeck. First-run setup checks this PC for supported providers and lets you choose providers, theme, and notifications before the first refresh.

The release includes the .NET runtime and the Microsoft-signed Windows App SDK packages required by UsageDeck, so users do not need to install .NET separately. The lightweight launcher registers those packages for the current Windows user before starting the app. If the Windows interface cannot start, UsageDeck displays the error and saves a privacy-safe report under `%LOCALAPPDATA%\UsageDeckData\diagnostics`. Current builds are unsigned, so Windows may show an unknown-publisher or SmartScreen warning. Each release also includes `SHA256SUMS.txt` for verifying the downloaded installer or portable ZIP.

The portable ZIP does not install UsageDeck itself, but its first launch registers the same shared Microsoft runtime packages for the current Windows user.

Provider-owned CLIs must already be installed and signed in. Z.AI does not require a CLI; add its API key under **Settings → Providers → Z.AI** using Windows Credential Manager, the `Z_AI_API_KEY` environment variable, or session-only storage.

For TheClawBay, open **Settings → Providers → TheClawBay** and choose **Automatic (recommended)**, **TheClawBay CLI**, or **API key**. Automatic prefers a configured API key and falls back to `theclawbay usage --json` through the signed-in CLI. API keys can be held in Windows Credential Manager, read from the externally managed `THECLAWBAY_API_KEY` environment variable, or kept in memory until UsageDeck exits.

## Privacy

Most usage collection happens locally through provider-owned tools. UsageDeck does not log tokens, cookies, raw provider responses, or captured terminal output.

- Codex, Antigravity, Copilot, Kiro, and Amp keep authentication under their own tools.
- Claude first sends the access token stored by Claude Code only to Anthropic's fixed usage endpoint. If that is unavailable, UsageDeck opens the authenticated `/usage` view in an isolated terminal session. UsageDeck never writes to Claude Code's credential store.
- Z.AI sends its key only to the fixed endpoint for the selected region and never writes it to the settings file.
- A UsageDeck-managed TheClawBay key is sent only to `https://theclawbay.com/api/codex-auth/v1/quota`. CLI mode leaves sign-in under the CLI's ownership, the public status request is unauthenticated, and raw responses are not logged.
- Service-status checks use public official endpoints and do not send provider credentials. Providers without a verified public source are labelled unavailable rather than inferred to be operational.

## Development

Install the .NET 10 SDK, then run:

```powershell
dotnet restore src/UsageDeck.App/UsageDeck.App.csproj -r win-x64
dotnet build src/UsageDeck.App/UsageDeck.App.csproj -c Debug --no-restore
dotnet test UsageDeck.slnx -c Debug -p:SkipReleaseArtifacts=true -p:WindowsAppSdkBootstrapInitialize=false --blame-hang-timeout 2m --nologo
& src/UsageDeck.App/bin/Debug/net10.0-windows10.0.26100.0/win-x64/UsageDeck.App.exe
```

Visual Studio users can open `UsageDeck.slnx` and select the shared **UsageDeck** launch profile. The package ID and credential identity remain unchanged so existing installations keep their update path and saved credentials. On the first launch after upgrading from a build that used `%LOCALAPPDATA%\UsageDeck\settings.json`, UsageDeck copies those settings to the protected `UsageDeckData` location and retains the original as a recovery copy.

## Releases

`Directory.Build.props` contains the release version. Before tagging a release, add its user-facing notes at `.github/release-notes/v<version>.md`, commit the notes with the version change, ensure that commit has reached `main`, and wait for CI to pass. Then push the exact matching tag:

```powershell
$version = ([xml](Get-Content -Raw Directory.Build.props)).Project.PropertyGroup.Version
git tag -a "v$version" -m "UsageDeck $version"
git push origin "v$version"
```

The Release workflow verifies that the tag matches the version, the release notes exist, and the tagged commit is reachable from `main`. It then runs the Release tests, builds the Velopack packages, and publishes the installer, portable ZIP, update package, release feed, and SHA-256 manifest automatically.

Beta releases use a SemVer pre-release suffix. For example, set the version to `0.4.0-beta.1`, then push the matching `v0.4.0-beta.1` tag. The workflow marks suffixed versions as GitHub pre-releases automatically. A fresh pre-release installation starts on the Beta update channel; a previously saved channel remains unchanged. Stable clients ignore pre-releases, while clients on the Beta channel consider both stable and pre-release builds.

For local packaging:

```powershell
.\tools\Publish-Release.ps1 -RepositoryUrl https://github.com/CallMeLewis/UsageDeck
```
