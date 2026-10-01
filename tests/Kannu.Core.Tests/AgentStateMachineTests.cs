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
using M = Kannu.Core.AgentStateMachine;

namespace Kannu.Core.Tests;

internal static class Sessions
{
    public static AgentSession Make(string provider = "claude", string conversation = "conv-1", string? chatName = null,
        string? projectName = null, string rawState = "executing", AgentLightState display = AgentLightState.Executing,
        long updatedAt = 1_000_000, bool visible = true, string? cwd = null, int? hostPid = null, int toolErrors = 0,
        bool unattended = false, RunError? runError = null, string? desktopSessionId = null, HookTurn? turn = null) => new()
    {
        Id = $"{provider}-{conversation}",
        Provider = provider,
        ConversationId = conversation,
        ChatName = chatName,
        ProjectName = projectName,
        RawState = rawState,
        DisplayState = display,
        UpdatedAtMs = updatedAt,
        IsVisible = visible,
        Cwd = cwd,
        HostPid = hostPid,
        ToolErrorCount = toolErrors,
        IsUnattended = unattended,
        RunError = runError,
        DesktopSessionId = desktopSessionId,
        Turn = turn,
    };
}

/// <summary>Ported from macOS RegressionGuardTests: rules that have broken before.</summary>
public class RegressionGuardTests
{
    // Entry 2: the active window must outlast the longest tool call.
    [Theory]
    [InlineData(120_000)]
    [InlineData(300_000)]
    public void HookOnlyProviderMidToolCallStaysActive(long ageMs)
    {
        var (state, visible) = M.ResolveHookState("executing", ageMs, 5_000, 5_000);
        Assert.True(state.IsActiveRun(), "if this fails, the active staleness default was shortened again");
        Assert.True(visible);
    }

    [Fact]
    public void TheActiveWindowIsSixMinutes() => Assert.Equal(360_000, M.ActiveStaleMs);

    // Entry 12: yellow follows evidence, not the clock.
    [Fact]
    public void HeldAwaitingInputStaysYellowAtOneHour() =>
        Assert.Equal((AgentLightState.AwaitingInput, true), M.ResolveHookState("awaiting_input", 3_600_000, 5_000, 5_000, holdAwaitingInput: true));

    [Fact]
    public void UnheldAwaitingInputExpiresAfterFiveMinutes()
    {
        Assert.Equal((AgentLightState.AwaitingInput, true), M.ResolveHookState("awaiting_input", 300_000, 5_000, 5_000));
        Assert.Equal((AgentLightState.Inactive, false), M.ResolveHookState("awaiting_input", 300_001, 5_000, 5_000));
    }

    [Fact]
    public void AwaitingInputHoldFollowsEvidencePerProvider()
    {
        Assert.True(M.HoldsAwaitingInput("claude", true, ClaudeTailState.ToolInFlight, false));
        foreach (var tail in new ClaudeTailState?[] { ClaudeTailState.Working, ClaudeTailState.TurnFinished, ClaudeTailState.Unknown, null })
        {
            Assert.False(M.HoldsAwaitingInput("claude", true, tail, true));
        }
        Assert.False(M.HoldsAwaitingInput("claude", false, ClaudeTailState.ToolInFlight, false));
        Assert.True(M.HoldsAwaitingInput("cursor", false, null, true));
        Assert.False(M.HoldsAwaitingInput("cursor", false, null, false));
        foreach (var provider in new[] { "vscode", "codex", "antigravity", "copilot", "gemini", "qwen", "opencode" })
        {
            Assert.True(M.HoldsAwaitingInput(provider, false, null, false), provider);
        }
        foreach (var provider in new[] { "warp", "claudedesktop", "unknown" })
        {
            Assert.False(M.HoldsAwaitingInput(provider, true, ClaudeTailState.ToolInFlight, true), provider);
        }
        Assert.True(M.IsAwaitingInputRawState("AwaitingInput"));
        Assert.False(M.IsAwaitingInputRawState("executing"));
    }

