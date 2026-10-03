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

using Kannu.Core;

namespace Kannu.Core.Tests;

public class HookEventMapperTests
{
    private static HookDecision Decide(string provider, string argState, string hookEvent, string json, string matcherKey = "")
    {
        using var payload = HookPayload.Parse(json);
        return HookEventMapper.Decide(provider, new HookInvocation(argState, provider, hookEvent, matcherKey), hookEvent, payload);
    }

    [Theory]
    [InlineData("UserPromptSubmit", "thinking", RawState.Thinking)]
    [InlineData("PostToolUse", "thinking", RawState.Thinking)]
    [InlineData("PostToolUseFailure", "thinking", RawState.Thinking)]
    [InlineData("PermissionRequest", "awaiting_input", RawState.AwaitingInput)]
    [InlineData("Stop", "stopped", RawState.Stopped)]
    [InlineData("StopFailure", "stopped", RawState.Stopped)]
    [InlineData("SessionStart", "idle", RawState.Idle)]
    public void ClaudeEventsMapToTheirLight(string hookEvent, string argState, RawState expected)
    {
        var d = Decide("claude", argState, hookEvent, "{}");
        Assert.Equal(HookAction.Write, d.Action);
        Assert.Equal(expected, d.State);
    }

    [Fact]
    public void PreToolUseIsGreenForOrdinaryTools() =>
        Assert.Equal(RawState.Executing, Decide("claude", "executing", "PreToolUse", """{"tool_name":"Bash","tool_input":{"command":"ls"}}""").State);

    [Theory]
    [InlineData("AskUserQuestion")]
    [InlineData("ExitPlanMode")]
    [InlineData("ask_user_question")]
    public void PreToolUseIsYellowForToolsThatWaitOnTheUser(string tool) =>
        Assert.Equal(RawState.AwaitingInput, Decide("claude", "executing", "PreToolUse", $$"""{"tool_name":"{{tool}}"}""").State);

    [Fact]
    public void PreToolUseWithQuestionsInputIsYellow() =>
        Assert.Equal(RawState.AwaitingInput, Decide("claude", "executing", "PreToolUse", """{"tool_name":"mcp__x__ask","tool_input":{"questions":[]}}""").State);

    [Fact]
    public void MatcherGroupStateIsTrustedAsIs() =>
        Assert.Equal(RawState.Stopped, Decide("claude", "stopped", "Notification", """{"notification_type":"agent_completed"}""", "completed").State);

    [Fact]
    public void SessionEndDeletes() => Assert.Equal(HookAction.Delete, Decide("claude", "session_end", "SessionEnd", "{}").Action);

    [Theory]
    [InlineData("permission_prompt", HookAction.Write)]
    [InlineData("elicitation_dialog", HookAction.Write)]
    [InlineData("ToolPermission", HookAction.Write)]
    [InlineData("idle_prompt", HookAction.Ignore)]
    [InlineData("auth_success", HookAction.Ignore)]
    public void UnmatchedNotificationOnlyLightsYellowForAPrompt(string type, HookAction expected) =>
        Assert.Equal(expected, Decide("gemini", "awaiting_input", "Notification", $$"""{"notification_type":"{{type}}"}""").Action);

    [Fact]
    public void CopilotPermissionRequestIsIgnored() =>
        Assert.Equal(HookAction.Ignore, Decide("copilot", "awaiting_input", "PermissionRequest", "{}").Action);

    [Theory]
    [InlineData("beforeSubmitPrompt", "thinking", RawState.Thinking)]
    [InlineData("afterAgentThought", "thinking", RawState.Thinking)]
    [InlineData("beforeShellExecution", "executing", RawState.Executing)]
    [InlineData("postToolUse", "thinking", RawState.Thinking)]
    [InlineData("stop", "stopped", RawState.Stopped)]
    public void CursorEvents(string hookEvent, string argState, RawState expected) =>
        Assert.Equal(expected, Decide("cursor", argState, hookEvent, "{}").State);

    [Theory]
    [InlineData("""{"tool_name":"WebSearch"}""", RawState.AwaitingInput)]
    [InlineData("""{"tool_input":{"command":"rm -rf"}}""", RawState.AwaitingInput)]
    [InlineData("""{"text":"done"}""", RawState.Executing)]
    public void CursorAgentResponseIsYellowOnlyForAGatedProposal(string json, RawState expected) =>
        Assert.Equal(expected, Decide("cursor", "executing", "afterAgentResponse", json).State);

    [Theory]
    [InlineData("BeforeAgent", RawState.Thinking)]
    [InlineData("BeforeTool", RawState.Executing)]
    [InlineData("AfterTool", RawState.Thinking)]
    [InlineData("AfterAgent", RawState.Stopped)]
    public void GeminiEvents(string hookEvent, RawState expected) =>
        Assert.Equal(expected, Decide("gemini", "thinking", hookEvent, "{}").State);

    [Theory]
    [InlineData("""{"terminationReason":"RESOURCE_EXHAUSTED"}""", RawState.QuotaExceeded)]
    [InlineData("""{"error":"Rate limit reached"}""", RawState.QuotaExceeded)]
    [InlineData("""{"error":"network"}""", RawState.Stopped)]
    public void AntigravityQuotaStop(string json, RawState expected) =>
        Assert.Equal(expected, Decide("antigravity", "stopped", "Stop", json).State);

    [Fact]
    public void UnmappedEventKeepsTheInstallersState() =>
        Assert.Equal(RawState.Thinking, Decide("opencode", "thinking", "PermissionReplied", "{}").State);

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    public void MalformedPayloadStillMovesTheLightByTheInstallersState(string json) =>
        Assert.Equal(RawState.Stopped, Decide("claude", "stopped", "Stop", json).State);

    [Fact]
    public void ToolNameIsInferredFromItsInput()
    {
        using var payload = HookPayload.Parse("""{"tool_input":{"url":"https://x"}}""");
        Assert.Equal("WebFetch", payload.Tool);
    }
}
