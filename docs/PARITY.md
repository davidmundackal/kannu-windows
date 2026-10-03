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
| Passive Codex sessions without hooks | Kannu | **Done (Windows extra)** | macOS has none. Rollouts in `~/.codex/sessions` (or `CODEX_HOME`) read from their tail: working, approval (yellow, if Codex writes it), finished, aborted or errored; a hook session wins. No process check: a crashed Codex stays green until it ages out (10 min) |
| Chat naming, subagent fold, 69 s ended-chat retention, run-error verdict | Kannu | **Done** | |
| Run time, tool calls, Claude tokens | Kannu | **Done** | |
| Per-agent app icons | Kannu | **Done** | From the exe icon, else a coloured initial |
| Detected Editors grid | Kannu | **Done (as the agent rows)** | Settings › Agents lists every agent with Installed / Not installed / Not found on this PC and its icon |
| Closed-notch light: Classic/Minimal style, colour palette (colour-blind safe), breathing, 5 s red blink | Kannu | **Done** | Settings › Notch › Traffic light: Classic (three lights) or Minimal (one), ten-colour palette with no two states sharing a colour, breathing; a fresh red pulses for 4 s. Tray icons follow the colours |
| Open-panel recent chats | Kannu | **Done** | Two-line cards. The findings chip, caffeinate cup and ADR line come with their features |
| Hide Kannu's own `/usage` probe | Kannu | **Doable** | Only needed if the usage refresh (§3) is ported |
| Agents running inside **WSL** | Windows only | **Done (D1)** | Settings › Agents › Agents in WSL installs the hook into each distro's CLI agents (Claude Code, Codex, Gemini CLI, Qwen Code, opencode), pointing at the Windows hook by its Linux path (`wslpath`), so sessions land in the Windows status folder. Needs WSL interop (on by default). Remove it there before uninstalling Kannu |