    [Fact]
    public void OnlyACorroboratedClaudePromptOutlivesTheStaleCap()
    {
        Assert.True(M.AwaitingInputOutlivesStaleCap("claude", true, ClaudeTailState.ToolInFlight));
        Assert.False(M.AwaitingInputOutlivesStaleCap("claude", false, ClaudeTailState.ToolInFlight));
        Assert.False(M.AwaitingInputOutlivesStaleCap("claude", true, ClaudeTailState.TurnFinished));
        Assert.False(M.AwaitingInputOutlivesStaleCap("cursor", true, ClaudeTailState.ToolInFlight));
        Assert.False(M.AwaitingInputOutlivesStaleCap("vscode", true, ClaudeTailState.ToolInFlight));
    }

    [Fact]
    public void OnlyProvablyLiveClaudeWorkOutlivesTheStaleCap()
    {
        Assert.True(M.HookFileOutlivesStaleCap("claude", "executing", true, false, false));
        Assert.True(M.HookFileOutlivesStaleCap("claude", "thinking", true, false, false));
        Assert.True(M.HookFileOutlivesStaleCap("claude", "executing", false, true, false));
        Assert.True(M.HookFileOutlivesStaleCap("claude", "thinking", false, false, true));
        Assert.False(M.HookFileOutlivesStaleCap("claude", "executing", false, false, false));
        Assert.False(M.HookFileOutlivesStaleCap("claude", "stopped", true, true, true));
        Assert.False(M.HookFileOutlivesStaleCap("claude", "awaiting_input", true, false, false));
        Assert.False(M.HookFileOutlivesStaleCap("cursor", "executing", true, true, false));
        Assert.False(M.HookFileOutlivesStaleCap("codex", "executing", true, false, false));
    }

    // Entry 3 addendum: a late-written session record must not mark a live chat dead.
    [Fact]
    public void ALiveSessionWhoseRecordWasWrittenLateIsStillAlive()
    {
        Assert.True(M.ProcessMatchesSessionRecord(processStartMs: 1_000_000, recordStartedAtMs: 1_013_000));
        Assert.False(M.ProcessMatchesSessionRecord(processStartMs: 1_020_000, recordStartedAtMs: 1_013_000), "pid reuse");
        Assert.True(M.ProcessMatchesSessionRecord(1_000_000, 1_500_000, recordProcStartMs: 1_002_000));
        Assert.False(M.ProcessMatchesSessionRecord(1_000_000, 1_500_000, recordProcStartMs: 1_010_000));
    }

    [Theory]
    [InlineData("shell")]
    [InlineData("Shell")]
    [InlineData("run_terminal_cmd")]
    [InlineData("runTerminalCmd")]
    [InlineData("read")]
    [InlineData("todowrite")]
    [InlineData("apply_patch")]
    [InlineData("str_replace")]
    [InlineData("web_search")]
    [InlineData("AskQuestion")]
    [InlineData("subagent")]
    public void ToolNamesAreRejectedAsChatTitles(string name) => Assert.True(M.LooksLikeToolName(name));

    [Theory]
    [InlineData("Fix the notch traffic light")]
    [InlineData("Debugging a shell script")]
    [InlineData("Why does the light stay green?")]
    public void RealChatTitlesSurviveSanitation(string title) => Assert.False(M.LooksLikeToolName(title));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyTitlesAreNotToolNames(string? title) => Assert.False(M.LooksLikeToolName(title));
}

/// <summary>Ported from macOS ClaudeReconcilerTests (REGRESSIONS entries 5, 7, 12).</summary>
public class ClaudeReconcilerTests
{
    private static IReadOnlyList<AgentSession> Reconcile(IReadOnlyList<AgentSession> hooks, IReadOnlyList<AgentSession> passive, params string[] dead) =>
        M.ReconcileClaudeSessions(hooks, passive, dead.ToHashSet(), 60_000, 120_000, 2_000_000);

