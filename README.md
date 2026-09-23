<p align="center">
  <img src="docs/brand/icon.svg" width="112" alt="Ration icon">
</p>

<h1 align="center">Ration</h1>

<p align="center">AI quota in your Windows tray</p>

<p align="center"><b>English</b> · <a href="README.tr.md">Türkçe</a></p>

Ration is a native Windows 11 tray app that shows the quota usage, reset times and local token/cost summaries of your AI coding providers.

![Ration flyout](docs/screenshots/flyout.png)

The tray icon fills with your remaining quota: [icon matrix](docs/screenshots/tray-contact-sheet.png).

## Supported providers

- Claude Code
- Codex CLI
- Antigravity
- OpenCode

Ration opens these providers' local credential files read-only. It never refreshes tokens or writes to the source files.

## Language

The app is available in English and Turkish. It follows the Windows display language by default; you can change it under **Settings → Language**.

## Installation

Run `Ration-win-x64-Setup.exe` from the release. For portable use, extract `Ration-win-x64-Portable.zip` and run `Ration.exe`.

The package bundles a self-contained Windows App SDK, so a clean machine does not need a separate Windows App SDK runtime. If the release is unsigned, Windows SmartScreen may warn you; this does not mean a runtime or certificate needs to be installed.

## Privacy

Ration sends no telemetry and no user credentials anywhere; credential files are read-only. Requests needed for the quota display go directly to each provider's official endpoint. Raw responses are redacted before they are written to the diagnostic log.

## Updates

The **Check for updates** button in Settings checks the GitHub Releases feed. Ration never downloads automatically; you decide when a new version is found.

The default feed is `https://github.com/ozkancirak/Ration`. If releases live elsewhere, override it with the `RATION_UPDATE_REPOSITORY` environment variable.

## Development

```powershell
dotnet build Ration.slnx -c Debug
dotnet test tests\Ration.Tests\Ration.Tests.csproj
dotnet run --project src\Ration.Cli -- --selftest --screenshot-dir artifacts\selftest
powershell -ExecutionPolicy Bypass -File scripts\package.ps1 -Configuration Release
```

Packaging produces the Setup.exe and portable zip under `artifacts\release\releases`. The diagnostic log is at `%LOCALAPPDATA%\Ration\ration.log`; the **Open log** button in Settings opens it.

Provider marks are based on Simple Icons (CC0); thank you.

On first launch, the previous product's settings files (`theme.json`, `refresh.json`, and `settings.json` if present) are migrated only if the new data directory does not exist yet. Quota snapshots and the pricing cache are rebuilt. The old startup entry is removed; if enabled, it is moved to the fixed `%LOCALAPPDATA%\Ration\Ration.exe` path.

The CLI build output is at `src\Ration.Cli\bin\Debug\net10.0-windows10.0.19041.0\ration.exe`. Examples: `ration usage -p claude --json` and `ration --log`. The app and CLI use separate output directories.
