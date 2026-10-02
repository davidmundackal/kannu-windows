# Changelog

Each commit must add one new entry under `## [Unreleased]` before committing.

## [Unreleased]

### 2026-10-02 - A beta guide, and the first beta pre-release
- **Developer label:** "how do I test a install in windows, if you can please add the file in our documents folder, i will send that to beta users"
- **Agent label:** Beta tester guide in docs/, release notes brought up to date, and a v0.1.0-beta.1 pre-release built by the release workflow
- **Changes:**
  - `docs/BETA.md`: install (including SmartScreen), a test checklist, known gaps, updates, how to
    report a problem or a security issue, where Kannu keeps things, uninstall.
  - `docs/release-notes/0.1.0.md`: the hidden-by-default notch, Settings and the Terms of Use.
  - README links the beta guide.
  - `release.yml` uploads with `--merge`, so a release first created on GitHub's Releases page (which
    creates the tag) receives the installer; `scripts/RELEASE.md` describes that route.

### 2026-10-02 - A hidden notch that comes out when agents change, a Windows Settings window, and the Terms gate
- **Developer label:** "what we want is a floating pill option but by default hidden and a notch appears like when a state changes like we have in mac app ... clicking our kannu icon in bar reveals notch and our different tabs, see if hover can still work as usual once notch is shown, but when its closed do not reveal if pointed near notch, atleast not by default, users can switch that option on in settings, make settings look a bit nice for windows users"; "8 write windows version"
- **Agent label:** Port of macOS Kannu's hide-until-hover reveal rules, a Windows 11 style Settings window, and a Windows edition of the Terms of Use with its launch gate
- **Changes:**
  - `NotchPresence` (Core): the notch is hidden by default and comes out for 7 s when an agent's
    light changes (macOS `notchRevealHoldSeconds`), stays while the pointer is on it, lingers 7 s
    after it leaves; the tray eye opens it and closes it after 3 s unless the pointer is on it.
    `AgentActivity.IsRevealWorthy`: a chat appearing lit or a light turning green, yellow or red
    reveals; thinking/executing, tool calls, dimming and chats going away do not.
  - Notch: slides in and out from above the top edge (snaps with animation effects off), never
    blocks clicks while hidden; Notch or Floating pill shape (macOS radii and 6 px offset); the open
    notch has icon-only round tabs (Agents for now) and a Settings gear.
  - Opt-in top-edge reveal: a 20 Hz cursor check that runs only while hidden and enabled, with a 1 s
    dwell in macOS's entry zone.
  - Tray: left click opens the notch; menu gains Open Kannu and Settings…; "Show notch" is gone.
  - Settings window in the Windows 11 style (navigation pane, cards, toggle switches, light/dark
    and accent colour from Windows, dark title bar): Notch (shape, hidden until something happens,
    top-edge reveal, open on hover), Agents (install or remove each agent's hook), About (version,
    updates, Terms, licence, links). `AppSettings` in `%APPDATA%\Kannu\settings.json`.
  - `TERMS.md`: a Windows edition of the Terms of Use (version 1). Nothing starts until it is
    accepted; Decline quits (`TermsOfUse`, `MarkdownLite` renders it).
  - `docs/PARITY.md`, README and AGENTS.md updated; decisions D1–D9 recorded.
  - 351 tests.

### 2026-10-01 - A map of every macOS Kannu feature and where it stands on Windows
- **Developer label:** "plan and create a map for every feature I specifically added for mac version and if we have done it in windows, what do we genuinely avoided, what we can do, what needs maybe a decision from me, what cant be done"
- **Agent label:** Feature-parity map from a full inventory of the macOS source, with open decisions and a re-ordered roadmap
- **Changes:**
  - `docs/PARITY.md`: about 110 macOS features (agent monitoring, agent extras, usage, Agent
    Security, app shell, inherited utilities), each marked Done / Partial / Doable / Decide / Avoid /
    Can't with its Windows route or the reason, and whether it is Kannu's own or inherited from Atoll.
  - Nine open decisions (WSL agents, exact terminal tab, toasts, usage refresh, ADR on Windows,
    all-desktops pinning, default visibility, Windows terms, inherited-utility scope) with
    recommendations.
  - Roadmap re-ordered so Kannu's own features (caffeinate, click-through, usage, Agent Security,
    push, skins) come before inherited utilities.
  - `AGENTS.md` points at the map and asks PRs to keep it current.