    [Fact]
    public void InheritedFieldsCarryAcrossOnDemote()
    {
        var hook = Sessions.Make(updatedAt: 1_000);
        var passive = Sessions.Make(chatName: "Fix the parser", projectName: "kannu", rawState: "stopped", display: AgentLightState.Stopped,
            updatedAt: 1_500, cwd: "/tmp/proj", hostPid: 4242, toolErrors: 2, unattended: true, runError: RunError.ApiError(429));
        var merged = Assert.Single(Reconcile([hook], [passive]));
        Assert.Equal(AgentLightState.Stopped, merged.DisplayState);
        Assert.Equal("Fix the parser", merged.ChatName);
        Assert.Equal("kannu", merged.ProjectName);
        Assert.Equal("/tmp/proj", merged.Cwd);
        Assert.Equal(4242, merged.HostPid);
        Assert.Equal(2, merged.ToolErrorCount);
        Assert.True(merged.IsUnattended);
        Assert.Equal(RunError.ApiError(429), merged.RunError);
    }

    [Fact]
    public void HeldYellowSurvivesPassiveToolInFlight()
    {
        var hook = Sessions.Make(rawState: "awaiting_input", display: AgentLightState.AwaitingInput, updatedAt: 1_000);
        var passive = Sessions.Make(chatName: "Ask", updatedAt: 1_900, hostPid: 4242);
        var merged = Assert.Single(Reconcile([hook], [passive]));
        Assert.Equal(AgentLightState.AwaitingInput, merged.DisplayState);
        Assert.Equal(4242, merged.HostPid);
        Assert.Equal("Ask", merged.ChatName);
    }

    [Fact]
    public void HeldYellowDemotesWhenTheProcessDies()
    {
        var hook = Sessions.Make(rawState: "awaiting_input", display: AgentLightState.AwaitingInput, updatedAt: 1_000);
        var passive = Sessions.Make(rawState: "stopped", display: AgentLightState.Stopped, updatedAt: 1_950);
        Assert.Equal(AgentLightState.Stopped, Reconcile([hook], [passive], "conv-1")[0].DisplayState);
        Assert.False(Reconcile([hook], [], "conv-1")[0].DisplayState.IsActiveRun());
    }

    [Fact]
    public void AgedYellowIsNotPromotedByPassiveActivity()
    {
        var hook = Sessions.Make(rawState: "awaiting_input", display: AgentLightState.Inactive, updatedAt: 500, visible: false);
        var passive = Sessions.Make(rawState: "thinking", display: AgentLightState.Thinking, updatedAt: 1_900);
        var output = Reconcile([hook], [passive])[0];
        Assert.Equal(AgentLightState.Inactive, output.DisplayState);
        Assert.False(output.IsVisible);
    }

    [Fact]
    public void DesktopSessionIdCarriesAcrossEveryArm()
    {
        var passiveStopped = Sessions.Make(rawState: "stopped", display: AgentLightState.Stopped, updatedAt: 1_500, desktopSessionId: "local_x");
        Assert.Equal("local_x", Reconcile([Sessions.Make()], [passiveStopped])[0].DesktopSessionId);
        var passiveActive = Sessions.Make(desktopSessionId: "local_x");
        Assert.Equal("local_x", Reconcile([Sessions.Make()], [passiveActive])[0].DesktopSessionId);
        Assert.Equal("local_x", Reconcile([Sessions.Make(rawState: "thinking", display: AgentLightState.Inactive)], [passiveActive])[0].DesktopSessionId);
    }

    [Fact]
    public void TheTurnCarriesAcrossEveryArm()
    {
        var turn = new HookTurn(400_000, null, 12, "/u/.claude/projects/p/c.jsonl", 100);
        var hook = Sessions.Make(turn: turn);
        var demoted = Reconcile([hook], [Sessions.Make(rawState: "stopped", display: AgentLightState.Stopped, updatedAt: 1_500_000)]);
        Assert.Equal(AgentLightState.Stopped, demoted[0].DisplayState);
        Assert.Equal(turn, demoted[0].Turn);
        Assert.Equal(turn, Reconcile([hook], [Sessions.Make()])[0].Turn);
        var aged = Sessions.Make(display: AgentLightState.Inactive, updatedAt: 500, visible: false, turn: turn);
        var promoted = Reconcile([aged], [Sessions.Make(updatedAt: 1_900)]);
        Assert.Equal(AgentLightState.Executing, promoted[0].DisplayState);
        Assert.Equal(turn, promoted[0].Turn);
    }

