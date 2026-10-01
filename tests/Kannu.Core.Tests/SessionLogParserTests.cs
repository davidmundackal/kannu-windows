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

using System.Text;
using Kannu.Core;
using P = Kannu.Core.SessionLogParser;

namespace Kannu.Core.Tests;

/// <summary>Ported from macOS AgentSessionLogParserTests.</summary>
public sealed class SessionLogParserTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kannu-log-" + Guid.NewGuid().ToString("N"));

    public SessionLogParserTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private static string L(string json) => json + "\n";

    private const string User = """{"type":"user","timestamp":"2026-08-21T10:00:00.123Z","message":{"role":"user","content":"fix the bug"}}""";
    private const string EndTurn = """{"type":"assistant","timestamp":"2026-08-21T10:00:00.000Z","message":{"role":"assistant","content":[{"type":"text","text":"done"}],"stop_reason":"end_turn"}}""";
    private const string ApiError529 = """{"type":"assistant","timestamp":"2026-08-21T10:00:00.000Z","isApiErrorMessage":true,"apiErrorStatus":529,"message":{"role":"assistant","content":[{"type":"text","text":"API Error"}],"stop_reason":"stop_sequence"}}""";

    [Fact]
    public void TrailingUserPromptIsWorking()
    {
        var r = P.ClaudeTailStateFromText(L(User));
        Assert.Equal(ClaudeTailState.Working, r.State);
        Assert.NotNull(r.RecordTimestampMs);
        Assert.True(r.NewestRecordIsConversational);
    }

    [Fact]
    public void DraftTrailerMarksTheTailNonConversational()
    {
        var r = P.ClaudeTailStateFromText(L(User) + L("""{"type":"last-prompt","lastPrompt":"draft tex","leafUuid":"abc"}"""));
        Assert.Equal(ClaudeTailState.Working, r.State);
        Assert.False(r.NewestRecordIsConversational);
    }

    [Fact]
    public void TimestampWithoutFractionalSecondsParses() =>
        Assert.NotNull(P.ClaudeTailStateFromText(L("""{"type":"user","timestamp":"2026-08-21T10:00:00Z","message":{"role":"user","content":"hello"}}""")).RecordTimestampMs);

    [Theory]
    [InlineData("""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]}}""")]
    [InlineData("""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user for tool use]"}]}}""")]
    [InlineData("""{"type":"user","message":{"role":"user","content":"[Request interrupted by user]"}}""")]
    [InlineData("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":[{"type":"text","text":"[Request interrupted by user for tool use]"}]}]}}""")]
    public void InterruptsFinishTheTurn(string record) => Assert.Equal(ClaudeTailState.TurnFinished, P.ClaudeTailStateFromText(L(record)).State);

    [Fact]
    public void NormalToolResultIsWorking() =>
        Assert.Equal(ClaudeTailState.Working, P.ClaudeTailStateFromText(L("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""")).State);

    [Fact]
    public void AssistantToolUseIsToolInFlight() =>
        Assert.Equal(ClaudeTailState.ToolInFlight, P.ClaudeTailStateFromText(L("""{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{}}],"stop_reason":"tool_use"}}""")).State);

    [Theory]
    [InlineData("end_turn", ClaudeTailState.TurnFinished)]
    [InlineData("stop_sequence", ClaudeTailState.TurnFinished)]
    [InlineData("max_tokens", ClaudeTailState.TurnFinished)]
    [InlineData("refusal", ClaudeTailState.TurnFinished)]
    [InlineData("tool_use", ClaudeTailState.Working)]
    [InlineData("pause_turn", ClaudeTailState.Working)]
    public void StopReasons(string stopReason, ClaudeTailState expected) =>
        Assert.Equal(expected, P.ClaudeTailStateFromText(L($$$"""{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"x"}],"stop_reason":"{{{stopReason}}}"}}""")).State);

    [Fact]
    public void NullStopReasonIsWorking() =>
        Assert.Equal(ClaudeTailState.Working, P.ClaudeTailStateFromText(L("""{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"..."}],"stop_reason":null}}""")).State);

    [Fact]
    public void CustomTitleBeatsAITitleAndTheLatestWins()
    {
        Assert.Equal("chat detection", P.ClaudeTitle(L("""{"type":"ai-title","aiTitle":"Fix status"}""") + L("""{"type":"custom-title","customTitle":"chat detection"}""")));
        Assert.Equal("renamed by user", P.ClaudeTitle(L("""{"type":"custom-title","customTitle":"first"}""") + L("""{"type":"ai-title","aiTitle":"model"}""") + L("""{"type":"custom-title","customTitle":"renamed by user"}""")));
        Assert.Equal("Fix healthcheck", P.ClaudeTitle(L("""{"type":"user","message":{"content":"hi"}}""") + L("""{"type":"ai-title","aiTitle":"Fix healthcheck"}""")));
        Assert.Null(P.ClaudeTitle(L("""{"type":"last-prompt","lastPrompt":"hi"}""")));
    }

    [Fact]
    public void BlankTitlesAreIgnoredAndLongOnesCapped() =>
        Assert.Equal(72, P.ClaudeTitle(L("""{"type":"custom-title","customTitle":"   "}""") + L($$"""{"type":"ai-title","aiTitle":"{{new string('x', 100)}}"}"""))!.Length);

    [Fact]
    public void BookkeepingAfterEndTurnIsStillTurnFinished() =>
        Assert.Equal(ClaudeTailState.TurnFinished, P.ClaudeTailStateFromText(L(EndTurn) + L("""{"type":"ai-title","aiTitle":"x"}""")).State);

    [Fact]
    public void TruncatedLeadingFragmentIsSkippedAndFragmentOnlyIsUnknown()
    {
        Assert.Equal(ClaudeTailState.TurnFinished, P.ClaudeTailStateFromText("ncated tail\"}]}}\n" + L(EndTurn)).State);
        Assert.Equal(ClaudeTailState.Unknown, P.ClaudeTailStateFromText("ncated tail with no complete records\n").State);
    }

    [Fact]
    public void ApiErrorsFinishTheTurnWithTheirStatus()
    {
        var r = P.ClaudeTailStateFromText(L(ApiError529));
        Assert.Equal(ClaudeTailState.TurnFinished, r.State);
        Assert.Equal(RunError.ApiError(529), r.RunError);

        Assert.Equal(RunError.ApiError(529), P.ClaudeTailStateFromText(L(ApiError529) + L("""{"type":"last-prompt","lastPrompt":"x"}""")).RunError);
        var retried = P.ClaudeTailStateFromText(L(ApiError529) + L(User));
        Assert.Equal(ClaudeTailState.Working, retried.State);
        Assert.Null(retried.RunError);

        Assert.Null(P.ClaudeTailStateFromText(L("""{"type":"assistant","isApiErrorMessage":false,"message":{"content":[],"stop_reason":"end_turn"}}""")).RunError);
        Assert.Equal(RunError.ApiError(null), P.ClaudeTailStateFromText(L("""{"type":"assistant","isApiErrorMessage":true,"apiErrorStatus":"529","message":{}}""")).RunError);
    }

    [Fact]
    public void EscalationFindsAVerdictBeyondTheFirstWindow()
    {
        var path = Path.Combine(_dir, "big.jsonl");
        File.WriteAllText(path, L($$$"""{"type":"assistant","message":{"content":[{"type":"text","text":"{{{new string('x', 40_000)}}}"}],"stop_reason":"end_turn"}}"""));
        Assert.Equal(ClaudeTailState.TurnFinished, new P(_dir).ClaudeTailState(path).State);
    }

    [Fact]
    public void EscalationSurvivesAMultibyteWindowBoundary()
    {
        var path = Path.Combine(_dir, "dash.jsonl");
        var padding = new string('—', 12_000);
        var data = Encoding.UTF8.GetBytes(padding + "\n" + L(EndTurn));
        for (var i = 0; i < 3 && (data[^16_000] & 0xC0) != 0x80; i++)
        {
            padding += "—";
            data = Encoding.UTF8.GetBytes(padding + "\n" + L(EndTurn));
        }
        Assert.Equal(0x80, data[^16_000] & 0xC0);
        File.WriteAllBytes(path, data);
        Assert.Equal(ClaudeTailState.TurnFinished, new P(_dir).ClaudeTailState(path).State);
    }

    [Fact]
    public void DisplayNamePrefersClaudesTitleThenTheFirstPrompt()
    {
        var parser = new P(_dir);
        var titled = Path.Combine(_dir, "t.jsonl");
        File.WriteAllText(titled, L(User) + L("""{"type":"ai-title","aiTitle":"Fix the parser"}"""));
        Assert.Equal("Fix the parser", parser.DisplayChatName(titled, SessionLogProvider.Claude));

        var untitled = Path.Combine(_dir, "u.jsonl");
        File.WriteAllText(untitled, L("""{"type":"user","message":{"content":"<user_query>  make the light\nyellow</user_query>"}}"""));
        Assert.Equal("make the light", parser.DisplayChatName(untitled, SessionLogProvider.Claude));
    }

    [Theory]
    [InlineData("rollout-2026-08-21T10-00-00-0199c1d2-3e4f-7a8b-9c0d-1e2f3a4b5c6d", "0199c1d2-3e4f-7a8b-9c0d-1e2f3a4b5c6d")]
    [InlineData("rollout-short", "short")]
    public void CodexSessionIdIsTheTrailingUuid(string name, string expected) =>
        Assert.Equal(expected, P.SessionId(Path.Combine("x", name + ".jsonl"), SessionLogProvider.Codex));
}

