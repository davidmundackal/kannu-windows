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

using System.Text.Json;

namespace Kannu.Core;

public enum HookAction
{
    /// <summary>The event says nothing about the light; leave the status file alone.</summary>
    Ignore,

    /// <summary>Write <see cref="HookDecision.State"/> to the session's status file.</summary>
    Write,

    /// <summary>The session ended: delete its status file.</summary>
    Delete,
}

public sealed record HookDecision(HookAction Action, RawState State)
{
    public static readonly HookDecision Ignore = new(HookAction.Ignore, RawState.Idle);
    public static readonly HookDecision Delete = new(HookAction.Delete, RawState.Idle);

    public static HookDecision Write(RawState state) => new(HookAction.Write, state);
}

/// <summary>
/// One hook event to a light change, for every agent Kannu supports. A port of the event table in
/// the macOS hook script (v43). Keyed by event name — each agent CLI's own vocabulary — so a new
/// provider adds names here rather than a branch per agent. An event not listed keeps the
/// installer's state for it.
/// </summary>
public static class HookEventMapper
{
    internal static HookDecision Decide(string provider, HookInvocation invocation, string hookEvent, HookPayload payload)
    {
        var state = RawStateWire.Parse(invocation.ArgState) ?? RawState.Thinking;

        // Matcher-scoped group: the installer already picked the state for exactly this case.
        if (invocation.MatcherKey.Length > 0) return HookDecision.Write(state);

        switch (hookEvent)
        {
            // Cursor reports a proposal while its approval card is open; the gated payload is the signal.
            case "afterAgentResponse":
                if (LooksGatedPayload(payload.Tool, payload.ToolInput)) state = RawState.AwaitingInput;
                break;

            case "afterAgentThought" or "PreInvocation":
                state = RawState.Thinking;
                break;

            case "preToolUse" or "beforeMCPExecution" or "PreToolUse":
                // Questions and plans to approve are waits on the user. Everything else (including
                // Cursor's WebSearch/WebFetch/Shell, approved before this fires) is running.
                state = IsApprovalGatedTool(payload) ? RawState.AwaitingInput : RawState.Executing;
                break;

            case "beforeShellExecution":
                // Fires for auto-approved commands too, so it means "running", not "waiting".
                state = RawState.Executing;
                break;

            case "PermissionRequest" when provider == "copilot":
                // Copilot CLI fires this before its own rules decide; its Notification says when a
                // prompt is really on screen.
                return HookDecision.Ignore;

            case "PermissionRequest":
                state = RawState.AwaitingInput;
                break;

            case "Notification":
                // Without a matcher (VS Code, Copilot CLI, Gemini CLI, Qwen Code), only a prompt on
                // screen is yellow; idle reminders and other notices change nothing.
                var type = payload.Pick("notification_type", "notificationType");
                if (type is not ("ToolPermission" or "permission_prompt" or "elicitation_dialog")) return HookDecision.Ignore;
                state = RawState.AwaitingInput;
                break;

            case "BeforeAgent":
                state = RawState.Thinking;
                break;

            case "BeforeTool":
                state = RawState.Executing;
                break;

            case "AfterTool":
                state = RawState.Thinking;
                break;

            case "AfterAgent":
                state = RawState.Stopped;
                break;

            case "postToolUse" or "postToolUseFailure" or "PostToolUse" or "PostToolUseFailure" or "PostInvocation":
                state = RawState.Thinking;
                break;

            case "stop" or "Stop" or "StopFailure":
                state = IsAntigravityQuotaStop(provider, hookEvent, payload) ? RawState.QuotaExceeded : RawState.Stopped;
                break;

            case "SessionEnd":
                return HookDecision.Delete;
        }
        return HookDecision.Write(state);
    }

    /// <summary>
    /// Questions and plan approvals. Claude runs matcher-scoped and generic PreToolUse groups in
    /// parallel with no ordering, so the generic group must reach the same verdict on its own.
    /// </summary>
    internal static bool IsApprovalGatedTool(HookPayload payload)
    {
        if (Compact(payload.Tool) is "askquestion" or "userquestion" or "askuserquestion" or "exitplanmode") return true;
        return payload.ToolInput.ValueKind == JsonValueKind.Object && payload.ToolInput.TryGetProperty("questions", out _);
    }

    internal static bool RequiresApproval(string name)
    {
        var lower = name.ToLowerInvariant();
        return Compact(name) is "websearch" or "webfetch" or "search" or "askquestion" or "userquestion"
                   or "shell" or "runterminalcmd" or "bash"
               || lower is "web_search" or "web_fetch" or "ask_question" or "run_terminal_cmd";
    }

    internal static bool LooksGatedPayload(string name, JsonElement input) =>
        RequiresApproval(name)
        || HookPayload.HasAny(input, "questions", "search_term", "searchTerm", "query", "url", "uri",
            "command", "working_directory");

    /// <summary>
    /// Antigravity reports a rate-limited run only through its Stop payload, so a quota stop gets its
    /// own state for the card to name.
    /// </summary>
    private static bool IsAntigravityQuotaStop(string provider, string hookEvent, HookPayload payload)
    {
        if (provider != "antigravity" || hookEvent != "Stop") return false;
        var signal = (payload.Pick("terminationReason", "termination_reason") + " " + payload.Pick("error")).ToLowerInvariant();
        return signal.Contains("quota") || signal.Contains("rate_limit") || signal.Contains("rate limit")
               || signal.Contains("resource_exhausted");
    }

    internal static string Compact(string value) =>
        value.Trim().ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(" ", "");
}