    [Fact]
    public void AKeptAgedHookFileDoesNotHideAFinishedPassiveCard()
    {
        var kept = Sessions.Make(display: AgentLightState.Inactive, updatedAt: 100, visible: false);
        var finished = Sessions.Make(chatName: "Esc'd", rawState: "stopped", display: AgentLightState.Inactive, updatedAt: 1_900, hostPid: 9);
        var output = Reconcile([kept], [finished])[0];
        Assert.True(output.IsVisible);
        Assert.Equal("Esc'd", output.ChatName);
        Assert.Equal(9, output.HostPid);

        var agedYellow = Sessions.Make(rawState: "awaiting_input", display: AgentLightState.Inactive, updatedAt: 100, visible: false);
        var thinking = Sessions.Make(rawState: "thinking", display: AgentLightState.Thinking, updatedAt: 1_900);
        Assert.False(Reconcile([agedYellow], [thinking])[0].IsVisible);
    }

    [Fact]
    public void RunVerdictSeamPrefersTheMoreSpecificReason()
    {
        var hookFailed = Sessions.Make(rawState: "stopped", display: AgentLightState.Stopped, runError: RunError.Failed);
        var passiveApi = Sessions.Make(rawState: "stopped", display: AgentLightState.Stopped, runError: RunError.ApiError(429));
        Assert.Equal(RunError.ApiError(429), Reconcile([hookFailed], [passiveApi])[0].RunError);
        var hookClean = Sessions.Make(rawState: "stopped", display: AgentLightState.Stopped);
        Assert.Equal(RunError.ApiError(429), Reconcile([hookClean], [passiveApi])[0].RunError);
        var passiveClean = Sessions.Make(rawState: "stopped", display: AgentLightState.Stopped);
        Assert.Equal(RunError.Failed, Reconcile([hookFailed], [passiveClean])[0].RunError);
        Assert.Null(Reconcile([hookClean], [passiveClean])[0].RunError);
    }

    [Fact]
    public void DeadPidWithoutPassiveDemotesFromTheHookTimestamp() =>
        Assert.False(Reconcile([Sessions.Make(updatedAt: 1_999_000)], [], "conv-1")[0].DisplayState.IsActiveRun());

    [Fact]
    public void FreshHookIsNotDemotedByOlderPassiveEvidence() =>
        Assert.Equal(AgentLightState.Executing,
            Reconcile([Sessions.Make(updatedAt: 1_800)], [Sessions.Make(rawState: "stopped", display: AgentLightState.Stopped, updatedAt: 1_200)])[0].DisplayState);

    [Fact]
    public void AgedOutHookIsPromotedByALivePassiveRun()
    {
        var output = Reconcile([Sessions.Make(display: AgentLightState.Inactive, updatedAt: 500, visible: false)], [Sessions.Make(updatedAt: 1_900)])[0];
        Assert.Equal(AgentLightState.Executing, output.DisplayState);
        Assert.True(output.IsVisible);
        Assert.Equal(1_900, output.UpdatedAtMs);
    }

    [Fact]
    public void PassiveOnlySessionsAreAppended()
    {
        var output = Reconcile([Sessions.Make()], [Sessions.Make(conversation: "conv-2", chatName: "Solo", rawState: "stopped", display: AgentLightState.Stopped)]);
        Assert.Equal(2, output.Count);
        Assert.Contains(output, s => s.ConversationId == "conv-2" && s.ChatName == "Solo");
    }

    [Fact]
    public void NonClaudeSessionsPassThroughUntouched()
    {
        var cursor = Sessions.Make(provider: "cursor", conversation: "c-9");
        var passive = Sessions.Make(conversation: "c-9", rawState: "stopped", display: AgentLightState.Stopped, updatedAt: 1_999);
        Assert.Equal(AgentLightState.Executing, Reconcile([cursor], [passive], "c-9").First(s => s.Provider == "cursor").DisplayState);
    }

    [Fact]
    public void EmptyPassiveAndNoDeadPidsIsIdentity()
    {
        var hook = Sessions.Make();
        Assert.Equal([hook], Reconcile([hook], []));
    }
}

/// <summary>Ported from macOS SubagentFoldTests.</summary>
public class SubagentFoldTests
{
    private const long T0 = 1_788_000_000_000;