/// <summary>Ported from macOS PassiveClaudeStateTests.</summary>
public class PassiveClaudeStateTests
{
    private const long Now = 1_787_300_000_000;
    private const long Collapse = 8_000;
    private const long Inactive = 300_000;

    private static (string RawState, AgentLightState State, bool Visible, long UpdatedAtMs) Resolve(ClaudeTailResult tail, long? mtime = null) =>
        AgentStateMachine.PassiveClaudeState(tail, mtime, Now - 60_000, Now, Collapse, Inactive);

    [Fact]
    public void FreshWorkingIsThinking() => Assert.Equal(AgentLightState.Thinking, Resolve(new(ClaudeTailState.Working, Now - 120_000)).State);

    [Fact]
    public void StaleWorkingGoesInactive()
    {
        var r = Resolve(new(ClaudeTailState.Working, Now - 660_000), Now - 660_000);
        Assert.Equal(AgentLightState.Inactive, r.State);
        Assert.Equal("idle", r.RawState);
        Assert.True(r.Visible);
    }

    [Fact]
    public void StaleWorkingWithFreshConversationalMtimeStaysThinking() =>
        Assert.Equal(AgentLightState.Thinking, Resolve(new(ClaudeTailState.Working, Now - 660_000, true), Now - 30_000).State);

