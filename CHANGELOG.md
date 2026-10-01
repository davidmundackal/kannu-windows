# Changelog

Each commit must add one new entry under `## [Unreleased]` before committing.

## [Unreleased]

### 2026-10-01 - Every agent macOS Kannu watches now reports to the Windows notch
- **Developer label:** "go" (Phase 1 of the parity plan: every agent provider and the full hook port)
- **Agent label:** Hook parity with macOS script v43, installers for all eight agents from one layout table
- **Changes:**
  - `kannu-hook.exe` takes the macOS script's arguments (`<state> <provider> [event] [matcher_key]`)
    and ports its whole status path: every agent's event map (Claude, Cursor, VS Code/Copilot CLI,
    Codex, Antigravity, Gemini CLI, Qwen Code, opencode), the Copilot CLI vs VS Code split, subagent
    `parent_id`, chat titles, `tool_errors`, sticky `unattended`, `ended_on_error`, Antigravity's
    `quota_exceeded`, Cursor's sticky yellow, and turn metrics (run time, tool calls, transcript offset).
  - The hook now prints the line each agent expects (allow JSON, `{}` for Gemini/Qwen/Copilot CLI,
    nothing for Codex), also when it fails. Previously it printed nothing.
  - Malformed or oversized input still moves the light by the installer's state, as on macOS.
  - Status files are read field by field: a wrong-typed field is dropped, not the whole record.
  - `AgentHookLayout` + `AgentHookInstaller` replace the Claude-only installer: one table of files,
    shapes and events per agent drives install, uninstall and the installed check (macOS REGRESSIONS
    entry 6). Settings with comments or trailing commas are refused, not rewritten. Codex gets
    `features.hooks = true` with a minimal TOML edit. opencode gets Kannu's plugin.
  - Tray menu: **Agent hooks** submenu with one checkable item per agent; agents not found on this
    machine are disabled.
  - The solution file now includes the app project.
  - 141 tests (was 80), including every installer round trip and the turn rules.

### 2026-10-01 - The tray shows Kannu's eye, coloured by the most urgent agent
- **Developer label:** "actually use the eye icon we use on the top bar"
- **Agent label:** Kannu branding on Windows: eye tray icon, app icon, shared palette
- **Changes:**
  - Tray icon is the eye macOS Kannu shows in the menu bar. SF Symbols are licensed for Apple
    platforms only, so it is drawn from Windows' own `View` glyph (Segoe Fluent Icons, falling back
    to Segoe MDL2 Assets). White or black to match the taskbar theme when idle; green, yellow or red
    for the most urgent session. Re-rendered on theme or display-scale changes.
  - Tray tooltip carries the notch summary ("Kannu · 1 needs you · 2 working").
  - `Kannu.exe` carries the Kannu app icon: `assets/Kannu.ico` (16–256 px) built by
    `scripts/make-icons.py` from macOS Kannu's AppIcon (JPEG data, re-encoded as PNG frames).
  - Traffic-light colours now match macOS Kannu (`AgentTrafficLightColors.swift`), shared by the
    notch and the tray via `Brand.cs`.
  - The collapsed notch shows the eye while no agent is running.

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
