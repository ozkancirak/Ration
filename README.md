<p align="center">
  <img src="docs/brand/icon.svg" width="112" alt="Ration icon">
</p>

<h1 align="center">Ration</h1>

<p align="center">AI coding quotas in your Windows tray.</p>

<p align="center"><b>English</b> · <a href="README.tr.md">Türkçe</a></p>

<p align="center">
  <a href="https://github.com/ozkancirak/Ration/releases/latest"><img src="https://img.shields.io/github/v/release/ozkancirak/Ration?logo=github&style=flat" alt="Release"></a>
  <a href="https://github.com/ozkancirak/Ration/actions/workflows/ci.yml"><img src="https://img.shields.io/github/actions/workflow/status/ozkancirak/Ration/ci.yml?branch=master&logo=githubactions&logoColor=white&style=flat" alt="CI"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?style=flat" alt="Windows 10 and 11">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/ozkancirak/Ration?style=flat" alt="License"></a>
</p>


<p align="center">
  <img src="docs/screenshots/flyout-en.png" width="380" alt="Ration flyout">
</p>

## Features

- Session and weekly quota left, with reset times and a pace hint
- Tray icon that fills with your remaining quota
- Token and cost totals for the last 30 days from local logs
- Notifications when a quota drops below 20% and when it runs out
- Light and dark theme; English and Turkish interface

## Providers

Claude Code · Codex · Antigravity · OpenCode

## Install

Download `Ration-win-x64-Setup.exe` or the portable zip from [Releases](https://github.com/ozkancirak/Ration/releases). Requires Windows 10 1809 or later; designed for Windows 11. Unsigned builds may trigger a SmartScreen warning.

### Verify a download

Every release has a `SHA256SUMS.txt` file listing the SHA-256 hash of each file. Download it next to the installer and compare:

```powershell
Get-FileHash .\Ration-win-x64-Setup.exe -Algorithm SHA256
Select-String Setup .\SHA256SUMS.txt
```

The two hashes must match (PowerShell prints upper case, the file lower case). With a Unix-style shell: `sha256sum -c SHA256SUMS.txt --ignore-missing`. The built-in updater checks its own downloads; the file is for manual downloads.

## Privacy

Credential files are read-only. No telemetry. Quota requests go straight to each provider's own endpoint, and responses are redacted before anything is logged.

## Development

```powershell
dotnet build Ration.slnx
dotnet test tests\Ration.Tests
dotnet run --project src\Ration.Cli -- usage -p all
powershell -ExecutionPolicy Bypass -File scripts\package.ps1
```

Log file: `%LOCALAPPDATA%\Ration\ration.log`.

To try the update path without GitHub, pack two versions with `vpk pack` into one folder, install the older one and start it with `RATION_UPDATE_REPOSITORY` set to that folder.

## License

MIT. Provider marks from [Simple Icons](https://simpleicons.org) (CC0), [lobe-icons](https://github.com/lobehub/lobe-icons) (MIT) and the official OpenAI logo.