    [Fact]
    public void ADraftTrailerDoesNotRelightAStaleWorkingSession() =>
        Assert.Equal(AgentLightState.Inactive, Resolve(new(ClaudeTailState.Working, Now - 660_000, false), Now - 5_000).State);

    [Fact]
    public void WorkingWithNoEvidenceStaysThinking()
    {
        Assert.Equal(AgentLightState.Thinking, Resolve(new(ClaudeTailState.Working, null)).State);
        Assert.Equal(AgentLightState.Thinking, Resolve(new(ClaudeTailState.Working, null, false), Now).State);
    }

    [Fact]
    public void ALongToolRunStaysExecuting() =>
        Assert.Equal((AgentLightState.Executing, true), (Resolve(new(ClaudeTailState.ToolInFlight, Now - 1_800_000), Now - 1_800_000) is var r ? (r.State, r.Visible) : default));

    [Fact]
    public void AFreshFinishIsRedAndAnOldOneIsDimButListed()
    {
        Assert.Equal(AgentLightState.Stopped, Resolve(new(ClaudeTailState.TurnFinished, Now - 2_000)).State);
        var old = Resolve(new(ClaudeTailState.TurnFinished, Now - 1_200_000), Now - 3_000);
        Assert.Equal(AgentLightState.Inactive, old.State);
        Assert.True(old.Visible);
        Assert.Equal(Now - 1_200_000, old.UpdatedAtMs);
    }

