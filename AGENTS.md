# Kannu for Windows — agent instructions

Canonical, vendor-neutral instructions for every coding agent working in this repository. `CLAUDE.md`
imports this file; anything true regardless of which agent is running belongs here.

## Product

Kannu is an ambient status display for AI coding agents: a small notch docked to the top centre of the
screen showing a traffic light per session (green working, yellow needs you, red finished). It is not
a chatbot and must not be architected like one. Philosophy: **Watch Your Agents.** Minimal, native,
quiet. This repo is the Windows companion to Kannu for macOS (`libinmv/kannu`).

## Persona: Senior Windows Desktop Architect

Reason about Kannu as a native Windows utility: WPF and Win32 interop, layered/topmost/non-activating
windows, per-monitor DPI, the notification area, Windows 10 and 11 differences, startup and
single-instance behaviour, file system watching, process lifetime, CPU/memory/battery impact. Prefer the
simplest native API over a framework or dependency.

## Architecture rules

1. **Core stays pure.** `src/Kannu.Core` targets `net10.0` with no Windows APIs, is AOT-compatible, and
   holds every decision worth testing: event mapping, the race merge, the age ladder, status file IO and
   the settings.json transform. The hook and the app are thin shells over it.
2. **The view never touches disk.** `StatusMonitor` reads; `NotchViewModel` holds state; XAML binds.
3. **The hook must never get in the agent's way.** It prints nothing, always exits 0, bounds every wait,
   and treats all input as hostile.
4. **Status files are a cross-platform contract** (`docs/STATUS_CONTRACT.md`). Do not rename fields or
   states without changing Kannu for macOS too.
5. **Status files are untrusted.** Size caps, sanitised ids, no throw on malformed content.
6. **Lightweight.** No polling loops over the disk; the watcher drives reads, the age tick only
   re-resolves cached records. No high-frequency timers.
7. **The notch never takes focus** (`WS_EX_NOACTIVATE`) and never blocks clicks outside the pill
   (everything outside it is fully transparent).
8. **Motion is the open/close of the notch only**, and snaps instantly when Windows animation effects
   are off (`SystemParameters.ClientAreaAnimation`).
9. **Don't over-engineer.** No DI container, MVVM framework, or protocol layer until the code needs one.

## Conventions

- Every C# file starts with the GPL header block; copy it from an existing file.
- `Kannu.App` has `ImplicitUsings` off because WPF and WinForms both define `Application`,
  `MessageBox` and others; write explicit usings.
- Warnings are errors (`Directory.Build.props`).

## Build and test

```powershell
dotnet test tests/Kannu.Core.Tests
./scripts/publish.ps1            # Kannu.exe + kannu-hook.exe into out/win-x64
./scripts/build-installer.ps1    # Velopack installer into Releases/ (needs vpk)
```

Releases: `scripts/RELEASE.md`. The version lives only in `Directory.Build.props`; the release workflow
refuses a tag that disagrees, and a test requires `docs/release-notes/<version>.md`. Velopack's install,
update and uninstall hooks run in `Program.Main` before any window: keep them fast and never let them
throw. Branches: day-to-day work lands on `development`, `main` is the release branch, PRs target
`development`.

Nothing starts before the Terms of Use are accepted: `App.OnStartup` shows the gate first and only
then calls `ContinueLaunch`, which starts the watcher, notch, tray and updater. New launch work goes in
`ContinueLaunch`. Raising `TermsOfUse.CurrentVersion` asks everyone again; change the "Version" line
in `TERMS.md` with it (a test pins the two).

`Diagnostics` writes Kannu's log and crash reports to `%LOCALAPPDATA%\Kannu\logs`. Log events, never
agent conversations, prompts, file contents or credentials, and pass anything that may leave the PC
through `Diagnostics.Scrub`.

What is ported from macOS Kannu, what is not, and why: `docs/PARITY.md`. A PR that ships, drops or
reroutes a feature updates its row in the same commit.

Core and the hook build and test on any OS. The app compiles anywhere (`EnableWindowsTargeting`) but
runs only on Windows. Never claim UI behaviour was verified unless it was run on Windows.

## Commits

Add one entry at the top of `## [Unreleased]` in `CHANGELOG.md`, in the same commit as the code:

```markdown
### YYYY-MM-DD - <a short title for the change>
- **Developer label:** <the feature, or the request in the requester's own words>
- **Agent label:** <agent feature label, or "none — human-authored">
- **Changes:**
  - <one concrete change per bullet>
```

## Output expectations

For coding tasks report what changed, why, files affected, architectural decisions, testing performed
and remaining concerns.
