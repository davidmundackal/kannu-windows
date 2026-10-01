# Kannu for Windows

**Watch your agents.** Kannu sits in a small notch at the top of your screen and shows what your AI
coding agents are doing, so you don't have to switch back to the terminal to find out.

- 🟢 Green: the agent is working
- 🟡 Yellow: the agent needs you
- 🔴 Red: the agent finished or stopped

Hover the notch to open it and see every session. It never takes focus from your editor.

This is the Windows companion to [Kannu for macOS](https://github.com/libinmv/kannu). Both read the
same status files ([docs/STATUS_CONTRACT.md](docs/STATUS_CONTRACT.md)).

## Status

Early. Supported today: **Claude Code** on Windows 10 and 11.

## Build and run

Requirements: Windows 10/11, the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
./scripts/publish.ps1          # builds Kannu.exe + kannu-hook.exe into out/win-x64
./out/win-x64/Kannu.exe
```

Then right-click the tray dot and choose **Install Claude Code hooks**. New Claude Code sessions show
up on the notch.

What installing does:

- copies `kannu-hook.exe` to `%LOCALAPPDATA%\Kannu\bin\`
- backs up `%USERPROFILE%\.claude\settings.json` next to itself (`settings.json.kannu-backup-<time>`)
- adds Kannu's hook entries to it, keeping all of your own hooks

**Remove Claude Code hooks** takes out exactly those entries again.

## Layout

| Path                   | What                                                                     |
|------------------------|--------------------------------------------------------------------------|
| `src/Kannu.Core`       | Platform-neutral logic: event mapping, race merge, age ladder, status files, settings transform |
| `src/Kannu.Hook`       | `kannu-hook.exe`, run by the agent on each event (Native AOT)            |
| `src/Kannu.App`        | The WPF notch, tray icon and status watcher                              |
| `tests/Kannu.Core.Tests` | xUnit tests for Core                                                   |

```powershell
dotnet test tests/Kannu.Core.Tests
```

## License

GPL-3.0-or-later. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