    private static AgentSession S(string conversation, string raw, AgentLightState state, bool visible = true, string? name = null,
        string provider = "claude", long at = 0, HookTurn? turn = null) =>
        Sessions.Make(provider, conversation, name, "kannu", raw, state, T0 + at, visible, "/u/kannu", 42, turn: turn);

    private static (IReadOnlyList<AgentSession> Sessions, HashSet<string> Folded) Fold(IReadOnlyList<AgentSession> s, Dictionary<string, string> parents) =>
        SubagentFold.Fold(s, parents);

    [Fact]
    public void ASubagentIsNeverACardOfItsOwn()
    {
        var result = Fold([S("parent", "executing", AgentLightState.Executing, name: "Fix the parser"), S("a98f", "thinking", AgentLightState.Thinking, at: 5)],
            new() { ["claude|a98f"] = "parent" });
        Assert.Equal(["parent"], result.Sessions.Select(s => s.ConversationId));
        Assert.Equal("Fix the parser", result.Sessions[0].ChatName);
        Assert.Equal(["a98f"], result.Folded);
    }

    [Fact]
    public void ASubagentPromptTurnsTheOpenTurnYellow()
    {
        var merged = Fold([S("parent", "thinking", AgentLightState.Thinking, name: "Chat", at: 9), S("sub", "awaiting_input", AgentLightState.AwaitingInput, at: 5)],
            new() { ["claude|sub"] = "parent" }).Sessions[0];
        Assert.Equal(AgentLightState.AwaitingInput, merged.DisplayState);
        Assert.Equal("claude-parent", merged.Id);
        Assert.Equal("Chat", merged.ChatName);
        Assert.Equal(42, merged.HostPid);
    }

    [Fact]
    public void ALeftoverSubagentNeverRelightsAFinishedTurn() =>
        Assert.Equal(AgentLightState.Stopped,
            Fold([S("parent", "stopped", AgentLightState.Stopped), S("sub", "thinking", AgentLightState.Thinking, at: 30)],
                new() { ["claude|sub"] = "parent" }).Sessions[0].DisplayState);

    [Fact]
    public void AnAgedParentMidRunShowsTheSubagentsLight()
    {
        var merged = Fold([S("parent", "executing", AgentLightState.Executing, visible: false), S("sub", "executing", AgentLightState.Executing, at: 400)],
            new() { ["claude|sub"] = "parent" }).Sessions[0];
        Assert.True(merged.IsVisible);
        Assert.Equal(T0 + 400, merged.UpdatedAtMs);
    }

    [Fact]
    public void WithoutAParentFileAStandInCarriesTheParentsIdAndGetsItsName()
    {
        var standIns = Fold([S("sub", "executing", AgentLightState.Executing)], new() { ["claude|sub"] = "parent-uuid" }).Sessions;
        var standIn = standIns[0];
        Assert.Equal("claude-parent-uuid", standIn.Id);
        Assert.Null(standIn.ChatName);
        Assert.False(standIn.IsVisible);
        Assert.Equal(AgentLightState.Inactive, standIn.DisplayState);
        Assert.Equal("executing", standIn.RawState);

        var named = M.ReconcileClaudeSessions(standIns, [S("parent-uuid", "executing", AgentLightState.Executing, name: "The real title")],
            new HashSet<string>(), 60_000, 120_000, T0);
        var promoted = named.First(s => s.ConversationId == "parent-uuid");
        Assert.Equal("The real title", promoted.ChatName);
        Assert.Equal(AgentLightState.Executing, promoted.DisplayState);
        Assert.True(promoted.IsVisible);
    }

    [Fact]
    public void TheAggregateLightIsUnchangedWhileTheTurnIsOpen()
    {
        var states = new[] { ("executing", AgentLightState.Executing), ("thinking", AgentLightState.Thinking), ("awaiting_input", AgentLightState.AwaitingInput) };
        foreach (var (pr, ps) in states)
        foreach (var (sr, ss) in states)
        {
            var parent = S("parent", pr, ps);
            var sub = S("sub", sr, ss, at: 1);
            var folded = Fold([parent, sub], new() { ["claude|sub"] = "parent" }).Sessions;
            Assert.Equal(M.ResolveDisplayState([parent, sub]), M.ResolveDisplayState(folded));
        }
    }

