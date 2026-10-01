# Status file contract

Kannu for Windows reads and writes the same status files as Kannu for macOS. Keep the two in step:
a field renamed here is a field the other app stops understanding.

## Where

`%USERPROFILE%\.kannu\agent-status\` (macOS: `~/.kannu/agent-status/`). The hook honours
`KANNU_STATUS_DIR` for tests.

One file per session: `{provider}-{sessionId}.json`. Both ids are reduced to `[A-Za-z0-9_-]` and capped at
64 characters, so hook input cannot escape the directory. Files starting with `.` are not sessions
(temp files, the lock).

## Shape

```json
{"state":"awaiting_input","ts":1790869122525,"provider":"claude","hook_event":"Notification","project":"kannu","cwd":"C:\\src\\kannu"}
```

| Field        | Meaning                                                         |
|--------------|-----------------------------------------------------------------|
| `state`      | `idle`, `thinking`, `executing`, `awaiting_input`, `stopped`, `quota_exceeded` |
| `ts`         | Unix ms of the write that set `state`                           |
| `provider`   | `claude` (more later)                                           |
| `hook_event` | The event that wrote it; scopes the race merge                  |
| `project`, `cwd`, `name` | Optional labels                                     |

## Writing

- Atomic: write a temp file in the same directory, rename over the target.
- Serialised: every read-modify-write holds `.kannu-status.lock` (never deleted). If the lock is not
  had within 1.5 s the hook goes ahead unlocked: a lost update beats a hung agent.
- Merge: within 2 s of the existing write, and for the same `hook_event` (or an existing
  `PermissionRequest` yellow), the higher-priority state is kept with its original `ts`.
  Priority: `quota_exceeded` > `awaiting_input` > `stopped` > `executing` > `thinking` > `idle`.
- `SessionEnd` deletes the file.
- The hook never prints anything and always exits 0.

## Reading

Status files are untrusted: any process running as the user can write one. Readers cap the size
(64 KB), tolerate any malformed content, and open files with read/write/delete sharing so they never
block a hook's rename.

| Raw state                    | Light  | Until                                   |
|------------------------------|--------|-----------------------------------------|
| `thinking`, `executing`      | Green  | 6 min with no event, then dim           |
| `awaiting_input`             | Yellow | 5 min, then dim                         |
| `stopped`, `quota_exceeded`  | Red    | 10 min, then hidden                     |
| `idle`, stale, unknown       | Dim    | 10 min after `ts`, then hidden          |
