# Kannu for Windows

**Watch your agents.** Kannu sits in a small notch at the top of your screen and shows what your AI
coding agents are doing, so you don't have to switch back to the terminal to find out.

- 🟢 Green: the agent is working
- 🟡 Yellow: the agent needs you
- 🔴 Red: the agent finished or stopped

The notch stays out of sight until something happens: when an agent starts, needs you or finishes,
it slides down from the top of the screen for a few seconds, then hides again. Click the Kannu eye in
the taskbar to open it any time; while it is on screen, hover it to see every session. It never takes
focus from your editor. Settings (the gear in the open notch, or right-click the eye) has the
Floating pill shape, an always-visible mode, and an opt-in reveal by resting the pointer at the top
of the screen.

This is the Windows companion to [Kannu for macOS](https://github.com/libinmv/kannu). Both read the
same status files ([docs/STATUS_CONTRACT.md](docs/STATUS_CONTRACT.md)).

## Status

Early. Windows 10 and 11. Hooks for **Claude Code, Cursor, VS Code Copilot and Copilot CLI, Codex CLI,
Antigravity, Gemini CLI, Qwen Code and opencode**.

## Install

Beta testers: start with the [beta guide](docs/BETA.md).

1. Download **`Kannu-win-Setup.exe`** from the latest
   [release](https://github.com/davidmundackal/kannu-windows/releases/latest).
2. Run it. Releases are not code-signed yet, so the first time Windows SmartScreen says *"Windows
   protected your PC"*: click **More info**, then **Run anyway**.
3. Kannu installs for your user only (no administrator prompt) into `%LOCALAPPDATA%\Kannu`, adds a
   Start-menu entry, installs the .NET 8 Desktop Runtime if it is missing, and starts. The eye appears
   in the notification area; the notch stays hidden until an agent does something.
4. Accept the Terms of Use, then open **Settings › Agents** (right-click the eye) and install the
   hook for each agent you use.

Updates install themselves: Kannu checks its GitHub Releases at launch and once a day, downloads in the
background and applies the update the next time it starts (or right away from **Restart to Update** in
the tray menu). **Check for Updates…** in the tray menu checks now.

To uninstall, use **Settings › Apps › Installed apps › Kannu**. Uninstalling also removes Kannu's
entries from every agent's settings.

No installer wanted? `Kannu-win-Portable.zip` on the same release page runs from any folder.

## Build and run

Requirements: Windows 10/11, the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
./scripts/publish.ps1          # builds Kannu.exe + kannu-hook.exe into out/win-x64
./out/win-x64/Kannu.exe
./scripts/build-installer.ps1  # the installer, unsigned, into Releases/ (needs vpk; see the script)
```

A build run from `out/` does not update itself; only an installed copy does. Releases are cut from tags:
[scripts/RELEASE.md](scripts/RELEASE.md).

Then accept the Terms of Use and install each agent's hook from **Settings › Agents** (right-click the
tray eye; **Agent hooks** in that menu does the same). New sessions show up
on the notch.

What installing does:

- copies `kannu-hook.exe` to `%LOCALAPPDATA%\Kannu\bin\`
- adds Kannu's hook entries to that agent's settings (for example `%USERPROFILE%\.claude\settings.json`),
  keeping all of your own hooks, and keeps the previous file as `<name>.kannu-backup`
- refuses, changing nothing, if the file is not plain JSON (comments, trailing commas)

Unticking the agent takes out exactly those entries again.

## Layout

| Path                   | What                                                                     |
|------------------------|--------------------------------------------------------------------------|
| `src/Kannu.Core`       | Platform-neutral logic: event mapping, race merge, age ladder, status files, settings transform |
| `src/Kannu.Detection`  | Passive detection that reads other apps' SQLite databases (Cursor)        |
| `src/Kannu.Hook`       | `kannu-hook.exe`, run by the agent on each event (Native AOT)            |
| `src/Kannu.App`        | The WPF notch, tray icon and status watcher                              |
| `tests/Kannu.Core.Tests` | xUnit tests for Core                                                   |

```powershell
dotnet test tests/Kannu.Core.Tests
```

## License

GPL-3.0-or-later. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