    [Fact]
    public void SubagentToolCallsAddToTheOpenTurn()
    {
        var parent = S("parent", "executing", AgentLightState.Executing, name: "Chat", turn: new HookTurn(T0, null, 5, "p", 10));
        var subA = S("subA", "thinking", AgentLightState.Thinking, at: 2, turn: new HookTurn(T0 + 1_000, null, 7, null, null));
        var subB = S("subB", "awaiting_input", AgentLightState.AwaitingInput, at: 3, turn: new HookTurn(T0 + 2_000, null, 1, null, null));
        var merged = Fold([parent, subA, subB], new() { ["claude|subA"] = "parent", ["claude|subB"] = "parent" }).Sessions[0];
        Assert.Equal(AgentLightState.AwaitingInput, merged.DisplayState);
        Assert.Equal(13, merged.Turn!.ToolCalls);
        Assert.Equal(T0, merged.Turn.StartedMs);
    }

    [Fact]
    public void ALeftoverSubagentAddsNothing()
    {
        var parent = S("parent", "stopped", AgentLightState.Stopped, turn: new HookTurn(T0 + 100_000, T0 + 200_000, 2, null, null));
        var sub = S("sub", "thinking", AgentLightState.Thinking, at: 50, turn: new HookTurn(T0 + 10_000, null, 40, null, null));
        Assert.Equal(2, Fold([parent, sub], new() { ["claude|sub"] = "parent" }).Sessions[0].Turn!.ToolCalls);
    }

    [Fact]
    public void AStandInHasNoTurnAndATurnlessParentNeverAdoptsOne()
    {
        var sub = S("sub", "executing", AgentLightState.Executing, turn: new HookTurn(T0, null, 3, null, null));
        Assert.Null(Fold([sub], new() { ["claude|sub"] = "parent" }).Sessions[0].Turn);
        var parent = S("parent", "executing", AgentLightState.Executing);
        Assert.Null(Fold([parent, sub], new() { ["claude|sub"] = "parent" }).Sessions.First(s => s.ConversationId == "parent").Turn);
        Assert.Null(Fold([sub, parent], new() { ["claude|sub"] = "parent" }).Sessions.First(s => s.ConversationId == "parent").Turn);
    }

    [Fact]
    public void ProvidersNeverCross()
    {
        var cursor = S("same", "executing", AgentLightState.Executing, provider: "cursor");
        var claudeSub = S("sub", "awaiting_input", AgentLightState.AwaitingInput);
        var result = Fold([cursor, claudeSub], new() { ["claude|sub"] = "same" });
        Assert.Equal(AgentLightState.Executing, result.Sessions.First(s => s.Provider == "cursor").DisplayState);
        Assert.Equal(2, result.Sessions.Count);
    }

    [Theory]
    [InlineData("abc-DEF_123", true)]
    [InlineData("", false)]
    [InlineData("../x", false)]
    [InlineData(null, false)]
    public void ConversationIdsAreValidated(string? id, bool valid) => Assert.Equal(valid, M.IsHookConversationId(id));
}

/// <summary>Ported from macOS AgentExecutionClockTests (REGRESSIONS entry 15).</summary>
public class AgentExecutionClockTests
{
    private const long T0 = 1_757_000_000_000;
    private const long Now = T0 + 1_200_000;

    private static AgentExecutionClock.Input In(string id = "c1", bool active = true, bool rawActive = true, long updated = T0, bool previous = false) =>
        new(id, active, rawActive, updated, previous);

    [Fact]
    public void ANewRunStartsNow() =>
        Assert.Equal(Now, AgentExecutionClock.Resolve([In()], new Dictionary<string, long>(), Now).DisplayedStartByConversationId["c1"]);

    [Fact]
    public void ARunInProgressKeepsItsStart() =>
        Assert.Equal(T0, AgentExecutionClock.Resolve([In(previous: true)], new Dictionary<string, long> { ["c1"] = T0 }, Now).DisplayedStartByConversationId["c1"]);

    [Fact]
    public void ARunUnderwayWhenKannuStartedDatesFromUpdatedAt() =>
        Assert.Equal(T0, AgentExecutionClock.Resolve([In(previous: true)], new Dictionary<string, long>(), Now).DisplayedStartByConversationId["c1"]);

