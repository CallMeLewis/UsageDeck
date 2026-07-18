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
- Configurable Windows notifications for limit thresholds and resets, Codex reset credits, provider incidents, sign-in requirements, repeated refresh failures, and recoveries. Settings also reports Windows delivery status and can send a test notification.
- Automatic refresh every 1, 5, 15, or 30 minutes, with manual refresh at any time.
- System, light, and dark themes with optional Mica.
- Settings stored per Windows user under `%LOCALAPPDATA%\UsageDeck`.
- UsageDeck-branded application, installer, executable, and update packages.
- Built-in updates through versioned Velopack releases.

## Supported providers

| Provider | Source |
| --- | --- |
| Codex | Installed Codex CLI app server |
| Claude Code | Authenticated `/usage` view through an isolated terminal session |
| Antigravity | `agy` CLI |
| GitHub Copilot | Authenticated GitHub CLI (`gh`) |
| Kiro | `kiro-cli` |
| Amp | `amp` CLI |
| OpenCode Go | OpenCode Console API billing export or read-only local `opencode.db` history |
| Z.AI | Personal Coding Plan quota API |

## Install

UsageDeck requires **Windows 11 24H2 or later on x64**.

1. Open the [latest release](https://github.com/CallMeLewis/UsageDeck/releases/latest).
2. Download the Windows Setup executable, or choose the portable ZIP if you do not want an installed copy.
3. Start UsageDeck and enable the providers you use in Settings.

The release includes the .NET and Windows App SDK runtimes. Current development builds are unsigned, so Windows may show an unknown-publisher or SmartScreen warning.

Provider-owned CLIs must already be installed and signed in. OpenCode Go can instead use an OpenCode Console service-account key (`oc_sk_…`) under **Settings → Providers → OpenCode Go**; ordinary Go or Zen inference keys cannot access the billing export. Z.AI does not require a CLI; add its API key under **Settings → Providers → Z.AI** using Windows Credential Manager, the `Z_AI_API_KEY` environment variable, or session-only storage.

## Privacy

Most usage collection happens locally through provider-owned tools. UsageDeck does not log tokens, cookies, raw provider responses, or captured terminal output.

- Codex, Claude, Antigravity, Copilot, Kiro, and Amp keep authentication under their own tools.
- OpenCode Go sends a configured service-account key only to `https://console.opencode.ai/api/v1/usage/export`. Without one, it reads `opencode.db` locally and never reads `auth.json` or browser cookies. Keys can be held in Windows Credential Manager, `OPENCODE_CONSOLE_SERVICE_API_KEY`, or session memory and are never written to the settings file.
- Z.AI sends its key only to the fixed endpoint for the selected region and never writes it to the settings file.
- Service-status checks use public official endpoints and do not send provider credentials. Providers without a verified public source are labelled unavailable rather than inferred to be operational.

## Development

Install the .NET 10 SDK, then run:

```powershell
dotnet restore src/UsageDeck.App/UsageDeck.App.csproj -r win-x64
dotnet build src/UsageDeck.App/UsageDeck.App.csproj -c Debug --no-restore
dotnet test UsageDeck.slnx -c Debug -p:SkipReleaseArtifacts=true
& src/UsageDeck.App/bin/Debug/net10.0-windows10.0.26100.0/win-x64/UsageDeck.App.exe
```

Visual Studio users can open `UsageDeck.slnx` and select the shared **UsageDeck** launch profile. The legacy package ID and local-data identities are retained so existing installations can update without losing their settings or saved credentials; upgraded installations remove the obsolete executable launcher.

## Releases

`Directory.Build.props` contains the release version. After a version change reaches `main` and CI passes, push the matching tag:

```powershell
git tag -a v0.1.1 -m "UsageDeck 0.1.1"
git push origin v0.1.1
```

The Release workflow verifies the version and successful CI run, builds the Velopack packages, and publishes the installer, portable ZIP, update package, and release feed automatically.

Beta releases use a SemVer pre-release suffix. For example, set the version to `0.4.0-beta.1`, then push the matching `v0.4.0-beta.1` tag. The workflow marks suffixed versions as GitHub pre-releases automatically. Stable clients ignore them, while clients on the Beta update channel consider both stable and pre-release builds.

For local packaging:

```powershell
.\tools\Publish-Release.ps1 -RepositoryUrl https://github.com/CallMeLewis/UsageDeck
```
