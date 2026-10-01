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
using Kannu.Core;

namespace Kannu.Core.Tests;

public class HookEventMapperTests
{
    private static HookDecision Map(string json, string? forced = null)
    {
        using var doc = JsonDocument.Parse(json);
        return HookEventMapper.Map("claude", forced, doc.RootElement);
    }

    [Theory]
    [InlineData("UserPromptSubmit", RawState.Thinking)]
    [InlineData("PostToolUse", RawState.Thinking)]
    [InlineData("PostToolUseFailure", RawState.Thinking)]
    [InlineData("PermissionRequest", RawState.AwaitingInput)]
    [InlineData("Stop", RawState.Stopped)]
    [InlineData("StopFailure", RawState.Stopped)]
    public void ClaudeEventsMapToTheirLight(string hookEvent, RawState expected)
    {
        var decision = Map($$"""{"hook_event_name":"{{hookEvent}}","session_id":"abc"}""");
        Assert.Equal(HookAction.Write, decision.Action);
        Assert.Equal(expected, decision.State);
        Assert.Equal("abc", decision.SessionId);
    }

    [Fact]
    public void PreToolUseIsGreenForOrdinaryTools()
    {
        var d = Map("""{"hook_event_name":"PreToolUse","session_id":"s","tool_name":"Bash","tool_input":{"command":"ls"}}""");
        Assert.Equal(RawState.Executing, d.State);
    }

    [Theory]
    [InlineData("AskUserQuestion")]
    [InlineData("ExitPlanMode")]
    [InlineData("ask_user_question")]
    public void PreToolUseIsYellowForToolsThatWaitOnTheUser(string tool)
    {
        var d = Map($$"""{"hook_event_name":"PreToolUse","session_id":"s","tool_name":"{{tool}}"}""");
        Assert.Equal(RawState.AwaitingInput, d.State);
    }

    [Fact]
    public void PreToolUseWithQuestionsInputIsYellow()
    {
        var d = Map("""{"hook_event_name":"PreToolUse","session_id":"s","tool_name":"mcp__x__ask","tool_input":{"questions":[]}}""");
        Assert.Equal(RawState.AwaitingInput, d.State);
    }

    [Theory]
    [InlineData("startup", HookAction.Write)]
    [InlineData("clear", HookAction.Write)]
    [InlineData("compact", HookAction.Ignore)]
    [InlineData("resume", HookAction.Ignore)]
    public void SessionStartOnlySeedsIdleOnARealStart(string source, HookAction expected)
    {
        var d = Map($$"""{"hook_event_name":"SessionStart","session_id":"s","source":"{{source}}"}""");
        Assert.Equal(expected, d.Action);
        if (expected == HookAction.Write) Assert.Equal(RawState.Idle, d.State);
    }

    [Fact]
    public void SessionEndDeletes()
    {
        Assert.Equal(HookAction.Delete, Map("""{"hook_event_name":"SessionEnd","session_id":"s"}""").Action);
    }

    [Theory]
    [InlineData("permission_prompt", HookAction.Write)]
    [InlineData("elicitation_dialog", HookAction.Write)]
    [InlineData("idle_prompt", HookAction.Ignore)]
    [InlineData("auth_success", HookAction.Ignore)]
    public void UnmatchedNotificationOnlyLightsYellowForAPrompt(string type, HookAction expected)
    {
        var d = Map($$"""{"hook_event_name":"Notification","session_id":"s","notification_type":"{{type}}"}""");
        Assert.Equal(expected, d.Action);
    }

    [Fact]
    public void ForcedStateFromAMatcherGroupWins()
    {
        var d = Map("""{"hook_event_name":"Notification","session_id":"s","message":"Claude needs your permission"}""", "awaiting_input");
        Assert.Equal(HookAction.Write, d.Action);
        Assert.Equal(RawState.AwaitingInput, d.State);
    }

    [Theory]
    [InlineData("SubagentStop")]
    [InlineData("")]
    [InlineData("SomethingNew")]
    public void UnknownEventsChangeNothing(string hookEvent)
    {
        Assert.Equal(HookAction.Ignore, Map($$"""{"hook_event_name":"{{hookEvent}}","session_id":"s"}""").Action);
    }

    [Fact]
    public void HostileSessionIdCannotEscapeTheStatusDirectory()
    {
        var d = Map("""{"hook_event_name":"Stop","session_id":"..\\..\\Windows\\evil/../x"}""");
        Assert.Equal("Windowsevilx", d.SessionId);
        Assert.Equal("claude-Windowsevilx.json", StatusPaths.StatusFileName("claude", d.SessionId));
    }

    [Fact]
    public void OversizedSessionIdIsCapped()
    {
        var d = Map($$"""{"hook_event_name":"Stop","session_id":"{{new string('a', 5000)}}"}""");
        Assert.Equal(64, d.SessionId.Length);
    }

    [Fact]
    public void MissingSessionIdFallsBackToDefault()
    {
        Assert.Equal("default", Map("""{"hook_event_name":"Stop"}""").SessionId);
    }

    [Fact]
    public void NonObjectPayloadIsIgnored()
    {
        Assert.Equal(HookAction.Ignore, Map("[1,2,3]").Action);
    }

    [Fact]
    public void WindowsCwdIsCarried()
    {
        var d = Map("""{"hook_event_name":"Stop","session_id":"s","cwd":"C:\\Users\\me\\src\\kannu\\"}""");
        Assert.Equal(@"C:\Users\me\src\kannu", d.Cwd);
        Assert.Equal("kannu", StatusPaths.ProjectName(d.Cwd));
    }
}
