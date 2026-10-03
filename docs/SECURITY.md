# Agent Security on Windows

Kannu watches what your AI coding agents read, write and are told, on this PC only. Nothing is sent
anywhere, no model is involved, and a finding never changes the traffic light: a shield appears on
the notch instead, and the details are in **Settings › Security**.

This is a port of macOS Kannu's Agent Security, including its connection to
[ADR](https://github.com/uber/ADR), Uber's Agentic Detection and Response toolkit (see
[ADR](#adr-ubers-agentic-detection-and-response) below). ADR's findings are the only ones that come from
another program, and ADR Detection is the only part that sends anything off the PC, and only when you
switch it on and pick a chat.

## The checks

| Check | Where it runs | What it looks for | Default |
|---|---|---|---|
| Hidden text | Kannu's hook, every event | Unicode tag characters, variation-selector runs, text-direction controls on a line with no right-to-left letters, long zero-width runs, in prompts, tool results and replies | On |
| Secrets | Kannu's hook, prompts and tool calls | API keys and tokens (Anthropic, OpenAI, AWS, GitHub, GitLab, Slack, Stripe, Google, npm, Hugging Face) and private keys. Only the kind, the prefix, the length and a 12-hex fingerprint are kept; never the secret | On |
| Sensitive files | Kannu's hook, after a tool ran | SSH and GPG keys, cloud and agent credentials, Windows credential stores, password managers, browser profiles, shell history, `.env` files; and changes to startup items, shell profiles and agent settings | On |
| Bypassed permission checks | Kannu's hook | A session launched with permission checks off (`bypassPermissions`, `--dangerously-skip-permissions`, `yolo`, …) | Always |
| New MCP servers | Kannu, every minute | A server added to an agent's MCP settings since Kannu last looked. The first look only learns what is there | On |
| Agent policy | Kannu's hook, before a tool runs | Commands or tools you listed in your policy file | Report only |

The hook reads which checks are on from marker files in `%USERPROFILE%\.kannu\agent-status`
(`.kannu-hidden-text-off`, `.kannu-secrets-off`, `.kannu-sensitive-paths-off`,
`.kannu-hidden-text-warn-agent`, `.kannu-policy-enforce`), the same files macOS Kannu uses.

## Agent policy

Create `%USERPROFILE%\.kannu\agent-policy.json`:

```json
{
  "version": 1,
  "block": [
    { "command": "git push --force", "reason": "Force pushes need me." },
    { "command": "Remove-Item" },
    { "tool": "WebFetch" }
  ]
}
```

- A **command** rule matches when a command (or any part of a chained command, after `sudo`, `env`,
  `cmd /c`, `powershell -Command`, `bash -c` and similar wrappers) starts with its words.
- A **tool** rule matches the tool's exact name.
- Every match is a finding. With **Refuse calls that match** on, Claude Code and Cursor are told no
  before the call runs, with your reason; other agents' matches are reported only.
- A broken policy file (bad JSON, over 64 KB, more than 200 rules, or a link) means no rules: an
  agent is never blocked by a mistake in the file.

## What you can do with a finding

**Acknowledge** hides it until it gets worse (a higher severity, or a policy match that was refused
now ran). **Snooze 24 h** hides it for a day. **Show hidden again** brings everything back.
**Copy for agent** copies the title and evidence, never the chat name or decoded hidden text.

## Notifications

High findings can go to Windows notifications and your phone (Settings › Notifications), once per
finding. The notification says only the title and severity; the details stay on this PC.

## ADR (Uber's Agentic Detection and Response)

ADR is a separate install you own (Apache-2.0). Kannu never installs it, never edits your MCP
configurations, and includes no ADR code: it runs the copy you installed and reads what it reports.
Settings › Security › **ADR** shows whether it is installed, the last scan and any error, and has
**Copy install command**. Kannu never runs the command itself.

### Install ADR Discovery (once)

ADR needs Python 3.11 or newer, and `uv` brings its own interpreter. In a terminal:

```powershell
uv tool install "adr-discovery @ git+https://github.com/uber/ADR#subdirectory=Discovery"
```

pipx also works: `pipx install "git+https://github.com/uber/ADR#subdirectory=Discovery"`. Then press
**Check again**. Kannu looks for `adr-discovery.exe` in these places, in order:

1. The **ADR tools folder**, if you set one.
2. `%USERPROFILE%\.local\bin`, where uv and pipx put tools on Windows.
3. `PIPX_BIN_DIR`.
4. `%APPDATA%\uv\tools\adr-discovery\Scripts`.
5. Your `PATH`.

It finds `uv.exe` the same way, and also checks the cargo and winget link folders. Kannu reads the
installed version with `uv tool list`, because `adr-discovery` has no `--version` flag.

### When Kannu runs it

**Let Kannu run scans** (on by default, does nothing until ADR is installed). Kannu checks once a
minute whether a scan is due, and runs one in these cases, the same as on macOS:

- when Kannu has never run one;
- 24 hours after Kannu's last scan;
- when the MCP servers declared in an agent's settings change, at most once every five minutes. Kannu
  compares server names, not file dates. It reads the same files as the **New MCP servers** check;
- after a scan that produced no snapshot: again after 1, 2, 4, 8 and 16 hours, and never later than
  the daily scan. While scans keep failing, a settings change waits for that retry too;
- when you press **Scan now** on the Security page.

A scan is stopped after 180 seconds. It runs in the background with no console window.

**How Kannu runs it differs from macOS.** On macOS Kannu runs `adr-discovery --json --output-dir
<folder>` and ADR writes the snapshot itself. On Windows ADR's own write fails: `cli.py`
`_write_private` opens the output folder with `os.open`, and Windows Python cannot open a folder that
way. So Kannu runs:

```
adr-discovery --json --dry-run [--policy <file>]
```

`--dry-run` makes ADR print the snapshot and write nothing. Kannu checks that the output is a schema-1.x
snapshot and writes it as `snapshot-<timestamp>.json` (the name ADR would have used) into
`%USERPROFILE%\.kannu\adr\discovery`. It never overwrites a file. The arguments are fixed in code and
pinned by tests. Kannu never passes `--root`, `--diff`, `--explain` or `--output-dir`.

Kannu reads the newest `snapshot-*.json` in that folder whenever it changes, within a minute, so a
snapshot from your own scheduler is picked up too. Turn the toggle off if something else already runs
ADR. **ADR scan policy** passes your tenant policy file to every scan (`tenant_domains`, `approved`,
`forbidden`; see macOS Kannu's `docs/ADR.md` §6). A policy file ADR cannot read fails every scan.

Each ADR finding gets an id, severity, title and group exactly as on macOS: one row per asset and rule.
Acknowledge, snooze and notifications work on them like on any other finding. A notification says only
"reported by ADR Discovery" and the severity.

### What ADR Discovery sees on Windows, and what it misses

ADR's Windows support is young, and upstream says so: "there is no Windows driver"
(`Discovery/tests/README.md`, `Discovery/adr_discovery/README.md`). Reading the source at commit
`d967665` shows the following:

- **Works:** `world/platform/windows.py` lists running processes with their command lines (CIM
  `Win32_Process`), TCP connections (`Get-NetTCPConnection`), and installed apps from the Uninstall
  registry keys (HKLM, HKCU, WOW6432Node) and `Get-AppxPackage`.
- **File checks do not work on Windows yet (upstream):**
  - The scan root `/` becomes `C:\` on Windows (`world/gate.py`, `os.path.realpath`). Every path is
    then compared against `C:\` plus another separator, so every file read is refused as
    `outside_root`.
  - Listing a folder uses `os.open` on the folder and `os.scandir(fd)`. Windows Python supports
    neither.
  - So ADR reads no MCP configuration, app state, extension folder or browser profile. The findings
    built from them cannot appear: unpinned MCP server, MCP server over plain HTTP, MCP server reaches
    outside your domains.
- **Windows folders are missing from ADR's lists anyway:**
  - `enumerator/markers.py` (state folders, config files, browser profiles) and
    `catalog/catalog.json` (Claude Desktop's state folder) list only `~/Library/Application Support/…`
    and `~/.config/…` paths. There is no `%APPDATA%\Claude`, `%APPDATA%\Code\User`,
    `%APPDATA%\Cursor\User` or `%LOCALAPPDATA%` browser profile.
  - Homes are looked for under `/Users` only, and the fallback reads `HOME`, which Windows does not set.
  - `PATH` is split on `:` (`enumerator/sources/binaries.py`), which breaks `C:\…` entries.
- **No way to add them:** ADR's command line has no extra-roots option (`--root` scans a fixture tree
  instead of the PC), and the policy file only takes `approved`, `forbidden` and `tenant_domains`. Kannu
  does not patch ADR.

So a Windows snapshot is an inventory of processes, connections and installed apps, usually with few or
no findings. ADR exits with code 2 and lists the folders it could not read. Settings shows this as
**partial coverage** with the number of gaps, never as a clean result. Kannu's own checks above (hidden
text, secrets, sensitive files, new MCP servers, agent policy) cover the MCP configuration that ADR
cannot read on Windows.

### ADR Detection: analyze a finished chat (opt-in, off by default)

ADR Detection judges a finished chat's transcript. It runs a local hidden-Unicode check, then a Claude
reasoning agent that runs as an unattended `claude -p` session on your PC with three local MCP context
servers. It is Uber's research tool, and Kannu runs it only when you ask.

Setup, all yours:

```powershell
git clone https://github.com/uber/ADR; cd ADR\Detection; uv sync   # Python 3.10–3.12
```

You also need **Claude Code installed with the native installer** and signed in. Detection starts
`claude` without a shell, so Windows finds only `claude.exe`. npm's `claude.cmd` is not found, and
Settings tells you when that is all there is.

Then turn on **Analyze chats with ADR Detection** (a confirmation names what leaves the PC), choose the
`Detection` folder, and use **Analyze a finished chat…**. It lists your 10 newest Claude Code chats
(`%USERPROFILE%\.claude\projects`), and every run asks first. Kannu writes its own adapter script (GPL,
the same file as macOS Kannu's `scripts/adr-analyze-session.py`) to `%USERPROFILE%\.kannu\adr\detection\`
and runs:

```
uv run --project <Detection folder> python <adapter> --transcript <chat> --report <report> --triage off …
```

The environment is limited to what uv, Python and Claude Code need. No API key is passed: the run uses
your Claude login. The run is stopped after the reasoning timeout plus two minutes, 7 minutes by
default.

**Results:** A malicious verdict becomes a finding: high when confidence is 0.8 or more, medium
otherwise. Reports are kept in `%USERPROFILE%\.kannu\adr\detection\`. A clean verdict is recorded and
shown in the ADR card, but it is not a finding.

**What leaves the PC:** the chosen transcript's text, sent to Anthropic by Claude Code under your login.
Nothing else, nothing automatic, nothing without your click. Triage with OpenAI, available on macOS, is
not offered on Windows.

**Windows limit:** Detection passes the whole transcript to `claude` as one command-line argument, and
Windows limits a command line to 32,767 characters. On Windows the adapter therefore keeps at most the
newest **18,000 characters** of the chat (macOS: 150,000), removing the oldest tool results first. A chat
whose own text still does not fit fails with an error that says so. It never yields a clean verdict.

## Where things are kept

- What the hook saw: in each chat's status file, while the chat lives.
- Findings, acknowledgements, snoozes and the MCP servers Kannu has seen:
  `%APPDATA%\Kannu\security.json`. Turning a check off forgets what it saw. The last ADR scan record,
  its failure count and ADR Detection verdicts are kept there as well.
- ADR Discovery snapshots: `%USERPROFILE%\.kannu\adr\discovery`. ADR Detection's adapter and reports:
  `%USERPROFILE%\.kannu\adr\detection`.
