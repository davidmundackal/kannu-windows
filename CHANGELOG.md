# Changelog

Each commit must add one new entry under `## [Unreleased]` before committing.

## [Unreleased]

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
