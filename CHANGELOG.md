# Changelog

Each commit must add one new entry under `## [Unreleased]` before committing.

## [Unreleased]

### 2026-10-01 - A notch for Windows that shows Claude Code's traffic light
- **Developer label:** "we want something that gives a notch base for us to work on windows, so that can create a kannu for windows"
- **Agent label:** Windows notch base: status core, Claude Code hook, WPF notch and tray
- **Changes:**
  - `Kannu.Core`: the macOS status-file contract in C# — Claude hook event to raw state mapping,
    the 2 s same-group race merge with the PermissionRequest carry, the age ladder that dims stale
    lights, atomic writes under one directory lock, hostile-input caps, and the settings.json
    install/remove transform that keeps the user's own hooks.
  - `kannu-hook.exe`: Native AOT hook that reads the event from stdin and updates the session's
    status file; prints nothing and always exits 0.
  - `Kannu.exe`: a non-activating, topmost WPF pill docked top-centre. Collapsed it shows a dot per
    session and a summary; hover opens the session list with a short ease-out (snaps when Windows
    animation effects are off). Tray icon carries the most urgent light and the install/remove menu.
  - Status directory watched with a debounced FileSystemWatcher; a 5 s tick re-resolves cached
    records so stale lights dim without rescanning.
  - 80 xUnit tests for Core, CI on windows-latest with a published-hook smoke test.
