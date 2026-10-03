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

/// <summary>What a session's card shows. Port of macOS <c>AgentTrafficLightState</c>.</summary>
public enum AgentLightState
{
    Executing,
    AwaitingInput,
    Thinking,
    Stopped,
    Inactive,
}

public static class AgentLightStateInfo
{
    /// <summary>
    /// Card order and the aggregate light, as on macOS: a finished run first (red is news), then a
    /// wait on the user, then work.
    /// </summary>
    public static int SortPriority(this AgentLightState state) => state switch
    {
        AgentLightState.Stopped => 6,
        AgentLightState.AwaitingInput => 5,
        AgentLightState.Executing => 4,
        AgentLightState.Thinking => 3,
        _ => 1,
    };

    /// <summary>Thinking, executing or waiting: between a prompt and the stop that answers it.</summary>
    public static bool IsActiveRun(this AgentLightState state) =>
        state is AgentLightState.Thinking or AgentLightState.Executing or AgentLightState.AwaitingInput;

    public static TrafficLight Light(this AgentLightState state) => state switch
    {
        AgentLightState.Executing or AgentLightState.Thinking => TrafficLight.Green,
        AgentLightState.AwaitingInput => TrafficLight.Yellow,
        AgentLightState.Stopped => TrafficLight.Red,
        _ => TrafficLight.Inactive,
    };
}

/// <summary>
/// Why a run ended, when it ended on an error. Only a run-terminating signal becomes one: a tool
/// failure the agent recovered from is a count, never a verdict.
/// </summary>
public sealed record RunError(int Specificity, int? Status)
{
    /// <summary>A run that ended failing without saying why (StopFailure, an Antigravity Stop with an error).</summary>
    public static readonly RunError Failed = new(1, null);

    /// <summary>The model's turn died on the API; <paramref name="status"/> is the HTTP status when known.</summary>
    public static RunError ApiError(int? status) => new(2, status);

    public string Label => Specificity == 1 ? "failed" : Status switch
    {
        429 => "rate limited (429)",
        529 => "API overloaded (529)",
        401 => "signed out (401)",
        { } code => $"API error {code}",
        null => "API error",
    };

    /// <summary>
    /// <c>own ?? other</c>, refined: when both carry a verdict the more specific wins (a tie keeps own).
    /// Never an OR or a max — the verdict must reset to null every turn.
    /// </summary>
    public static RunError? Preferred(RunError? own, RunError? other) =>
        own is null ? other : other is null ? own : other.Specificity > own.Specificity ? other : own;
}

/// <summary>
/// One request as the hook recorded it: from the prompt to the Stop that answered it. Port of macOS
/// <c>HookTurn</c>.
/// </summary>
public sealed record HookTurn(long StartedMs, long? EndedMs, int ToolCalls, string? TranscriptPath, long? TranscriptOffset)
{
    private const int MaxToolCalls = 99_999;
    private const long FutureSlackMs = 60_000;

    /// <summary>The turn a status file carries (untrusted, re-checked as the hook does); null without a plausible start.</summary>
    public static HookTurn? FromRecord(StatusRecord record, string home, long nowMs)
    {
        if (record.TurnStartedMs is not { } start || start < TurnMetrics.PlausibleMs || start > nowMs + FutureSlackMs) return null;
        long? end = record.TurnEndedMs is { } e && e >= start && e <= nowMs + FutureSlackMs ? e : null;
        var path = TurnMetrics.TranscriptPath(record.TranscriptPath, home);
        return new HookTurn(start, end, Math.Min(record.TurnToolCalls ?? 0, MaxToolCalls),
            path.Length > 0 ? path : null,
            path.Length > 0 ? record.TurnTranscriptOffset : null);
    }

    /// <summary>
    /// The parent's turn with a subagent's calls added — only when the subagent belongs to this turn.
    /// A parent with no turn stays without one: never adopt a subagent's.
    /// </summary>
    public static HookTurn? Folding(HookTurn? sub, HookTurn? parent)
    {
        if (parent is null) return null;
        if (sub is null || sub.StartedMs < parent.StartedMs) return parent;
        return parent with { ToolCalls = Math.Min(parent.ToolCalls + sub.ToolCalls, MaxToolCalls) };
    }
}