    [Fact]
    public void AnUnreadableTailOnALiveProcessIsThinkingNeverIdle()
    {
        var r = Resolve(ClaudeTailResult.Unknown, Now - 60_000);
        Assert.Equal(AgentLightState.Thinking, r.State);
        Assert.Equal("thinking", r.RawState);
    }
}

public sealed class ClaudePassiveScannerTests : IDisposable
{
    private const long Now = 1_790_000_000_000;
    private readonly string _home = Path.Combine(Path.GetTempPath(), "kannu-claude-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private void Session(int pid, string id, long startedAt, string? transcript = null)
    {
        var sessions = Path.Combine(_home, ".claude", "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions, $"{pid}.json"),
            $$"""{"pid":{{pid}},"sessionId":"{{id}}","startedAt":{{startedAt}},"kind":"interactive","cwd":"C:\\src\\kannu"}""");
        if (transcript is null) return;
        var project = Path.Combine(_home, ".claude", "projects", "C--src-kannu");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, id + ".jsonl"), transcript);
    }

    private ClaudePassiveResult Scan(Func<int, ProcessProbe> probe) =>
        ClaudePassiveScanner.Scan(new SessionLogParser(_home), new AgentTimings(), Now, probe);

    [Fact]
    public void ALiveProcessWithAToolInFlightIsExecutingAndClickable()
    {
        Session(10, "abc", Now - 60_000, """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t"}],"stop_reason":"tool_use"}}""" + "\n");
        var result = Scan(_ => new ProcessProbe(true, Now - 61_000));
        var session = Assert.Single(result.Sessions);
        Assert.Equal(AgentLightState.Executing, session.DisplayState);
        Assert.Equal(10, session.HostPid);
        Assert.Equal("kannu", session.ProjectName);
        Assert.Equal(ClaudeTailState.ToolInFlight, result.LiveTails["abc"]);
        Assert.Empty(result.DeadPidConversationIds);
    }

    [Fact]
    public void AReusedPidIsDead()
    {
        Session(10, "abc", Now - 600_000);
        var result = Scan(_ => new ProcessProbe(true, Now - 1_000)); // started long after the record
        Assert.Contains("abc", result.DeadPidConversationIds);
        Assert.Null(result.Sessions.Single().HostPid);
    }

    [Fact]
    public void AResumedSessionsLiveProcessBeatsItsOrphanFile()
    {
        Session(10, "abc", Now - 600_000);
        Session(20, "abc", Now - 5_000);
        var result = Scan(pid => pid == 20 ? new ProcessProbe(true, Now - 6_000) : ProcessProbe.Gone);
        Assert.DoesNotContain("abc", result.DeadPidConversationIds);
    }

    [Fact]
    public void AnUninspectableLiveProcessCountsAsAlive()
    {
        Session(10, "abc", Now - 60_000);
        Assert.Single(Scan(_ => new ProcessProbe(true, null)).LiveTails);
    }

    [Theory]
    [InlineData("""{"pid":"x","sessionId":"a","startedAt":1,"kind":"interactive"}""")]
    [InlineData("""{"pid":1,"sessionId":"../a","startedAt":1,"kind":"interactive"}""")]
    [InlineData("""{"pid":1,"sessionId":"a","startedAt":1,"kind":"print"}""")]
    [InlineData("[]")]
    public void UntrustedRecordsAreSkipped(string json)
    {
        var sessions = Path.Combine(_home, ".claude", "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(sessions, "1.json"), json);
        Assert.Empty(Scan(_ => new ProcessProbe(true, null)).Sessions);
    }

    [Fact]
    public void ProcStartParsesTheRecordFormat() =>
        Assert.Equal(new DateTimeOffset(2026, 9, 12, 7, 12, 39, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            ClaudePassiveScanner.ProcStartMs("Sat Sep 12  07:12:39 2026"));
}
