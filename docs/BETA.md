# Kannu for Windows — beta guide

Thank you for trying Kannu for Windows. Kannu watches the AI coding agents on your PC (Claude Code,
Cursor, VS Code Copilot and Copilot CLI, Codex CLI, Antigravity, Gemini CLI, Qwen Code, opencode) and
shows what they are doing in a small notch at the top of your screen:

- **Green:** an agent is working
- **Yellow:** an agent needs you (a question or an approval)
- **Red:** an agent finished or stopped

This is a beta. Things will be rough in places, and your reports are what makes it better.

## What you need

- Windows 10 or Windows 11, 64-bit (x64).
- At least one of the agents above.

## Install

1. Download **`Kannu-win-Setup.exe`** from the newest beta (marked "Pre-release") on the releases page:
   https://github.com/davidmundackal/kannu-windows/releases
2. Run it. The beta is not code-signed yet, so Windows SmartScreen says *"Windows protected your PC"*
   the first time. Click **More info**, then **Run anyway**.
3. Kannu installs for your Windows account only (no administrator prompt). Everything it needs is
   included; nothing else is installed.
4. Kannu shows its **Terms of Use**. Read them and click **Accept** (Decline closes Kannu).
5. Kannu's eye appears in the notification area of the taskbar (you may need to click the **^**
   arrow to see it, and can drag it next to the clock). The notch itself stays hidden until
   something happens.
6. Kannu starts with Windows from now on (Settings › General turns that off). Right-click the eye,
   choose **Settings…**, open **Agents**, and click **Install** next to each
   agent you use. Restart that agent (or start a new session) so it picks the hook up.

Claude Code, Cursor, Warp and Claude Desktop also show up without the hook, with less detail.

## Try these

Please go through this list and tell us what did not happen as described.

1. **Nothing at the top while idle.** With no agent working, nothing should be visible at the top
   of the screen.
2. **It comes out when an agent changes.** Start a task in one of your agents. The notch should
   slide down from the top centre for about 7 seconds, showing a coloured dot and a short summary
   ("1 working"), then slide away. It comes out again when the agent needs you (yellow) and when it
   finishes (red).
3. **Click the eye.** Left-click the Kannu eye in the taskbar. The notch opens and lists your
   sessions: the agent's icon, the chat's name, its state, how long it has run and how many tools it
   used. If you do not move the pointer onto it, it closes again after 3 seconds.
4. **Hover.** While the notch is on screen, move the pointer onto it: it opens and stays open.
   Move away: it closes, stays for a few seconds, then hides.
5. **The top edge does nothing by default.** While the notch is hidden, rest the pointer at the top
   centre of the screen. Nothing should happen.
6. **Settings › Notch.** Try each option:
   - **Floating pill** instead of **Notch** (the notch moves just below the top edge, fully rounded);
   - **Stay hidden until something happens** off (the notch stays visible all the time);
   - **Reveal when the pointer rests at the top of the screen** on (resting there for a second now
     brings the notch out);
   - **Open when the pointer is over the notch** off.
7. **Light and dark.** Switch Windows between light and dark mode (Settings › Personalization ›
   Colors) with Kannu's Settings window open. It should follow, including the accent colour.
8. **Remove a hook.** Settings › Agents › **Remove** for one agent. New sessions of that agent should
   no longer show (unless it is one of the agents Kannu also sees without a hook).

## Not there yet

These are known and on the way, so no need to report them:

- **Agents** is the only tab in the open notch (usage, keep-awake and the rest follow).
- The notch appears on the main display only.
- Clicking a session does not jump to its app yet.
- No mobile or desktop notifications yet.

## Updates

Kannu checks for updates when it starts and once a day, downloads them in the background and installs
them the next time it starts. Beta installs update to the next beta. **Check for Updates…** in the eye's
menu checks right away.

## Reporting a problem

If Kannu crashes or freezes, it offers a report the next time it starts. It shows exactly what would be
shared (with your user name, PC name and profile folder taken out); click **Report on GitHub** to send
it. For anything else, Settings › About › **Report a problem** opens an issue with your Kannu and
Windows versions filled in, and **Logs › Export…** saves logs you can attach.

Open an issue at https://github.com/davidmundackal/kannu-windows/issues/new and include:

- your Windows version (Settings › System › About, "Edition" and "Version");
- Kannu's version (right-click the eye: it is the first line of the menu);
- which agent you were using;
- what you did, what you expected, and what happened instead;
- a screenshot or short screen recording if you can.

Found a security problem? Please report it privately instead:
https://github.com/davidmundackal/kannu-windows/security/advisories/new

## Where Kannu keeps things

| What | Where |
|---|---|
| The app | `%LOCALAPPDATA%\Kannu` |
| Your settings | `%APPDATA%\Kannu\settings.json` |
| Agent status files | `%USERPROFILE%\.kannu\agent-status` |
| The hook agents run | `%LOCALAPPDATA%\Kannu\bin\kannu-hook.exe` |
| Logs and problem reports | `%LOCALAPPDATA%\Kannu\logs` |

When Kannu adds its hook to an agent's settings file, it keeps the previous version beside it as
`<name>.kannu-backup`.

## Uninstall

Windows Settings › Apps › Installed apps › **Kannu** › Uninstall. Uninstalling also removes Kannu's
hooks from every agent. Your settings file in `%APPDATA%\Kannu` is left behind; delete that folder if
you want it gone too.
