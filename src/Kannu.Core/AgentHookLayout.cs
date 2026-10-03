// Kannu for Windows
// Copyright (C) 2026 Kannu Contributors
//
// This program is free software: you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation, either version 3 of the
// License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without
// even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
// General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with this program. If
// not, see <https://www.gnu.org/licenses/>.

namespace Kannu.Core;

/// <summary>Every agent Kannu installs hooks for. The id is the provider name hooks write.</summary>
public enum AgentProvider
{
    Cursor,
    VSCode,
    Codex,
    Claude,
    Antigravity,
    Gemini,
    Qwen,
    Opencode,
}

public static class AgentProviderInfo
{
    public static string Id(this AgentProvider provider) => provider switch
    {
        AgentProvider.VSCode => "vscode",
        _ => provider.ToString().ToLowerInvariant(),
    };

    public static string DisplayName(this AgentProvider provider) => provider switch
    {
        AgentProvider.Cursor => "Cursor",
        AgentProvider.VSCode => "VS Code and Copilot CLI",
        AgentProvider.Codex => "Codex CLI",
        AgentProvider.Claude => "Claude Code",
        AgentProvider.Antigravity => "Antigravity",
        AgentProvider.Gemini => "Gemini CLI",
        AgentProvider.Qwen => "Qwen Code",
        AgentProvider.Opencode => "opencode",
        _ => provider.ToString(),
    };
}

/// <summary>One hook registration: the event, an optional matcher, and the state the hook is told.</summary>
/// <param name="MatcherKey">Passed to the hook only for a matcher-scoped group, whose state is then trusted.</param>
public sealed record HookEntry(string Event, string State, string? Matcher = null, string MatcherKey = "");

/// <summary>
/// Where each provider's hook lives and what it registers. Install, uninstall and the installed check
/// all read this one table — on macOS, while each kept its own list they drifted apart twice
/// (docs/REGRESSIONS.md entry 6). A new provider is one more case here.
/// </summary>
public sealed class AgentHookLayout(string home)
{
    public enum Shape
    {
        /// <summary>Cursor: <c>hooks.&lt;event&gt;</c> is a list of <c>{"command": …}</c>.</summary>
        FlatEntries,

        /// <summary>Claude, Codex, Antigravity, Gemini, Qwen: a list of <c>{"matcher"?, "hooks": [{…}]}</c>.</summary>
        MatcherGroups,

        /// <summary>A file that is entirely Kannu's, deleted on uninstall (Copilot's hook file, opencode's plugin).</summary>
        OwnFile,
    }

    public enum WritePolicy
    {
        /// <summary>Install writes it, creating it when needed.</summary>
        Always,

        /// <summary>Install merges into it only when it already exists; uninstall still strips it.</summary>
        OnlyIfPresent,
    }

    public sealed record ConfigFile(string Path, Shape Shape, WritePolicy Write);

    /// <param name="SharedSettings">Settings install turns on and uninstall leaves alone (Codex's <c>features.hooks</c>).</param>
    public sealed record Files(IReadOnlyList<ConfigFile> Configs, IReadOnlyList<string> SharedSettings);

    /// <summary>Kannu's name on its Gemini CLI handlers (Gemini shows it while a hook runs).</summary>
    public const string HandlerName = "kannu-agent-status";

    public string Home { get; } = home;

    private string At(params string[] parts) => Path.Combine([Home, .. parts]);

    public string ClaudeSettings => At(".claude", "settings.json");
    public string CursorHooks => At(".cursor", "hooks.json");
    /// <summary>Copilot loads every JSON file in <c>~/.copilot/hooks</c>, so Kannu's is a file of its own.</summary>
    public string CopilotHookFile => At(".copilot", "hooks", "kannu-agent-status.json");
    public string CodexHooks => At(".codex", "hooks.json");
    public string CodexConfigToml => At(".codex", "config.toml");
    /// <summary>Antigravity reads whichever of its three hook files exists; this documented one is always written.</summary>
    public string AntigravityConfigHooks => At(".gemini", "config", "hooks.json");
    public string AntigravityIdeHooks => At(".gemini", "antigravity-ide", "hooks.json");
    public string AntigravityRootHooks => At(".gemini", "hooks.json");
    /// <summary>Gemini CLI shares <c>~/.gemini</c> with Antigravity but reads its hooks from settings.json.</summary>
    public string GeminiSettings => At(".gemini", "settings.json");
    public string QwenSettings => At(".qwen", "settings.json");
    public string OpencodePlugin => At(".config", "opencode", "plugins", OpencodePluginSource.FileName);