### 2026-10-01 - Installer, releases and automatic updates
- **Developer label:** "how does someone install it, can we do verything like libin has setup for kannu mac"
- **Agent label:** Velopack installer and updater, the Windows counterpart of macOS Kannu's DMG + Sparkle release pipeline
- **Changes:**
  - `Program.Main` runs Velopack first. After an install or update, every agent that carries a Kannu
    hook is rewritten and `kannu-hook.exe` is copied again (`AgentHookInstaller.Reinstall`); before
    an uninstall, Kannu's entries are removed from every agent (`UninstallAll`).
  - `UpdateService`: checks GitHub Releases at launch and daily, downloads in the background and
    applies on the next start; tray menu shows the version and codename, **Check for Updates…** and
    **Restart to Update**. Off in Debug builds and builds Velopack did not install; a test build
    follows pre-releases only.
  - `ReleaseInfo` (version from `Directory.Build.props`, codename Heimdall, repository URL).
  - `.github/workflows/release.yml`: a `v*` tag tests, builds the installer, signs it only when the
    `WINDOWS_SIGN_*` secrets exist, and publishes the GitHub Release with delta packages; it refuses a
    tag that disagrees with `<Version>`. CI builds the installer on every PR.
  - `scripts/build-installer.ps1`, `scripts/RELEASE.md`, `publish.ps1 -Version`, README **Install**,
    `docs/release-notes/0.1.0.md`.
  - 317 tests.

### 2026-10-01 - Agent cards show the agent's icon, run time, tools and tokens
- **Developer label:** "go" (Phase 1 of the parity plan: notch agent UI parity)
- **Agent label:** Port of AgentProviderIconView's sources and ClaudeTurnTokens; two-line cards
- **Changes:**
  - Each card shows the agent's own app icon (found at its usual Windows install location, else
    from a running copy), or a coloured initial for terminal agents in macOS's symbol colours.
    Resolved once per agent and kept ten minutes.
  - `ClaudeTurnTokens`: a Claude request's tokens ("1.4M in · 45k out"), read forward from the
    transcript offset the hook recorded at the turn's start plus the turn's subagent transcripts,
    deduplicated per message, within a budget per pass, shown only once caught up, never reading
    outside `~/.claude/projects` or through a link, and only token counts. Read off the UI thread.
  - Cards are two lines: the chat title, then agent · state · run time · tools · tokens.
  - 311 tests.

### 2026-10-01 - Warp, Claude Desktop agent mode and named Codex chats on the notch
- **Developer label:** "go" (Phase 1 of the parity plan: the remaining passive sources)
- **Agent label:** Ports of WarpAgentStore and ClaudeDesktopAgentSessionStore; Codex names from its logs
- **Changes:**
  - `WarpStore` (Kannu.Detection): Warp's agent mode from `warp.sqlite`'s `ai_queries`, read-only
    with the WAL honoured — pending counts as running only while Warp runs and within the 6-minute
    window, a failed exchange is the run's verdict, the prompt is the title. Windows locations are
    best guesses (`%LOCALAPPDATA%\warp\Warp\data\warp.sqlite` and two others) until checked on a
    real install.
  - `ClaudeDesktopAgentStore`: Claude Desktop's agent mode ("Cowork") from each session's
    `audit.jsonl` under `%APPDATA%\Claude\local-agent-mode-sessions`, including dispatch agents:
    running, thinking, stopped, throttled, and the error verdict, cached against mtime+size.
  - Codex hook cards are named, and given a project, from Codex's own session logs; Codex and
    Claude log names and projects are now cached against mtime+size.
  - 296 tests.

### 2026-10-01 - CI builds again on the newer Windows .NET SDK
- **Developer label:** CI red on PR #1 (run 4): `SessionLogParser.cs(159,30): error CS1579`
- **Agent label:** Fix a compiler-version-dependent overload binding; pin the C# version
- **Changes:**
  - `SessionLogParser`: `Enumerable.Reverse(...)` instead of `array.Reverse()`, which the newer SDK
    on the Windows runner (8.0.425) bound to the in-place `MemoryExtensions.Reverse` returning void.
  - `Directory.Build.props`: `LangVersion` pinned to 12 (was `latest`), so local and CI compilers
    resolve overloads the same way.

