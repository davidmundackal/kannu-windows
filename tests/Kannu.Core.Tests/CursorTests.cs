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
using E = Kannu.Core.CursorTranscripts.TranscriptEvent;

namespace Kannu.Core.Tests;

public class CursorTranscriptAnalysisTests
{
    private static TranscriptAnalysis A(params E[] events) => CursorTranscripts.AnalyzeEvents(1, events);
    private static E Assistant(string? stop = null, params string[] tools) => new("assistant", tools, stop);
    private static readonly E User = new("user", [], null);
    private static readonly E TurnEnded = new("turn_ended", [], null);

    [Fact]
    public void AnAssistantReplyWithoutToolsIsDone() => Assert.True(A(User, Assistant("end_turn")).IsDone);

    [Fact]
    public void AToolCallIsActiveNotDone()
    {
        var a = A(User, Assistant(null, "Read"));
        Assert.False(a.IsDone);
        Assert.True(a.HasActiveToolUse);
    }

    [Fact]
    public void AGatedToolProposalIsPendingApproval()
    {
        var a = A(User, Assistant(null, "WebSearch"));
        Assert.True(a.HasPendingToolApproval);
        Assert.False(a.HasActiveToolUse);
    }

    [Fact]
    public void AProposalAnsweredByTheUserOrAnEndedTurnIsNotPending()
    {
        Assert.False(A(Assistant(null, "Shell"), User).HasPendingToolApproval);
        Assert.False(A(Assistant(null, "Shell"), TurnEnded).HasPendingToolApproval);
    }

    [Fact]
    public void APromptAfterAnEndedTurnIsAwaitingAResponse()
    {
        Assert.True(A(Assistant("end_turn"), TurnEnded, User).IsUserPromptAwaitingResponse);
        Assert.False(A(TurnEnded, Assistant(), User).IsUserPromptAwaitingResponse);
        Assert.True(A(Assistant("end_turn"), TurnEnded).IsTurnEndedAtTail);
    }

    [Fact]
    public void SubagentTranscriptsNameTheirParent()
    {
        var sep = Path.DirectorySeparatorChar;
        var path = $"{sep}h{sep}.cursor{sep}projects{sep}p{sep}agent-transcripts{sep}parent-1{sep}subagents{sep}agent-sub-2.jsonl";
        Assert.Equal("parent-1", CursorTranscripts.ParentSessionId(path));
        Assert.Equal("sub-2", CursorTranscripts.SessionId(path));
        Assert.Null(CursorTranscripts.ParentSessionId($"{sep}h{sep}x.jsonl"));
    }

    [Fact]
    public void AProjectSlugBecomesTheFolderName()
    {
        var home = OperatingSystem.IsWindows() ? @"C:\Users\me" : "/home/me";
        var transcripts = new CursorTranscripts(home);
        var homeSlug = home.Replace(":", "").Replace('\\', '-').Replace('/', '-').Trim('-');
        Assert.Equal("kannu", transcripts.DisplayProjectName($"{homeSlug}-src-kannu"));
        Assert.Null(transcripts.DisplayProjectName("var-folders-x"));
        Assert.Null(transcripts.DisplayProjectName("123456"));
    }
}

public class CursorSessionsTests
{
    private const long Now = 1_790_000_000_000;
    private static readonly AgentTimings Timings = new();

    private static AgentSessionSnapshot Snap(string? status = null, bool done = false, bool tool = false, bool pending = false,
        bool awaitingResponse = false, long age = 1_000) =>
        new("c", Now - age, status, done, tool, pending, awaitingResponse, Now - age);

    [Theory]
    [InlineData("awaiting_input", AgentLightState.AwaitingInput)]
    [InlineData("generating", AgentLightState.Executing)]
    [InlineData("thinking", AgentLightState.Thinking)]
    public void ALiveComposerStatusDecides(string status, AgentLightState expected) => Assert.Equal(expected, CursorSessions.Map(Snap(status), Now, Timings).State);

    [Fact]
    public void APendingGatedToolIsYellowUnlessSomethingFresherSaysOtherwise()
    {
        Assert.Equal(AgentLightState.AwaitingInput, CursorSessions.Map(Snap(pending: true), Now, Timings).State);
        Assert.Equal(AgentLightState.Executing, CursorSessions.Map(Snap("generating", pending: true), Now, Timings).State);
    }

    [Fact]
    public void ADoneTranscriptFlashesRedThenDims()
    {
        Assert.Equal(AgentLightState.Stopped, CursorSessions.Map(Snap(done: true, age: 1_000), Now, Timings).State);
        Assert.Equal((AgentLightState.Inactive, true), CursorSessions.Map(Snap(done: true, age: 7_000), Now, Timings));
        Assert.Equal((AgentLightState.Inactive, false), CursorSessions.Map(Snap(done: true, age: 60_000), Now, Timings));
    }

    [Fact]
    public void AStaleSnapshotIsHidden() => Assert.False(CursorSessions.Map(Snap(age: Timings.StaleMs + 1), Now, Timings).Visible);

    private static AgentSession Cursor(string raw, AgentLightState state, long updated = Now - 1_000, string provider = "cursor", string id = "c") =>
        Sessions.Make(provider, id, rawState: raw, display: state, updatedAt: updated);

    private static readonly Dictionary<string, TranscriptAnalysis> Pending = new() { ["c"] = new(0, false, false, true, false, false) };
    private static readonly Dictionary<string, TranscriptAnalysis> Ended = new() { ["c"] = new(0, true, false, false, false, true) };

    [Fact]
    public void HookYellowIsNeverOverriddenByTheTranscript() =>
        Assert.Equal(AgentLightState.AwaitingInput, CursorSessions.EnrichHookSessions([Cursor("awaiting_input", AgentLightState.AwaitingInput)], Ended, Now)[0].DisplayState);