/// <summary>
/// One session as the notch shows it, from any source (a hook file or passive detection). Port of
/// macOS <c>AgentSessionStatus</c>. Immutable; every rebuild from another session must go through
/// <see cref="CarryingExtras"/> (macOS docs/REGRESSIONS.md entry 7).
/// </summary>
public sealed record AgentSession
{
    public required string Id { get; init; }
    public required string Provider { get; init; }
    public required string ConversationId { get; init; }
    public string? ChatName { get; init; }
    public string? ProjectName { get; init; }
    public required string RawState { get; init; }
    public required AgentLightState DisplayState { get; init; }
    public required long UpdatedAtMs { get; init; }
    public required bool IsVisible { get; init; }
    public long? ExecutionStartedAtMs { get; init; }

    /// <summary>Full working directory, when known; click-through opens the right project with it.</summary>
    public string? Cwd { get; init; }

    /// <summary>The agent process itself (Claude passive sessions); its parent chain leads to the host window.</summary>
    public int? HostPid { get; init; }

    /// <summary>The program <see cref="HostPid"/> was recorded running; null when the pid is known alive.</summary>
    public string? HostName { get; init; }

    /// <summary>A classic console window the agent runs in (hook sessions, Windows).</summary>
    public long? HostWindow { get; init; }

    /// <summary>Claude Desktop's own id for a Code-tab chat, for click-through.</summary>
    public string? DesktopSessionId { get; init; }

    public int ToolErrorCount { get; init; }
    public bool IsUnattended { get; init; }
    public RunError? RunError { get; init; }
    public HookTurn? Turn { get; init; }

    /// <summary>The source reported work in progress, whatever the staleness ladder later concluded.</summary>
    public bool HasActiveRawState => RawState.ToLowerInvariant() is "executing" or "thinking";

    public AgentSession WithDisplayState(AgentLightState state, bool visible, long? updatedAtMs = null) =>
        this with { DisplayState = state, IsVisible = visible, UpdatedAtMs = updatedAtMs ?? UpdatedAtMs };

    /// <summary>
    /// The same session named by another record's identity: when a host (Cursor) and the engine it
    /// embeds (Claude) report one conversation, the host names the card, whichever side's state won.
    /// </summary>
    public AgentSession AdoptingIdentity(AgentSession identity) =>
        (this with
        {
            Id = identity.Id,
            Provider = identity.Provider,
            ChatName = AgentStateMachine.HasReliableChatName(identity.ChatName) ? identity.ChatName : ChatName,
            ProjectName = ProjectName ?? identity.ProjectName,
            Cwd = Cwd ?? identity.Cwd,
            HostPid = HostPid ?? identity.HostPid,
            HostName = HostPid is null ? identity.HostName : HostName,
            HostWindow = HostWindow ?? identity.HostWindow,
        }).CarryingExtras(identity);

    /// <summary>
    /// Merges the fields one side may know and the other not: the larger error count, the OR of the
    /// unattended flag, the preferred verdict, and <c>self ?? source</c> for locators and the turn.
    /// </summary>
    public AgentSession CarryingExtras(AgentSession source) => this with
    {
        ToolErrorCount = Math.Max(ToolErrorCount, source.ToolErrorCount),
        IsUnattended = IsUnattended || source.IsUnattended,
        RunError = Kannu.Core.RunError.Preferred(RunError, source.RunError),
        DesktopSessionId = DesktopSessionId ?? source.DesktopSessionId,
        Turn = Turn ?? source.Turn,
    };

    public string ProviderLabel => ProviderLabelFor(Provider);

    public static string ProviderLabelFor(string provider) => provider.ToLowerInvariant() switch
    {
        "cursor" => "Cursor",
        "vscode" => "VS Code",
        "codex" => "Codex",
        "claude" => "Claude",
        "antigravity" => "Antigravity",
        "warp" => "Warp",
        "claudedesktop" => "Claude Desktop",
        "copilot" => "Copilot CLI",
        "gemini" => "Gemini CLI",
        "qwen" => "Qwen Code",
        "opencode" => "opencode",
        "" => "Agent",
        _ => char.ToUpperInvariant(provider[0]) + provider[1..],
    };

    public string DisplayChatName => string.IsNullOrWhiteSpace(ChatName) ? "Untitled chat" : ChatName.Trim();

    /// <summary>"Stopped · rate limited (429)": rendered only once the run has stopped.</summary>
    public string RunOutcomeSuffix =>
        DisplayState is AgentLightState.Stopped or AgentLightState.Inactive && RunError is { } error ? " · " + error.Label : "";
}
