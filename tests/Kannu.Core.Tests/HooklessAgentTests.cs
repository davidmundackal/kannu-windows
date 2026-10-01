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
using Kannu.Detection;
using Microsoft.Data.Sqlite;

namespace Kannu.Core.Tests;

public sealed class WarpStoreTests : IDisposable
{
    private const long Now = 1_790_000_000_000;
    private static readonly AgentTimings Timings = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kannu-warp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    [Theory]
    [InlineData("\"Pending\"", 10_000, true, "executing")]
    [InlineData("Pending", 10_000, false, "aborted")]
    [InlineData("Pending", 400_000, true, "aborted")]
    [InlineData("Completed", 0, true, "stopped")]
    [InlineData("Cancelled", 0, true, "aborted")]
    [InlineData("Failed", 0, true, "error")]
    public void StatusesMapLikeMacOS(string status, long age, bool running, string expected) =>
        Assert.Equal(expected, WarpStore.RawState(status, age, running));

    [Theory]
    [InlineData("""[{"Query":{"text":"fix the build\nnow","context":[]}}]""", "fix the build now")]
    [InlineData("""[{"Query":{"text":"caf\u00e9"}}]""", "caf")]
    [InlineData("""[{"Other":{}}]""", null)]
    [InlineData(null, null)]
    public void ThePromptIsTheTitle(string? prefix, string? expected) => Assert.Equal(expected, WarpStore.QueryTitle(prefix));

    [Fact]
    public void TimestampsParseWithAndWithoutFractions()
    {
        Assert.Equal(new DateTimeOffset(2026, 6, 6, 19, 20, 37, TimeSpan.Zero).ToUnixTimeMilliseconds() + 931, WarpStore.ParseTimestamp("2026-06-06 19:20:37.931790"));
        Assert.NotNull(WarpStore.ParseTimestamp("2026-06-06 19:20:37"));
        Assert.Null(WarpStore.ParseTimestamp("nope"));
    }

    [Fact]
    public void OnlyTheNewestExchangePerConversationCountsAndFailureIsTheVerdict()
    {
        var sessions = WarpStore.SessionsFrom(
        [
            new("e2", "c", Now - 1_000, "Failed", @"C:\src\kannu", null),
            new("e1", "c", Now - 60_000, "Completed", null, null),
        ], Timings, Now, warpRunning: true);
        var session = Assert.Single(sessions);
        Assert.Equal(RunError.Failed, session.RunError);
        Assert.Equal("kannu", session.ProjectName);
        Assert.Equal(AgentLightState.Stopped, session.DisplayState);
    }

    [Fact]
    public void TheDatabaseIsReadReadOnly()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "warp.sqlite");
        using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            var start = DateTimeOffset.UtcNow.AddSeconds(-5).ToString("yyyy-MM-dd HH:mm:ss.ffffff");
            cmd.CommandText = $$$"""
                CREATE TABLE ai_queries (exchange_id TEXT, conversation_id TEXT, start_ts TEXT, output_status TEXT, working_directory TEXT, input TEXT);
                INSERT INTO ai_queries VALUES ('e1', 'conv', '{{{start}}}', '"Pending"', 'C:\src\app', '[{"Query":{"text":"deploy it"}}]');
                """;
            cmd.ExecuteNonQuery();
        }
        var session = Assert.Single(new WarpStore([Path.Combine(_dir, "missing.sqlite"), path])
            .Sessions(Timings, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), warpRunning: true));
        Assert.Equal(AgentLightState.Executing, session.DisplayState);
        Assert.Equal("deploy it", session.ChatName);
    }

    [Fact]
    public void NoDatabaseNoSessions() => Assert.Empty(new WarpStore([Path.Combine(_dir, "nope")]).Sessions(Timings, Now, true));
}

public sealed class ClaudeDesktopAgentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kannu-desktop-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private const string Init = """{"type":"system","subtype":"init","model":"claude-x","cwd":"C:\\src\\kannu"}""";

    [Fact]
    public void TheHeadGivesModelAndProject()
    {
        var parsed = ClaudeDesktopAgentStore.Parse(Init + "\n" + """{"title":"Tidy the repo"}""", null);
        Assert.Equal("claude-x", parsed.Model);
        Assert.Equal(@"C:\src\kannu", parsed.Cwd);
        Assert.Equal("Tidy the repo", parsed.Title);
        Assert.Equal("idle", parsed.RawState);
    }

    [Theory]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"tool_use"}]}}""", "executing")]
    [InlineData("""{"type":"assistant","message":{"content":[],"stop_reason":"end_turn"}}""", "stopped")]
    [InlineData("""{"type":"assistant","message":{"content":[]}}""", "thinking")]
    [InlineData("""{"type":"user","message":{"content":"go"}}""", "thinking")]
    [InlineData("""{"type":"result","subtype":"success"}""", "stopped")]
    [InlineData("""{"type":"rate_limit_event","rate_limit_info":{"status":"rejected"}}""", "quota_exceeded")]
    public void TheNewestRecordDecides(string tail, string expected) => Assert.Equal(expected, ClaudeDesktopAgentStore.Parse(null, tail).RawState);

    [Fact]
    public void AnErrorResultIsTheVerdictAndErrorsCountBackToThePrompt()
    {
        var tail = string.Join('\n',
            """{"type":"user","message":{"content":"do it"}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","is_error":true}]}}""",
            """{"type":"result","subtype":"error_during_execution","is_error":true,"api_error_status":529}""");
        var parsed = ClaudeDesktopAgentStore.Parse(null, tail);
        Assert.Equal(RunError.ApiError(529), parsed.RunError);
        Assert.Equal(2, parsed.ToolErrorCount);
    }

    [Fact]
    public void SessionsAreFoundByDirectoryNameIncludingDispatchAgents()
    {
        var interactive = Path.Combine(_root, "user", "org", "local_abc", "audit.jsonl");
        var dispatch = Path.Combine(_root, "user", "org", "agent", "local_ditto_xyz", "audit.jsonl");
        foreach (var p in new[] { interactive, dispatch })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, Init + "\n" + """{"type":"assistant","message":{"content":[{"type":"tool_use"}]}}""" + "\n");
        }
        var sessions = new ClaudeDesktopAgentStore(_root).Sessions(new AgentTimings(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Equal(2, sessions.Count);
        Assert.Contains(sessions, s => s.ConversationId == "abc" && s.DisplayState == AgentLightState.Executing);
        Assert.Contains(sessions, s => s.ConversationId == "xyz" && s.ChatName == "Dispatch agent");
    }
}