    [Fact]
    public void ATranscriptApprovalCardTurnsAWorkingHookYellow() =>
        Assert.Equal(AgentLightState.AwaitingInput, CursorSessions.EnrichHookSessions([Cursor("thinking", AgentLightState.Thinking)], Pending, Now)[0].DisplayState);

    [Fact]
    public void ALaggingTurnEndedNeverDemotesAFreshHook()
    {
        Assert.Equal(AgentLightState.Executing, CursorSessions.EnrichHookSessions([Cursor("executing", AgentLightState.Executing)], Ended, Now)[0].DisplayState);
        Assert.Equal(AgentLightState.Stopped,
            CursorSessions.EnrichHookSessions([Cursor("executing", AgentLightState.Executing, Now - 120_000)], Ended, Now)[0].DisplayState);
    }

    [Fact]
    public void OtherProvidersAreNotEnriched() =>
        Assert.Equal(AgentLightState.Thinking, CursorSessions.EnrichHookSessions([Cursor("thinking", AgentLightState.Thinking, provider: "claude")], Pending, Now)[0].DisplayState);

    [Fact]
    public void AFreshHookExecutingBeatsALingeringTranscriptYellow()
    {
        var merged = CursorSessions.Merge([Cursor("executing", AgentLightState.Executing)], [Cursor("transcript", AgentLightState.AwaitingInput)], Now);
        Assert.Equal(AgentLightState.Executing, Assert.Single(merged).DisplayState);
    }

    [Fact]
    public void ATranscriptYellowBeatsHookThinking() =>
        Assert.Equal(AgentLightState.AwaitingInput,
            Assert.Single(CursorSessions.Merge([Cursor("thinking", AgentLightState.Thinking)], [Cursor("transcript", AgentLightState.AwaitingInput)], Now)).DisplayState);

    [Fact]
    public void SubagentChatsRollIntoTheirParent()
    {
        var parent = Cursor("thinking", AgentLightState.Thinking, id: "p");
        var sub = Cursor("executing", AgentLightState.Executing, id: "s");
        var rolled = Assert.Single(CursorSessions.CollapseSubagents([parent, sub], new Dictionary<string, string> { ["s"] = "p" }, Now));
        Assert.Equal("p", rolled.ConversationId);
        Assert.Equal(AgentLightState.Executing, rolled.DisplayState);
    }

    [Fact]
    public void TitlesFromComposerAndGlassAreVettedTheTranscriptPromptIsTrusted()
    {
        var sources = new ChatTitleSources(
            new Dictionary<string, ComposerMeta> { ["c"] = new("c", null, 0, 0, 0, "Plan: refactor") },
            new Dictionary<string, string> { ["c"] = "Let me look at the parser" },
            new Dictionary<string, string> { ["c"] = "fix the parser bug" },
            new Dictionary<string, IReadOnlyList<string>> { ["c"] = ["Let me look at the parser and fix it"] },
            new HashSet<string> { "Plan: refactor" });
        Assert.Equal("fix the parser bug", CursorSessions.ResolveChatName("c", "Shell", sources));
        Assert.Equal("Fix login", CursorSessions.ResolveChatName("c", "Fix login", sources));
    }
}

public sealed class CursorDatabaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kannu-cursor-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
    }

    private CursorDatabase Seed()
    {
        var global = Path.Combine(_dir, "globalStorage");
        Directory.CreateDirectory(global);
        using var db = new SqliteConnection($"Data Source={Path.Combine(global, "state.vscdb")};Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE ItemTable (key TEXT, value TEXT);
            CREATE TABLE cursorDiskKV (key TEXT, value BLOB);
            INSERT INTO ItemTable VALUES ('composer.composerHeaders', '{"allComposers":[{"composerId":"a","status":"generating","lastUpdatedAt":100,"name":"Fix login"}]}');
            INSERT INTO ItemTable VALUES ('composer.planRegistry', '{"x":{"name":"Big plan"}}');
            INSERT INTO ItemTable VALUES ('cursor/glass.tabs.v2/ws/agent-9/state.json', '{"tabOrder":["t2","t1"],"planTabs":[{"id":"t1","label":"First"},{"id":"t2","kind":"plan","label":"Plan"},{"id":"t3","label":"Other"}]}');
            INSERT INTO cursorDiskKV VALUES ('composerData:b', '{"status":"aborted","lastUpdatedAt":5,"conversationCheckpointLastUpdatedAt":50,"name":"Old chat"}');
            """;
        cmd.ExecuteNonQuery();
        return new CursorDatabase(_dir);
    }

    [Fact]
    public void ComposerMetaComesFromHeadersAndPerChatRecords()
    {
        var meta = Seed().ComposerMeta(new HashSet<string> { "a", "b", "missing" }, DateTime.UtcNow);
        Assert.Equal("generating", meta["a"].Status);
        Assert.Equal("Fix login", meta["a"].Name);
        Assert.Equal(50, meta["b"].UpdatedMs);
        Assert.False(meta.ContainsKey("missing"));
    }

    [Fact]
    public void PlanNamesAndGlassTitlesAreRead()
    {
        var db = Seed();
        Assert.Contains("Big plan", db.PlanRegistryNames(DateTime.UtcNow));
        Assert.Equal("First", db.GlassTitles(new HashSet<string> { "agent-9" }, DateTime.UtcNow)["agent-9"]);
    }

    [Fact]
    public void AMissingDatabaseYieldsNothing() =>
        Assert.Empty(new CursorDatabase(Path.Combine(_dir, "nope")).ComposerMeta(new HashSet<string> { "a" }, DateTime.UtcNow));
}
