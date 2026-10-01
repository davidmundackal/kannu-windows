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

public sealed record HookDecision(HookAction Action, RawState State, string HookEvent, string SessionId, string? Cwd)
{
    public static HookDecision Ignore(string hookEvent, string sessionId) =>
        new(HookAction.Ignore, RawState.Idle, hookEvent, sessionId, null);
}

/// <summary>
/// Turns one hook invocation into a light change. Keyed by the event name, which is what each agent
/// CLI documents; adding a provider means adding its event names here, not a new branch per agent.
/// </summary>
public static class HookEventMapper
{
    /// <param name="provider">Which agent fired the hook (from the installed command line).</param>
    /// <param name="forcedState">
    /// A state the installer already chose for a matcher-scoped hook group (Claude's Notification group
    /// is installed with a matcher that only fires for prompts). Trusted over re-deriving it here.
    /// </param>
    /// <param name="payload">The JSON the agent wrote to the hook's stdin.</param>
    public static HookDecision Map(string provider, string? forcedState, JsonElement payload)
    {
        var hookEvent = PickString(payload, "hook_event_name", "hookEventName", "event") ?? "";
        var sessionId = StatusPaths.SanitizeId(
            PickString(payload, "session_id", "sessionId", "conversation_id", "conversationId", "thread_id"));
        var cwd = PickCwd(payload);

        HookDecision Write(RawState state) => new(HookAction.Write, state, hookEvent, sessionId, cwd);

        if (RawStateWire.Parse(forcedState) is { } forced) return Write(forced);

        switch (hookEvent)
        {
            case "SessionStart":
                // SessionStart also fires for /compact and /resume mid-conversation; seeding idle there
                // would dim a session that is working. Only a real startup opens the card.
                var source = PickString(payload, "source") ?? "";
                return source is "compact" or "resume" ? HookDecision.Ignore(hookEvent, sessionId) : Write(RawState.Idle);

            case "UserPromptSubmit":
            case "PostToolUse":
            case "PostToolUseFailure":
                return Write(RawState.Thinking);

            case "PreToolUse":
                // A question or a plan to approve is a wait on the user, not work.
                return Write(IsApprovalGatedTool(payload) ? RawState.AwaitingInput : RawState.Executing);

            case "PermissionRequest":
                return Write(RawState.AwaitingInput);

            case "Notification":
                // Without a matcher, only a prompt on screen is yellow; idle reminders change nothing.
                var type = PickString(payload, "notification_type", "notificationType") ?? "";
                return type is "permission_prompt" or "elicitation_dialog" or "ToolPermission"
                    ? Write(RawState.AwaitingInput)
                    : HookDecision.Ignore(hookEvent, sessionId);

            case "Stop":
            case "StopFailure":
                return Write(RawState.Stopped);

            case "SessionEnd":
                return new HookDecision(HookAction.Delete, RawState.Idle, hookEvent, sessionId, cwd);

            default:
                return HookDecision.Ignore(hookEvent, sessionId);
        }
    }

    internal static bool IsApprovalGatedTool(JsonElement payload)
    {
        var tool = (PickString(payload, "tool_name", "toolName") ?? "")
            .ToLowerInvariant().Replace("_", "").Replace("-", "");
        if (tool is "askquestion" or "userquestion" or "askuserquestion" or "exitplanmode") return true;
        return payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("tool_input", out var input)
            && input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty("questions", out _);
    }

    private static string? PickCwd(JsonElement payload)
    {
        var cwd = PickString(payload, "cwd");
        if (cwd is null && payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("workspace_roots", out var roots)
            && roots.ValueKind == JsonValueKind.Array && roots.GetArrayLength() > 0
            && roots[0].ValueKind == JsonValueKind.String)
        {
            cwd = roots[0].GetString();
        }
        if (string.IsNullOrWhiteSpace(cwd)) return null;
        cwd = cwd.Replace("file://", "").TrimEnd('/', '\\');
        return cwd.Length is 0 or > 1024 ? null : cwd;
    }

    private static string? PickString(JsonElement payload, params string[] keys)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in keys)
        {
            if (payload.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var s = value.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        return null;
    }
}
