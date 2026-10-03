# Agent Security on Windows

Kannu watches what your AI coding agents read, write and are told, on this PC only. Nothing is sent
anywhere, no model is involved, and a finding never changes the traffic light: a shield appears on
the notch instead, and the details are in **Settings › Security**.

This is a port of macOS Kannu's Agent Security. ADR (Uber's Agent Detection & Response toolkit)
is not part of it: it has no Windows build to run (decision D5).

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

## Where things are kept

- What the hook saw: in each chat's status file, while the chat lives.
- Findings, acknowledgements, snoozes and the MCP servers Kannu has seen:
  `%APPDATA%\Kannu\security.json`. Turning a check off forgets what it saw.