## 2. Agent extras
| Feature | Origin | Windows | How on Windows / why |
|---|---|---|---|
| **Caffeinate**, smart + manual (cup in the panel, onboarding step) | Kannu | **Done** (onboarding step pending) | Sun button in the open notch (amber while held) and Settings › Agents › Keep the PC awake. Decision and transition tables ported with their tests; `PowerCreateRequest` + `PowerSetRequest(PowerRequestSystemRequired)`, the same semantics as `caffeinate -i`: the display may still sleep, and the lid follows power policy. Port `shouldKeepAwake` / `caffeinateTransition` and their decision-table tests unchanged |
| **Click-through**: IDE chats | Kannu | **Done** | Clicking a row brings the editor's window forward: the hook records the window-owning ancestor (`host_pid`, `host_name`), the app picks that process's window whose title names the project. Without a hook record, the provider's running app. Launching `code <folder>` when nothing runs is not done |
| Click-through: Claude Desktop chat (`claude://…epitaxy/<id>`, `claude://resume`) | Kannu | **Partial** | Claude Desktop's window comes forward (D2); opening the exact chat by URL is not done. |
| Click-through: CLI agent's terminal window | Kannu | **Done** | The hook walks its process parents (`NtQueryInformationProcess`, start times guard reused pids) up to Windows Terminal, VS Code or Cursor, or records a classic console window; the app brings it forward, borrowing the front window's input queue when Windows refuses |
| Click-through: the **exact tab** (macOS: Terminal/iTerm2 via AppleScript) | Kannu | **Decide (D2)** | Windows Terminal has no "select tab for this process" API. UI Automation can select a tab by title, but only best-effort |
| Click-through: tmux pane | Kannu | **Can't (native)** | tmux exists only inside WSL. Revisit with D1 |
| Raise the window whose title matches the project (Accessibility) | Kannu | **Done** | Click-through picks the host's window whose title names the project |
| **Mobile push**: ntfy / Pushover / webhook, test button, 2 s debounce | Kannu | **Done** | Settings › Notifications. Same texts, priorities and URL policy as macOS; topic, keys and webhook URL in Windows Credential Manager |
| "Still waiting on you" reminder push | Kannu | **Done** | `WaitReminder` ported with its rules (one per wait, overdue-at-start not sent); 5/10/15/30 min, off by default |
| Pushes for security findings and usage limits | Kannu | **Done** | High findings (and medium, if chosen) once per finding; usage limits once per window |
| Local desktop notifications (toasts) | Windows only | **Done (D3)** | Off by default. Yellow and red, and the reminder, through the tray icon's notification |
| **Notch skins** (image clipped to the notch, scrim) | Kannu | **Done** | Settings › Notch › Notch skin: PNG/JPG/BMP/GIF (first frame) copied to `%APPDATA%\Kannu\skins`, `ImageBrush` UniformToFill, "Darken the picture" scrim 0–90 %. SVG not supported |
| Tray eye (menu-bar eye) | Kannu | **Done** (Restart doable) | Eye tinted by the light; left click opens the notch with its tabs (closes after 3 s unless the pointer is on it); menu: Open Kannu, Settings…, Agent hooks, status folder, updates, Quit |
| Shortcuts off by default; launch at login on by default | Kannu | **Done** | Ctrl+Alt+K (macOS's ⇧⌘I is developer tools in every Windows browser and editor) |

## 3. Usage and quota
| Feature | Origin | Windows | How on Windows / why |
|---|---|---|---|
| Usage tab (Claude 5 h / weekly / per-model bars, reset countdowns, severity colour) | Kannu | **Done** | Settings › Agents › Usage limits adds `kannu-hook.exe statusline` to Claude Code (a user's own statusline is kept and still drawn); it writes `claude-usage.json` in macOS's shape. `~/.claude.json`'s cache is the fallback. Desktop's history file is not read yet |
| "Fetch latest usage" (runs `claude` in a pty and types `/usage`) | Kannu | **Decide (D4)** | Doable through ConPTY, but fiddly and fragile |
| Codex usage (`chatgpt.com/backend-api/wham/usage`) | Kannu | **Doable** | Token from `~/.codex/auth.json`; Credential Manager fallback |
| Cursor usage (`cursor.com/api/...`) | Kannu | **Doable** | Token from `state.vscdb` (already read) |
| Antigravity session counts | Kannu | **Done** | A line on the Usage tab: sessions and last activity |
| Model pricing (`pricing.json` from the repo), local token and cost totals | Kannu | **Doable** | Portable |
| Usage forecast ("full by 3:40 PM"), near-limit alerts, "resumes at" on 429 stops | Kannu | **Done** | Forecast and alert rules ported with their constants. Readings kept across restarts in `%APPDATA%\Kannu\usage-samples.json` (saved every 5 minutes, 8 days), as macOS does. A Claude card stopped on its quota says "resumes 15:40" |

## 4. Agent Security
| Feature | Origin | Windows | How on Windows / why |
|---|---|---|---|
| Hook checks: hidden Unicode (ASCII smuggling), optional warning to the agent | Kannu | **Done** | Ported into `kannu-hook.exe` with macOS's code-point ranges, flag and RTL-line exemptions, decoding and note outputs; the same marker files switch it |
| Hook checks: secrets in prompts and tool inputs (fingerprint only) | Kannu | **Done** | Same patterns, placeholders, boundary and plausibility rules; only kind, prefix, length and a 12-hex fingerprint are written |
| Hook checks: sensitive files read or changed | Kannu | **Done, Windows list** | Credential stores (`%APPDATA%\Microsoft\Credentials`, `Protect`, `Crypto`), `cmdkey`/`vaultcmd`, Run/RunOnce keys, Startup folders, `schtasks`/`Register-ScheduledTask`/`sc create`, PowerShell profiles and history, browser profiles, `.ssh`, cloud and agent credentials, `.env`. Commands parsed for bash, PowerShell and cmd, including `cmd /c`, `powershell -Command` and `-EncodedCommand`. Needs review by a maintainer |
| Permission-bypass detection (`--dangerously-skip-permissions`, Codex `never`) | Kannu | **Done** | A high finding while the chat runs |
| **Agent policy**: block ssh or anything on Claude Code and Cursor; edit, import or draft rules; open in your editor | Kannu | **Done** (no in-app editor) | Same `~/.kannu/agent-policy.json` and strictness as macOS; refuses on Claude Code and Cursor when "Refuse calls that match" is on. Settings shows the file's state and opens its folder; docs/SECURITY.md has the format |
| Security findings: one row per problem, acknowledge for a project or everywhere, snooze, reveal, copy for agent | Kannu | **Done** (acknowledge is everywhere only) | Settings › Security: grouped cards, Details, Acknowledge (back if it gets worse), Snooze 24 h, Show in folder, Copy for agent. Kept in `%APPDATA%\Kannu\security.json`, 50 per kind |
| Shield cue in the closed notch | Kannu | **Done** | A shield beside the lights, never a light colour; the open notch pins the top high finding with Details and OK |
| New-MCP-server watch | Kannu | **Done** | Every minute, Windows config paths plus session project folders; first look learns. Medium finding per new server |
| **ADR Discovery** (Uber ADR, run by Kannu daily or on MCP change) | Kannu | **Partial (upstream)** | Same schedule and finding mapping as macOS; Kannu runs `adr-discovery --json --dry-run` and saves the snapshot itself (ADR's own folder write fails on Windows). Upstream ADR's file checks don't work on Windows yet (`gate.py` root check, `os.open` on folders, macOS-only paths), so it sees processes, connections and installed apps only; Kannu always shows "partial coverage" |
| **ADR Detection** (LLM analysis of one finished chat; claude-sonnet-5, optional gpt-4o triage) | Kannu | **Done** (no gpt-4o triage) | Opt-in with a consent prompt; "Analyze a finished chat…" lists the 10 newest Claude Code chats. Windows' 32,767-character command line caps the transcript at about 18,000 characters; longer is an error, never a clean verdict |

## 5. App shell
| Feature | Origin | Windows | How on Windows / why |
|---|---|---|---|
| Notch window, closed and open, non-activating, transparent pixels pass clicks | Atoll + Kannu | **Done** | Top centre of the primary display. Shape: **Notch** (attached to the top edge, default) or **Floating pill** (6 px below it), as macOS's standard notch and Dynamic Island styles |
| Physical-notch features (notch height/width, dwell on the hardware notch rect) | Atoll | **Not applicable** | No notch hardware to hide behind. Instead the notch is **hidden by default** and comes out the way macOS's hide-until-hover island does (next rows) |
| Hover to open, click outside to close | Atoll | **Done** | Click outside closes at once, through a low-level mouse hook present only while the notch is open |
| Two-finger scroll open/close, swipe to skip track, trackpad haptics | Atoll | **Doable / Can't** | Scroll via `WM_MOUSEWHEEL` on precision touchpads is doable. Haptics can't be done |
| Visible on every virtual desktop and over fullscreen apps | Kannu | **Done, to verify (D6)** | The notch is a tool window with no taskbar entry, which Windows does not assign to one desktop, so it shows on every desktop without the undocumented pinning API. Beta testers: please confirm. Exclusive-fullscreen games still cover topmost windows |
| Hide while a fullscreen app or video runs | Kannu/Atoll | **Done** | `SHQueryUserNotificationState`: no reveal while busy, D3D full screen or presenting; the always-visible notch steps aside. Settings › Notch › Placement, on by default |
| "Where Kannu appears": external takes over / all displays / built-in only / chosen display | Kannu | **Partial** | Main display, the pointer's display, or a chosen display (main display while it is unplugged). "All displays" (a notch on each) is not done |
| Open on the display you are using (tray click) | Kannu | **Done** | Settings › Notch › Placement: main display, or the monitor the pointer is on each time the notch comes out; per-monitor DPI |
| Hidden until something happens (agent activity reveals it for 7 s; hover keeps it out; 7 s after the pointer leaves) | Kannu | **Done** | `NotchPresence` (Core, tested). Windows reveals on light changes only (a chat appearing lit, a light turning green, yellow or red), not on every hook event as macOS does: the user's choice. Settings › Notch › "Stay hidden until something happens" (on) |
| Reveal by resting the pointer at the top edge | Kannu | **Done, off by default** | macOS always allows it on hidden displays; on Windows it is opt-in (Settings › Notch). 20 Hz cursor poll only while hidden and enabled, 1 s dwell, macOS's entry zone |
| Header, tabs, minimalistic UI mode | Atoll | **Done** | Open notch has icon-only round tabs (80 ms hover selects, as macOS): Agents and Usage, plus caffeinate and a Settings gear |
| Hide Kannu from screenshots and recordings | Atoll | **Done** | Settings › Notch › Placement, off by default |
| Custom app icon | Atoll | **Avoid** | No Dock icon on Windows; the tray eye is the identity |
| Idle animations (shimmer, neon eyes, Lottie/video), 04:20 easter egg, first-launch hello | Atoll + Kannu | **Decide (D9)** | Doable (LottieSharp / MediaElement); purely decorative |
| **Settings window** (21 tabs, search across 208 entries, scroll-to-highlight) | Kannu | **Done** | Windows 11 style. Search reads every row and section title from the pages themselves (no hand-kept index), opens the page, scrolls to the row and highlights it |
| **Onboarding** (profile presets, light style, caffeinate) | Atoll + Kannu | **Done (reduced)** | One welcome window after the Terms: the lights, connect agents, find the tray eye, open Settings. No profile presets |
| **Terms of Use gate**: nothing runs before acceptance; the updater starts after it | Kannu | **Done** | Windows edition of the terms (`TERMS.md`, D8). `App.OnStartup` shows them before the watcher, notch, tray or updater start; Decline quits |
| Launch at login (on by default, repairs a stale entry) | Kannu | **Done** | HKCU `Run` value pointing at Velopack's stable launcher; on once for a fresh install, repaired if stale, removed on uninstall; Settings › General shows when Task Manager turned it off. Not offered in developer/portable builds |
| Global shortcuts (toggle notch, etc.; off by default) | Kannu/Atoll | **Done** | `RegisterHotKey` on a message-only window; Settings says when another app owns the keys |
| Localization (17 languages in `Localizable.xcstrings`) | Atoll + Kannu | **Doable** | Convert the xcstrings to `.resx` and reuse the translations for shared strings |
| Auto-update | Kannu | **Done** | Velopack. macOS uses Sparkle |
| Installer / release workflow / codename / release notes | Kannu | **Done** | `Kannu-win-Setup.exe`, tag-driven `release.yml` |
| Crash reporter: offer the last crash, prefilled GitHub issue, redaction | Kannu | **Done** | `AppDomain`/`Dispatcher` unhandled-exception handlers write `crash-<utc>.txt`; offered 4 s after the next launch with the scrubbed text shown, "Report on GitHub" (pre-filled issue, cut to fit), "Copy report", "Show in folder"; Settings › About › Report a problem pre-fills version and Windows |
| Hang watchdog (stack of the stuck main thread) | Kannu | **Done, different** | A background thread pings the UI thread every second; 5 s without an answer writes `freeze-<utc>.txt` and a memory dump (`MiniDumpWriteDump`, newest only), offered like a crash. A sleep is not a freeze. The dump replaces macOS's in-process stack walk and is never put in a public issue |
| Memory monitor (restart prompt above 1 GB) | Atoll | **Done** | Checked every 5 minutes; a notification, click to restart |
| Export logs / log level | Kannu | **Done** (no log level) | `%LOCALAPPDATA%\Kannu\logs\kannu.log` (1 MB, one rollover); Settings › About › Logs: Open folder, Export… (zip of logs and reports, scrubbed, dumps left out) |
| Restart Kannu, Quit row in Settings | Kannu | **Done** | Settings › About |

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
| Media-key interception, fine steps | Doable (volume) / **Can't** (brightness keys) | Laptop brightness keys are handled by firmware and the maker's driver: no app can take them over. Fine brightness steps are done from Kannu's own Ctrl+Alt+F1/F2 (1/64), DDC/CI or WMI |
| AirPods listening-mode (ANC/Transparency) HUD | **Can't** | No public API |
| Volume HUD replacing Windows' flyout | Decide | Showing ours is doable; hiding Windows' flyout relies on an undocumented window hack |
| Brightness HUD | **Done** | Every change of the built-in panel (WMI `WmiMonitorBrightnessEvent`) and Kannu's own steps show a bar on the notch. Windows' own flyout still shows too. Off by default |
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
| Screen-recording indicator | **Partial (best effort)** | A red dot while an app uses Windows 11's screen-capture permission (CapabilityAccessManager consent store). Recorders using older methods (OBS by default) are not seen. Off by default |
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
- **D5 – ADR Discovery / Detection on Windows.** Done: Detection fully, Discovery partially until upstream ADR's file checks work on Windows.
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
   - Settings window with search (done);
   - Terms gate (D8), with the updater moved behind it (done);
   - onboarding (done);
   - launch at login;
   - tray left-click (done);
   - display placement (done);
   - hidden-until-activity notch (D7) (done);
   - fullscreen hide (done);
   - capture exclusion;
   - crash, hang and log reports;
   - shortcuts (done);
   - localization plumbing.
2. **Phase 3 – Kannu's agent extras:**
   - light style, palette, breathing and blink (done);
   - caffeinate (done);
   - click-through (D2) (done);
   - mobile push + reminder (done);
   - toasts (D3) (done);
   - notch skins (done);
   - Detected Editors (done);
   - hookless Codex (done);
   - WSL (D1) (done).
3. **Phase 4 – usage:** the Claude statusline writer, Codex/Cursor/Antigravity cards, pricing, forecast, near-limit alerts and pushes (D4).
4. **Phase 5 – Agent Security:**
   - hook checks (hidden text, secrets, the Windows sensitive-path list, bypass);
   - findings UI and shield;
   - agent policy enforcement and editor;
   - MCP watch;
   - ADR (D5) (done; Discovery partial upstream).
5. **Phase 6+ – inherited utilities** per D9.

Each phase ships like Phase 1:
- ported pure logic with the macOS tests;
- a CHANGELOG entry;
- green CI;
- a note of what still needs a real Windows run.