    public Files For(AgentProvider provider) => provider switch
    {
        AgentProvider.Cursor => new([new(CursorHooks, Shape.FlatEntries, WritePolicy.Always)], []),
        AgentProvider.VSCode => new([new(CopilotHookFile, Shape.OwnFile, WritePolicy.Always)], []),
        AgentProvider.Codex => new([new(CodexHooks, Shape.MatcherGroups, WritePolicy.Always)], [CodexConfigToml]),
        AgentProvider.Claude => new([new(ClaudeSettings, Shape.MatcherGroups, WritePolicy.Always)], []),
        AgentProvider.Antigravity => new(
        [
            new(AntigravityConfigHooks, Shape.MatcherGroups, WritePolicy.Always),
            new(AntigravityIdeHooks, Shape.MatcherGroups, WritePolicy.OnlyIfPresent),
            new(AntigravityRootHooks, Shape.MatcherGroups, WritePolicy.OnlyIfPresent),
        ], []),
        AgentProvider.Gemini => new([new(GeminiSettings, Shape.MatcherGroups, WritePolicy.Always)], []),
        AgentProvider.Qwen => new([new(QwenSettings, Shape.MatcherGroups, WritePolicy.Always)], []),
        AgentProvider.Opencode => new([new(OpencodePlugin, Shape.OwnFile, WritePolicy.Always)], []),
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    /// <summary>
    /// Whether the tool has ever run here. Only the opt-in CLIs are checked: installing for a tool that
    /// is not there would create its folder. <c>~/.gemini</c> alone means little — Antigravity makes it
    /// too — so Gemini CLI needs a file of its own.
    /// </summary>
    public bool ToolIsPresent(AgentProvider provider) => provider switch
    {
        AgentProvider.Gemini => new[] { At(".gemini", "settings.json"), At(".gemini", "projects.json"), At(".gemini", "tmp"), At(".gemini", "oauth_creds.json") }
            .Any(p => File.Exists(p) || Directory.Exists(p)),
        AgentProvider.Qwen => Directory.Exists(At(".qwen")),
        AgentProvider.Opencode => Directory.Exists(At(".config", "opencode")) || Directory.Exists(At(".local", "share", "opencode")),
        _ => true,
    };

    // ---- Events each provider registers (the state is the hook's fallback for an unmapped event) ----

    /// <summary>VS Code Copilot and Codex: Claude-compatible names. Codex validates strictly, so it keeps exactly this list.</summary>
    public static readonly HookEntry[] ClaudeStyleEvents =
    [
        // idle, not thinking: opening a session must not paint the green "running" light.
        new("SessionStart", "idle"),
        new("UserPromptSubmit", "thinking"),
        new("PreToolUse", "executing"),
        new("PostToolUse", "thinking"),
        new("PermissionRequest", "awaiting_input"),
        new("Stop", "stopped"),
    ];

    /// <summary>
    /// Claude Code: matcher-scoped groups reach states nothing else does — <c>Notification/agent_completed</c>
    /// is the real "done", and matching PreToolUse on the plan/question tools is the only "approve this".
    /// </summary>
    public static readonly HookEntry[] ClaudeEvents =
    [
        new("SessionStart", "idle"),
        new("UserPromptSubmit", "thinking"),
        new("PreToolUse", "awaiting_input", "ExitPlanMode|AskUserQuestion", "gated"),
        new("PreToolUse", "executing"),
        new("PostToolUse", "thinking"),
        // Counted into tool_errors for diagnostics only; the verdict comes from StopFailure.
        new("PostToolUseFailure", "thinking"),
        new("PermissionRequest", "awaiting_input"),
        new("Notification", "stopped", "agent_completed", "completed"),
        new("Notification", "awaiting_input", "permission_prompt|idle_prompt|agent_needs_input", "needs_input"),
        new("Stop", "stopped"),
        new("StopFailure", "stopped"),
        new("SessionEnd", "session_end"),
    ];

    /// <summary>Cursor (lowerCamelCase). <c>afterAgentThought</c> fires while reasoning and between tool calls.</summary>
    public static readonly HookEntry[] CursorEvents =
    [
        new("beforeSubmitPrompt", "thinking"),
        new("afterAgentThought", "thinking"),
        new("afterAgentResponse", "executing"),
        // Fires for every shell command, auto-approved ones too, so it cannot mean "waiting for you".
        new("beforeShellExecution", "executing"),
        new("preToolUse", "executing"),
        new("postToolUse", "thinking"),
        new("postToolUseFailure", "thinking"),
        new("beforeMCPExecution", "executing"),
        new("stop", "stopped"),
    ];

    /// <summary>
    /// Kannu's own <c>~/.copilot/hooks</c> file, read by VS Code and by Copilot CLI. The CLI's yellow comes
    /// from Notification; the hook ignores its PermissionRequest, which fires before the CLI's rules decide.
    /// </summary>
    public static readonly HookEntry[] VSCodeEvents =
        [.. ClaudeStyleEvents, new("Notification", "awaiting_input"), new("SessionEnd", "session_end")];

    public static readonly HookEntry[] AntigravityEvents =
    [
        new("SessionStart", "idle"),
        new("UserPromptSubmit", "thinking"),
        new("PreInvocation", "thinking"),
        new("PostInvocation", "thinking"),
        new("PreToolUse", "executing"),
        new("PostToolUse", "thinking"),
        new("Stop", "stopped"),
    ];

    /// <summary>Gemini CLI. A prompt on screen arrives as Notification with <c>notification_type: "ToolPermission"</c>.</summary>
    public static readonly HookEntry[] GeminiEvents =
    [
        new("SessionStart", "idle"),
        new("BeforeAgent", "thinking"),
        new("BeforeTool", "executing"),
        new("AfterTool", "thinking"),
        new("Notification", "awaiting_input"),
        new("AfterAgent", "stopped"),
        new("SessionEnd", "session_end"),
    ];

    /// <summary>Qwen Code, Claude-style. Its PermissionRequest fires when the dialog is shown.</summary>
    public static readonly HookEntry[] QwenEvents =
        [.. ClaudeStyleEvents, new("Notification", "awaiting_input"), new("SessionEnd", "session_end")];

    public static IReadOnlyList<HookEntry> Events(AgentProvider provider) => provider switch
    {
        AgentProvider.Cursor => CursorEvents,
        AgentProvider.VSCode => VSCodeEvents,
        AgentProvider.Codex => ClaudeStyleEvents,
        AgentProvider.Claude => ClaudeEvents,
        AgentProvider.Antigravity => AntigravityEvents,
        AgentProvider.Gemini => GeminiEvents,
        AgentProvider.Qwen => QwenEvents,
        _ => [],
    };

    /// <summary>
    /// The events whose entries must be present for a provider to count as installed. Claude's is the
    /// core subset on purpose: requiring the whole table would make an older install read as "not
    /// installed" the moment the table grows.
    /// </summary>
    public static IReadOnlyList<string> RequiredEvents(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => ["SessionStart", "UserPromptSubmit", "PreToolUse", "Stop"],
        AgentProvider.Gemini => ["BeforeAgent", "BeforeTool", "AfterAgent"],
        AgentProvider.Qwen => ["UserPromptSubmit", "PreToolUse", "Stop"],
        AgentProvider.VSCode or AgentProvider.Opencode => [],
        _ => Events(provider).Select(e => e.Event).Distinct().ToList(),
    };

    /// <summary>Hook timeout as the provider counts it: Gemini CLI in milliseconds, everyone else in seconds.</summary>
    public static int Timeout(AgentProvider provider) => provider == AgentProvider.Gemini ? 10_000 : 10;
}