### 2026-10-01 - Cursor chats show on the notch from its transcripts and database
- **Developer label:** "go" (Phase 1 of the parity plan: Cursor passive detection)
- **Agent label:** Port of CursorTranscriptParser, CursorComposerStore, CursorGlassAgentStore and the Cursor merge rules
- **Changes:**
  - `CursorTranscripts`: Cursor agent transcripts' tails (done, tool running, approval card pending,
    prompt awaiting a reply, turn ended), subagent-to-parent mapping, project names from slugs, and
    first-prompt titles, all cached against mtime+size.
  - `CursorSessions`: the macOS rules for combining Cursor evidence — a composer/transcript snapshot to
    a light; transcript context on Cursor hook cards that never overrides a hook's yellow and never
    demotes a fresh hook; hook/transcript merge where a fresh hook's executing beats a lingering
    transcript yellow; subagent chats rolled into their parent; chat titles vetted against tool names,
    plan names and the chat's own prose.
  - New `Kannu.Detection` library (keeps SQLite out of the hook): `CursorDatabase` reads Cursor's
    `state.vscdb` read-only — composer headers and records, workspace databases for unresolved chats,
    the plan registry and Glass agent titles. Any locked or missing database yields nothing.
  - The app reads Cursor only while Cursor runs, and feeds its pending approvals into the yellow hold
    (REGRESSIONS entry 12).
  - 272 tests.

### 2026-10-01 - Claude Code sessions show on the notch even without hooks
- **Developer label:** "go" (Phase 1 of the parity plan: passive Claude detection)
- **Agent label:** Port of AgentSessionLogParser and buildClaudeSessions, with their macOS tests
- **Changes:**
  - `SessionLogParser`: Claude transcript tails classified as macOS does (tool in flight, owes a
    response, turn finished, unreadable), with escalating read windows that survive a split
    multibyte character (REGRESSIONS entry 4), Esc interrupts, API-error verdicts, and the draft
    trailer that must not relight an idle chat. Chat titles from Claude's custom-title/ai-title,
    else the first prompt; Codex session ids and prompts. Every verdict cached against mtime+size.
  - `ClaudePassiveScanner`: reads `~/.claude/sessions/<pid>.json`, checks the process (start time
    must fit the record, so a reused pid is dead; the late-written record rule of entry 3 kept),
    and builds passive sessions. Passive detection never claims yellow; its live tails only hold a
    hook's yellow (entry 12). Untrusted records are skipped field by field.
  - The app's tick now feeds that evidence into the reconciler: a hook file is demoted when the
    process died, promoted during a long silent tool call, and named from the transcript. 1 s while
    a card is listed, 5 s otherwise.
  - 248 tests, including ports of macOS AgentSessionLogParserTests and PassiveClaudeStateTests.

### 2026-10-01 - The notch's lights follow macOS Kannu's rules exactly
- **Developer label:** "go" (Phase 1 of the parity plan: the traffic-light state machine)
- **Agent label:** Port of AgentTrafficLightMapper, execution clock and subagent fold, with their tests
- **Changes:**
  - `AgentStateMachine`: `resolveHookState` (6-minute active window, 5-minute yellow unless held,
    red 5 s then dim 5 s), the per-provider yellow hold (REGRESSIONS entry 12), the stale-cap rules
    for Claude files a live process or subagent still proves, the Claude hook/passive reconciler
    (entries 5 and 7), host/engine identity (Cursor driving Claude names the card), one card per
    conversation, and ended red chats staying listed dim for 69 s.
  - `AgentExecutionClock` and `TurnDisplay`: a run's clock survives a staleness demotion and only
    restarts after a real stop (entry 15); cards show "Running 3m", "Ran 1h 2m", "12 tools".
  - `SubagentFold`: a subagent's file folds into its chat's card, turning it yellow when the subagent
    waits, adding its tool calls to the turn, never relighting a finished chat.
  - `HookSessionReader` + `AgentSessionPipeline`: status files to cards in the macOS order; stale files
    are deleted only if unchanged since read and only under the hooks' lock.
  - The app reruns the pipeline every second only while something is listed; idle, it does nothing.
  - Cards order as on macOS (red, then yellow, then green) and show provider, state, run time and tools.
  - Replaces the earlier simplified resolver. 205 tests, including ports of macOS
    RegressionGuardTests, ClaudeReconcilerTests, SubagentFoldTests and AgentExecutionClockTests.

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
