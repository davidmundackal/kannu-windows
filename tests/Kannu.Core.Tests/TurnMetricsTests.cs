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

/// <summary>The turn rules, driven through the real hook (macOS REGRESSIONS entry 15).</summary>
public sealed class TurnMetricsTests : IDisposable
{
    private const long T0 = 1_790_000_000_000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "kannu-turn-" + Guid.NewGuid().ToString("N"));
    private string Dir => Path.Combine(_root, "status");
    private string Home => Path.Combine(_root, "home");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private void Run(string state, string hookEvent, string json, long now, string provider = "claude") =>
        HookRunner.Run(new HookInvocation(state, provider, hookEvent, ""), json, Dir, now, new HookEnvironment(false, false, Home));

    private StatusRecord Status(string session = "s") => StatusStore.Read(Path.Combine(Dir, $"claude-{session}.json"))!;

    [Fact]
    public void APromptStartsATurnAndStopEndsIt()
    {
        Run("thinking", "UserPromptSubmit", """{"session_id":"s"}""", T0);
        Run("executing", "PreToolUse", """{"session_id":"s","tool_name":"Bash","tool_use_id":"t1"}""", T0 + 1_000);
        Run("thinking", "PostToolUse", """{"session_id":"s","tool_name":"Bash","tool_use_id":"t1"}""", T0 + 2_000);
        Run("stopped", "Stop", """{"session_id":"s"}""", T0 + 9_000);
        var s = Status();
        Assert.Equal(T0, s.TurnStartedMs);
        Assert.Equal(T0 + 9_000, s.TurnEndedMs);
        Assert.Equal(1, s.TurnToolCalls);
    }

    [Fact]
    public void APromptMidRunJoinsTheRequest()
    {
        Run("thinking", "UserPromptSubmit", """{"session_id":"s"}""", T0);
        Run("thinking", "UserPromptSubmit", """{"session_id":"s"}""", T0 + 30_000);
        Assert.Equal(T0, Status().TurnStartedMs);
    }

    [Fact]
    public void APromptAfterStopStartsANewRequest()
    {
        Run("thinking", "UserPromptSubmit", """{"session_id":"s"}""", T0);
        Run("stopped", "Stop", """{"session_id":"s"}""", T0 + 5_000);
        Run("thinking", "UserPromptSubmit", """{"session_id":"s"}""", T0 + 60_000);
        var s = Status();
        Assert.Equal(T0 + 60_000, s.TurnStartedMs);
        Assert.Null(s.TurnEndedMs);
    }

    [Fact]
    public void WorkAfterStopWithoutAPromptReopensTheSameTurn()
    {
        Run("thinking", "UserPromptSubmit", """{"session_id":"s"}""", T0);
        Run("stopped", "Stop", """{"session_id":"s"}""", T0 + 5_000);
        Run("executing", "PreToolUse", """{"session_id":"s","tool_name":"Bash"}""", T0 + 8_000);
        var s = Status();
        Assert.Equal(T0, s.TurnStartedMs);
        Assert.Null(s.TurnEndedMs);
    }

    [Fact]
    public void ACompletionDeliveredTwiceCountsOnce()
    {
        Run("thinking", "UserPromptSubmit", """{"session_id":"s"}""", T0);
        Run("thinking", "PostToolUse", """{"session_id":"s","tool_name":"Read"}""", T0 + 1_000);
        Run("thinking", "PostToolUse", """{"session_id":"s","tool_name":"Read"}""", T0 + 1_500);
        Run("thinking", "PostToolUse", """{"session_id":"s","tool_name":"Read"}""", T0 + 9_000);
        Assert.Equal(2, Status().TurnToolCalls);
    }

    [Fact]
    public void ThinkingNeverMovesTheClock()
    {
        Run("thinking", "UserPromptSubmit", """{"session_id":"s"}""", T0);
        for (var i = 1; i <= 5; i++) Run("thinking", "PostToolUse", $$"""{"session_id":"s","tool_use_id":"t{{i}}"}""", T0 + i * 10_000);
        Assert.Equal(T0, Status().TurnStartedMs);
        Assert.Equal(5, Status().TurnToolCalls);
    }

    [Fact]
    public void TheTranscriptOffsetIsItsSizeWhenTheTurnStarts()
    {
        var transcript = Path.Combine(Path.GetFullPath(Home), ".claude", "projects", "p", "s.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        File.WriteAllText(transcript, new string('x', 123));
        var json = $$"""{"session_id":"s","transcript_path":{{System.Text.Json.JsonSerializer.Serialize(transcript)}}}""";
        Run("thinking", "UserPromptSubmit", json, T0);
        Assert.Equal(123, Status().TurnTranscriptOffset);
        Assert.Equal(transcript, Status().TranscriptPath);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("relative/x.jsonl")]
    public void TranscriptPathsOutsideClaudeProjectsAreRejected(string path) =>
        Assert.Equal("", TurnMetrics.TranscriptPath(path, "/home/me"));

    [Fact]
    public void ASubagentTranscriptIsRejected()
    {
        var home = Path.GetFullPath(Home);
        var sep = Path.DirectorySeparatorChar;
        Assert.Equal("", TurnMetrics.TranscriptPath($"{home}{sep}.claude{sep}projects{sep}p{sep}subagents{sep}a.jsonl", home));
    }

    [Fact]
    public void AnImplausibleCarriedStartIsDropped()
    {
        Assert.Null(TurnMetrics.Carried(new StatusRecord { TurnStartedMs = 5 }, T0));
        Assert.Null(TurnMetrics.Carried(new StatusRecord { TurnStartedMs = T0 + 120_000 }, T0));
        Assert.NotNull(TurnMetrics.Carried(new StatusRecord { TurnStartedMs = T0 - 1 }, T0));
    }
}
