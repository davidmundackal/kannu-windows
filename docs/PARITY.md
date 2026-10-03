# Kannu macOS → Windows feature map

Every user-facing feature of Kannu for macOS ([libinmv/kannu](https://github.com/libinmv/kannu)) and where
it stands on Windows: done, doable, deliberately avoided, impossible, or waiting on a decision. Built from
a full inventory of the macOS source (`Kannu/managers/**`, `Kannu/components/**`, `Constants.swift`,
`CHANGELOG.md`) on 2026-10-01.

**Keep it true:** a PR that ships, drops or changes the route of a feature updates its row here in the
same commit.

**Origin.** *Kannu* marks features built for Kannu itself (they appear in macOS Kannu's CHANGELOG as new
features); *Atoll* marks features inherited from the Atoll / Boring.Notch fork.

## Legend
- **Done**: shipped on the Windows branch.
- **Partial**: some of it shipped; the note says what is missing.
- **Doable**: a clear Windows route; only the work is left.
- **Decide**: doable, but whether, or how, is the maintainer's call. See "Open decisions" below.
- **Avoid**: deliberately not ported; the reason is given.
- **Can't**: Windows offers no API, or blocks third-party apps.

## 1. Agent monitoring
| Feature | Origin | Windows | How on Windows / why |
|---|---|---|---|
| Editor hooks, 8 tools (Cursor, VS Code+Copilot CLI, Codex, Claude Code, Antigravity, Gemini, Qwen, opencode) | Kannu | **Done** | Native AOT `kannu-hook.exe` replaces bash+python3; same args, same status files. One layout table drives install, uninstall and the installed check |
| Status-file contract shared with macOS | Kannu | **Done** | `~/.kannu/agent-status`, same JSON and merge rules |
| Traffic-light state machine (stale ladder, held yellow, 360 s cap, collapse/dim) | Kannu | **Done** | Ported with the macOS tests |
| Passive Claude Code (sessions + transcripts + pid liveness) | Kannu | **Done** | |
| Passive Cursor (transcripts + state.vscdb, composer and Glass titles) | Kannu | **Done** | |
| Passive Warp agent mode | Kannu | **Partial** | Code done. The warp.sqlite path on Windows is a guess; check it on a real install |
| Passive Claude Desktop agent mode (audit.jsonl) | Kannu | **Done** | `%APPDATA%\Claude\…`. Check it on a real install |
| Passive Codex sessions without hooks | Kannu | **Partial** | Only chat names are read from `~/.codex/sessions`; hookless Codex cards are not shown yet. Doable |
| Chat naming, subagent fold, 69 s ended-chat retention, run-error verdict | Kannu | **Done** | |
| Run time, tool calls, Claude tokens | Kannu | **Done** | |
| Per-agent app icons | Kannu | **Done** | From the exe icon, else a coloured initial |
| Detected Editors grid | Kannu | **Doable** | Needs the Settings window |
| Closed-notch light: Classic/Minimal style, colour palette (colour-blind safe), breathing, 5 s red blink | Kannu | **Partial** | Dots and summary exist. Style picker, palette, breathing and blink are doable (WPF animations) |
| Open-panel recent chats | Kannu | **Done** | Two-line cards. The findings chip, caffeinate cup and ADR line come with their features |
| Hide Kannu's own `/usage` probe | Kannu | **Doable** | Only needed if the usage refresh (§3) is ported |
| Agents running inside **WSL** | Windows only | **Doable (D1: yes, Phase 3)** | Not a macOS feature, but common on Windows: a hook inside WSL writes to Linux `~/.kannu`, which the Windows app never sees. Plan: a small Linux hook in WSL writing to `/mnt/c/Users/<name>/.kannu/agent-status` |

## 2. Agent extras
| Feature | Origin | Windows | How on Windows / why |
|---|---|---|---|
| **Caffeinate**, smart + manual (cup in the panel, onboarding step) | Kannu | **Done** (onboarding step pending) | Sun button in the open notch (amber while held) and Settings › Agents › Keep the PC awake. Decision and transition tables ported with their tests; `PowerCreateRequest` + `PowerSetRequest(PowerRequestSystemRequired)`, the same semantics as `caffeinate -i`: the display may still sleep, and the lid follows power policy. Port `shouldKeepAwake` / `caffeinateTransition` and their decision-table tests unchanged |
| **Click-through**: IDE chats | Kannu | **Doable** | Activate a running Cursor/Code window by its process (`AllowSetForegroundWindow` plus a foreground-lock workaround), or launch `cursor`/`code <folder>` |
| Click-through: Claude Desktop chat (`claude://…epitaxy/<id>`, `claude://resume`) | Kannu | **Doable** | Only if Claude Desktop for Windows registers `claude://`; verify first. Read Desktop's session index under `%APPDATA%\Claude` |
| Click-through: CLI agent's terminal window | Kannu | **Doable** | Walk the process parents (Toolhelp32 / `NtQueryInformationProcess`) up to Windows Terminal, conhost, VS Code or Cursor, then bring that window forward |
| Click-through: the **exact tab** (macOS: Terminal/iTerm2 via AppleScript) | Kannu | **Decide (D2)** | Windows Terminal has no "select tab for this process" API. UI Automation can select a tab by title, but only best-effort |
| Click-through: tmux pane | Kannu | **Can't (native)** | tmux exists only inside WSL. Revisit with D1 |
| Raise the window whose title matches the project (Accessibility) | Kannu | **Doable** | `EnumWindows` + title match; no permission needed on Windows |
| **Mobile push**: ntfy / Pushover / webhook, test button, 2 s debounce | Kannu | **Doable** | Plain HTTPS. Secrets go in Windows Credential Manager (DPAPI) instead of Keychain |
| "Still waiting on you" reminder push | Kannu | **Doable** | Pure logic (`AgentWaitReminder`) + tests |
| Pushes for security findings and usage limits | Kannu | **Doable** | Comes with §3 and §4 |
| Local desktop notifications (toasts) | Windows only | **Decide (D3)** | macOS Kannu has none (mobile push only). Windows toasts would be new |
| **Notch skins** (image clipped to the notch, scrim) | Kannu | **Doable** | WPF `ImageBrush` in the pill; PNG/JPG/GIF/SVG via SharpVectors or WPF |
| Tray eye (menu-bar eye) | Kannu | **Done** (Restart doable) | Eye tinted by the light; left click opens the notch with its tabs (closes after 3 s unless the pointer is on it); menu: Open Kannu, Settings…, Agent hooks, status folder, updates, Quit |
| Shortcuts off by default; launch at login on by default | Kannu | **Doable** | §5 |

## 3. Usage and quota
| Feature | Origin | Windows | How on Windows / why |
|---|---|---|---|
| Usage tab (Claude 5 h / weekly / per-model bars, reset countdowns, severity colour) | Kannu | **Doable** | Claude Code on Windows supports a `statusLine` command, so `kannu-hook.exe` can write `claude-usage.json` (as `kannu-usage-status.sh` does on macOS). Fallbacks are `~/.claude.json` and Desktop's `plan-usage-history.json` under `%APPDATA%` |
| "Fetch latest usage" (runs `claude` in a pty and types `/usage`) | Kannu | **Decide (D4)** | Doable through ConPTY, but fiddly and fragile |
| Codex usage (`chatgpt.com/backend-api/wham/usage`) | Kannu | **Doable** | Token from `~/.codex/auth.json`; Credential Manager fallback |
| Cursor usage (`cursor.com/api/...`) | Kannu | **Doable** | Token from `state.vscdb` (already read) |
| Antigravity session counts | Kannu | **Doable** | From the hook files |
| Model pricing (`pricing.json` from the repo), local token and cost totals | Kannu | **Doable** | Portable |
| Usage forecast ("full by 3:40 PM"), near-limit alerts, "resumes at" on 429 stops | Kannu | **Doable** | Pure logic + tests port as is |

## 4. Agent Security
| Feature | Origin | Windows | How on Windows / why |
|---|---|---|---|
| Hook checks: hidden Unicode (ASCII smuggling), optional warning to the agent | Kannu | **Doable** | Port into `kannu-hook.exe`; the same marker files switch it on and off |
| Hook checks: secrets in prompts and tool inputs (fingerprint only) | Kannu | **Doable** | Same patterns |
| Hook checks: sensitive files read or changed | Kannu | **Doable, needs a Windows list** | The macOS list (Keychains, `security`, LaunchAgents, `launchctl`) must become: `%APPDATA%\Microsoft\Credentials` and `Protect`, `cmdkey`, Credential Manager, Run/RunOnce keys, Startup folder, `schtasks`, PowerShell profile and history, browser profiles, `.ssh`, cloud CLI configs. Drafted by an agent, reviewed by a maintainer |
| Permission-bypass detection (`--dangerously-skip-permissions`, Codex `never`) | Kannu | **Doable** | |
| **Agent policy**: block ssh or anything on Claude Code and Cursor; edit, import or draft rules; open in your editor | Kannu | **Doable** | Hook-side deny in C#, using the same `~/.kannu/agent-policy.json` as macOS. A policy written on one platform works on the other |
| Security findings: one row per problem, acknowledge for a project or everywhere, snooze, reveal, copy for agent | Kannu | **Doable** | Big UI piece. Needs Settings (§5). Explorer `/select,` replaces Finder reveal |
| Shield cue in the closed notch | Kannu | **Doable** | |
| New-MCP-server watch | Kannu | **Doable** | Windows config paths (`%APPDATA%\Claude\claude_desktop_config.json`, `%APPDATA%\Code\User\…`, `~/.claude.json`, …) |
| **ADR Discovery** (Uber ADR, run by Kannu daily or on MCP change) | Kannu | **Decide (D5)** | ADR is Python, installed with `uv`. Whether ADR Discovery supports Windows is unverified |
| **ADR Detection** (LLM analysis of one finished chat; claude-sonnet-5, optional gpt-4o triage) | Kannu | **Decide (D5)** | Same question. It also sends a transcript to Anthropic, so it is opt-in |

## 5. App shell
| Feature | Origin | Windows | How on Windows / why |
|---|---|---|---|
| Notch window, closed and open, non-activating, transparent pixels pass clicks | Atoll + Kannu | **Done** | Top centre of the primary display. Shape: **Notch** (attached to the top edge, default) or **Floating pill** (6 px below it), as macOS's standard notch and Dynamic Island styles |
| Physical-notch features (notch height/width, dwell on the hardware notch rect) | Atoll | **Not applicable** | No notch hardware to hide behind. Instead the notch is **hidden by default** and comes out the way macOS's hide-until-hover island does (next rows) |
| Hover to open, click outside to close | Atoll | **Partial** | Hover open/close is done. Click-outside close is doable (low-level mouse hook or deactivation) |
| Two-finger scroll open/close, swipe to skip track, trackpad haptics | Atoll | **Doable / Can't** | Scroll via `WM_MOUSEWHEEL` on precision touchpads is doable. Haptics can't be done |
| Visible on every virtual desktop and over fullscreen apps | Kannu | **Decide (D6)** | Pinning to all desktops needs the *undocumented* `IVirtualDesktopPinnedApps`. Exclusive-fullscreen games always cover topmost windows |
| Hide while a fullscreen app or video runs | Kannu/Atoll | **Doable** | `SHQueryUserNotificationState` (D3D fullscreen, presentation mode, busy) |
| "Where Kannu appears": external takes over / all displays / built-in only / chosen display | Kannu | **Doable** | `Screen.AllScreens` + per-monitor DPI; the resolver logic and its tests port as is |
| Open on the display you are using (tray click) | Kannu | **Partial** | Tray click opens the notch, on the primary display until multi-display placement is ported |
| Hidden until something happens (agent activity reveals it for 7 s; hover keeps it out; 7 s after the pointer leaves) | Kannu | **Done** | `NotchPresence` (Core, tested). Windows reveals on light changes only (a chat appearing lit, a light turning green, yellow or red), not on every hook event as macOS does: the user's choice. Settings › Notch › "Stay hidden until something happens" (on) |
| Reveal by resting the pointer at the top edge | Kannu | **Done, off by default** | macOS always allows it on hidden displays; on Windows it is opt-in (Settings › Notch). 20 Hz cursor poll only while hidden and enabled, 1 s dwell, macOS's entry zone |
| Header, tabs, minimalistic UI mode | Atoll | **Partial** | Open notch has icon-only round tabs (80 ms hover selects, as macOS) and a Settings gear. Agents is the only tab until Usage and the D9 features land. Minimalistic mode: not needed while there is one tab |
| Hide Kannu from screenshots and recordings | Atoll | **Doable** | `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` |
| Custom app icon | Atoll | **Avoid** | No Dock icon on Windows; the tray eye is the identity |
| Idle animations (shimmer, neon eyes, Lottie/video), 04:20 easter egg, first-launch hello | Atoll + Kannu | **Decide (D9)** | Doable (LottieSharp / MediaElement); purely decorative |
| **Settings window** (21 tabs, search across 208 entries, scroll-to-highlight) | Kannu | **Partial** | Windows 11 style (nav pane, cards, toggle switches, light/dark and accent from Windows): Notch, Agents (hook install/remove per agent) and About pages. More pages, and search, come with their features |
| **Onboarding** (profile presets, light style, caffeinate) | Atoll + Kannu | **Doable** | |
| **Terms of Use gate**: nothing runs before acceptance; the updater starts after it | Kannu | **Done** | Windows edition of the terms (`TERMS.md`, D8). `App.OnStartup` shows them before the watcher, notch, tray or updater start; Decline quits |
| Launch at login (on by default, repairs a stale entry) | Kannu | **Done** | HKCU `Run` value pointing at Velopack's stable launcher; on once for a fresh install, repaired if stale, removed on uninstall; Settings › General shows when Task Manager turned it off. Not offered in developer/portable builds |
| Global shortcuts (toggle notch, etc.; off by default) | Kannu/Atoll | **Doable** | `RegisterHotKey` |
| Localization (17 languages in `Localizable.xcstrings`) | Atoll + Kannu | **Doable** | Convert the xcstrings to `.resx` and reuse the translations for shared strings |
| Auto-update | Kannu | **Done** | Velopack. macOS uses Sparkle |
| Installer / release workflow / codename / release notes | Kannu | **Done** | `Kannu-win-Setup.exe`, tag-driven `release.yml` |
| Crash reporter: offer the last crash, prefilled GitHub issue, redaction | Kannu | **Done** | `AppDomain`/`Dispatcher` unhandled-exception handlers write `crash-<utc>.txt`; offered 4 s after the next launch with the scrubbed text shown, "Report on GitHub" (pre-filled issue, cut to fit), "Copy report", "Show in folder"; Settings › About › Report a problem pre-fills version and Windows |
| Hang watchdog (stack of the stuck main thread) | Kannu | **Done, different** | A background thread pings the UI thread every second; 5 s without an answer writes `freeze-<utc>.txt` and a memory dump (`MiniDumpWriteDump`, newest only), offered like a crash. A sleep is not a freeze. The dump replaces macOS's in-process stack walk and is never put in a public issue |
| Memory monitor (restart prompt above 1 GB) | Atoll | **Doable** | Small |
| Export logs / log level | Kannu | **Done** (no log level) | `%LOCALAPPDATA%\Kannu\logs\kannu.log` (1 MB, one rollover); Settings › About › Logs: Open folder, Export… (zip of logs and reports, scrubbed, dumps left out) |
| Restart Kannu, Quit row in Settings | Kannu | **Doable** | Small |

## 6. Inherited utilities (Atoll lineage)
| Feature | Windows | How / why |
|---|---|---|
| Now Playing: art, controls, sneak peek, swipe | **Decide (D9)** → doable | GSMTC (`GlobalSystemMediaTransportControlsSessionManager`) covers Spotify, browsers and most players. Big |
| Lyrics (lrclib), explicit badge | Doable | Portable HTTP |
| Visualizer / real-time spectrum | Doable | WASAPI loopback capture |
| Spotify Canvas (sp_dc cookie → web token) | **Avoid** | Scrapes Spotify's web player with a session cookie; ToS-grey and breaks often |
| Apple Music animated artwork (MusicKit) | **Can't** | MusicKit is Apple-only |
| Output device picker | Decide | Switching the default device needs the undocumented `IPolicyConfig` |
| AirPlay multi-room via Music.app | **Can't** | No Music.app/AirPlay on Windows |
| Floating media controls | Doable | |
| Media card opens the browser tab that is playing | **Partial** | GSMTC names the app, not the tab. The browser can be activated; the exact tab can't |
| Fullscreen lock-screen artwork / wallpaper swap | **Can't** | Third parties can't draw on the Windows lock screen |
| Media-key interception, fine steps | Doable (volume) / **Can't** (brightness) | `WH_KEYBOARD_LL` for `VK_VOLUME_*`. Brightness keys are firmware/OEM |
| AirPods listening-mode (ANC/Transparency) HUD | **Can't** | No public API |
| Volume HUD replacing Windows' flyout | Decide | Showing ours is doable; hiding Windows' flyout relies on an undocumented window hack |
| Brightness HUD | Doable (laptops) | WMI `WmiMonitorBrightness`; external monitors via DDC/CI (`dxva2`) |
| Custom OSD / vertical / circular HUD styles | Doable | WPF windows |
| Keyboard backlight HUD | **Can't** | OEM-specific WMI only |
| Caps Lock indicator | Doable | `WH_KEYBOARD_LL` + `GetKeyState` |
| BetterDisplay / Lunar DDC integrations | **Avoid** | Those are Mac apps. Native DDC/CI covers the need |
| Laptop battery (charging/low/full HUDs, low-battery sound) | Doable | `SystemInformation.PowerStatus` / `Windows.Devices.Power`, battery saver status |
| Bluetooth audio connect HUD + battery | Doable / **Partial for AirPods** | Device battery via the Bluetooth DEVPKEY or GATT `0x180F`. Per-bud and case battery needs Apple BLE advertisement parsing |
| Timer (presets, notch countdown, alarm, floating controls) | Doable | Portable |
| Mirror timers from the Clock app | **Can't** | Windows Clock has no API |
| Focus / Do Not Disturb indicator | Doable (partial) | `SHQueryUserNotificationState` (quiet time); Win11 Focus sessions via `FocusSessionManager` |
| Camera / mic in-use indicators | Decide | Doable via the CapabilityAccessManager ConsentStore registry, but Windows 11 already shows these in the tray |
| Screen-recording indicator | **Can't** | No system signal on Windows |
| Clipboard history | **Avoid (recommend)** | Windows has Win+V built in. Note: the macOS version records passwords too (no concealed-type filter); worth fixing there |
| Downloads indicator | Doable | `FileSystemWatcher` on Downloads; Chromium `.crdownload`, Firefox `.part` |
| Shelf (drop files on the notch, tray, drag out, actions) | Decide (D9) | Doable (OLE drag-drop, `IThumbnailCache`). Big |
| AirDrop share | **Can't** → alternative | Windows Nearby Share via `DataTransferManager` |
| LocalSend send | Doable | Open protocol (multicast + HTTPS) |
| Notes tab | Doable | Portable |
| Apple Notes sync | **Can't** | No Notes.app. OneNote via Microsoft Graph would be a different feature |
| Stats (CPU/GPU/mem/net/disk, top processes) | Decide (D9) | Doable via PDH. **Temperatures can't** without a kernel driver (LibreHardwareMonitor needs admin) |
| Screen Assistant (multi-provider AI chat + screenshots) | **Avoid (recommend)** | AGENTS.md: "Kannu is not a chatbot". It is off-mission |
| Extensions platform (XPC + WebSocket JSON-RPC, AtollExtensionKit) | **Avoid for now** | XPC can't be ported. No Windows extension ecosystem exists yet; revisit if someone asks |
| Lock-screen widgets (media, weather, timer, battery, focus), lock animation | **Can't** | Windows blocks third-party UI on the lock screen. Lock *detection* works (`WTSRegisterSessionNotification`) but has nothing to drive |
| Calendar, terminal, colour picker, mirror, reminders | **Avoid** | Already removed from macOS Kannu |

## Decisions
- **D1 – WSL agents.** Should Claude Code or Codex running inside WSL show on the notch?
  - *Decided:* yes (Phase 3).
- **D2 – Exact Windows Terminal tab on click.**
  - *Default taken:* window only for v1, the UI Automation tab match later.
- **D3 – Windows toast notifications** (yellow/red when the notch is not visible). New compared with macOS.
  - *Decided:* yes, off by default.
- **D4 – "Fetch latest usage" button** (drives `claude /usage` in a hidden console).
  - *Decided:* skip for v1.
- **D5 – ADR Discovery / Detection on Windows.** Depends on Uber ADR running on Windows, still unverified.
  - *Decided:* Kannu's own checks and the policy first; ADR only if it runs cleanly on Windows.
- **D6 – Pin the notch to all virtual desktops** (undocumented COM API that can break on Windows updates).
  - *Default taken:* yes, behind a try/fallback.
- **D7 – Default visibility.** *Decided:* hidden by default, revealed on light changes, top-edge reveal opt-in.
- **D8 – Terms of Use for Windows.** Reuse macOS `TERMS.md` v1 as is, or a Windows edition (names davidmundackal/kannu-windows, the Velopack updater)?
  - *Decided:* a Windows edition (`TERMS.md`, version 1).
- **D9 – Scope of the Atoll-inherited utilities** (media, HUDs, battery/Bluetooth, timer, shelf/LocalSend, notes, stats, idle animations). Options:
  - (a) agent-focused only;
  - (b) add a small set: Now Playing, timer, downloads, battery;
  - (c) everything doable.
  - *Decided:* **(b)**, after the agent features.

## Roadmap (Kannu's own features before inherited ones)
1. **Phase 2 – shell essentials:**
   - Settings window with search (window done; search to come);
   - Terms gate (D8), with the updater moved behind it (done);
   - onboarding;
   - launch at login;
   - tray left-click (done);
   - display placement;
   - hidden-until-activity notch (D7) (done);
   - fullscreen hide;
   - capture exclusion;
   - crash, hang and log reports;
   - shortcuts;
   - localization plumbing.
2. **Phase 3 – Kannu's agent extras:**
   - light style, palette, breathing and blink;
   - caffeinate;
   - click-through (D2);
   - mobile push + reminder;
   - toasts (D3);
   - notch skins;
   - Detected Editors;
   - hookless Codex;
   - WSL (D1).
3. **Phase 4 – usage:** the Claude statusline writer, Codex/Cursor/Antigravity cards, pricing, forecast, near-limit alerts and pushes (D4).
4. **Phase 5 – Agent Security:**
   - hook checks (hidden text, secrets, the Windows sensitive-path list, bypass);
   - findings UI and shield;
   - agent policy enforcement and editor;
   - MCP watch;
   - ADR (D5).
5. **Phase 6+ – inherited utilities** per D9.

Each phase ships like Phase 1:
- ported pure logic with the macOS tests;
- a CHANGELOG entry;
- green CI;
- a note of what still needs a real Windows run.