    [Fact]
    public void AStaleAgedDemotionDoesNotRestartTheClock()
    {
        var dipped = AgentExecutionClock.Resolve([In(active: false, previous: true)], new Dictionary<string, long> { ["c1"] = T0 }, Now);
        Assert.False(dipped.DisplayedStartByConversationId.ContainsKey("c1"));
        Assert.Equal(T0, dipped.StartByConversationId["c1"]);
        var resumed = AgentExecutionClock.Resolve([In()], dipped.StartByConversationId, Now);
        Assert.Equal(T0, resumed.DisplayedStartByConversationId["c1"]);
    }

    [Fact]
    public void AGenuineEndClearsTheClockAndANewRequestRestarts()
    {
        var ended = AgentExecutionClock.Resolve([In(active: false, rawActive: false, previous: true)], new Dictionary<string, long> { ["c1"] = T0 }, Now);
        Assert.Empty(ended.StartByConversationId);
        Assert.Equal(Now, AgentExecutionClock.Resolve([In()], ended.StartByConversationId, Now).DisplayedStartByConversationId["c1"]);
    }

    [Fact]
    public void AConversationThatLeavesTheListIsForgotten()
    {
        var result = AgentExecutionClock.Resolve([In("c2")], new Dictionary<string, long> { ["c1"] = T0, ["c2"] = T0 }, Now);
        Assert.False(result.StartByConversationId.ContainsKey("c1"));
        Assert.True(result.StartByConversationId.ContainsKey("c2"));
    }
}

public class SessionListTests
{
    [Fact]
    public void LatestSessionsKeepsTheHigherStateThenTheTitledThenTheNewer()
    {
        var running = Sessions.Make(conversation: "c", updatedAt: 1);
        var stopped = Sessions.Make(conversation: "c", rawState: "stopped", display: AgentLightState.Stopped, updatedAt: 0);
        Assert.Equal(AgentLightState.Stopped, M.LatestSessions([running, stopped]).Single().DisplayState);

        var untitled = Sessions.Make(conversation: "d", updatedAt: 5);
        var titled = Sessions.Make(conversation: "d", chatName: "Real", updatedAt: 1);
        Assert.Equal("Real", M.LatestSessions([untitled, titled]).Single().ChatName);
    }

    [Fact]
    public void TheHostNamesACursorDrivenClaudeChat()
    {
        var claude = Sessions.Make(provider: "claude", conversation: "same", display: AgentLightState.AwaitingInput, rawState: "awaiting_input");
        var cursor = Sessions.Make(provider: "cursor", conversation: "same", chatName: "Cursor chat");
        var merged = M.LatestSessions([claude, cursor]).Single();
        Assert.Equal("cursor", merged.Provider);
        Assert.Equal(AgentLightState.AwaitingInput, merged.DisplayState);
        Assert.Equal("Cursor chat", merged.ChatName);
    }

    [Fact]
    public void SimulationSessionsNeverDriveTheLight()
    {
        Assert.Equal(AgentLightState.Inactive, M.ResolveDisplayState([Sessions.Make(conversation: "default")]));
        Assert.Equal(AgentLightState.Inactive, M.ResolveDisplayState([Sessions.Make(conversation: "test-1")]));
    }

    [Fact]
    public void AnEndedRedChatStaysListedDimForTheRetentionWindow()
    {
        var red = Sessions.Make(rawState: "stopped", display: AgentLightState.Stopped);
        var (sessions, retained) = M.RetainEndedSessions([red], [], new Dictionary<string, M.RetainedEndedSession>(), 5_000);
        var kept = Assert.Single(sessions);
        Assert.Equal(AgentLightState.Inactive, kept.DisplayState);
        Assert.True(kept.IsVisible);

        var (later, _) = M.RetainEndedSessions([kept], [], retained, 5_000 + M.EndedChatRetentionMs);
        Assert.Empty(later);

        var (live, liveRetained) = M.RetainEndedSessions([kept], [Sessions.Make()], retained, 6_000);
        Assert.Equal(AgentLightState.Executing, Assert.Single(live).DisplayState);
        Assert.Empty(liveRetained);
    }
}
