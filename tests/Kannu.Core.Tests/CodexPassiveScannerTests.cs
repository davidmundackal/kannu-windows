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

using System.Globalization;
using System.Text;
using Kannu.Core;

namespace Kannu.Core.Tests;

public sealed class CodexPassiveScannerTests : IDisposable
{
    private const long Now = 1_790_000_000_000;
    private const string Id = "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b";
    private static readonly AgentTimings Timings = new();
    private readonly string _home = Path.Combine(Path.GetTempPath(), "kannu-codex-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (Exception) { }
    }

    private static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static string Meta(string id = Id, string cwd = @"C:\work\kannu") =>
        $$$"""{"timestamp":"{{{Iso(Now - 60_000)}}}","type":"session_meta","payload":{"id":"{{{id}}}","timestamp":"{{{Iso(Now - 60_000)}}}","cwd":"{{{cwd.Replace("\\", "\\\\")}}}","originator":"codex_cli_rs","instructions":null}}""";

    private static string Event(string type, long ms, string extra = "") =>
        $$$"""{"timestamp":"{{{Iso(ms)}}}","type":"event_msg","payload":{"type":"{{{type}}}"{{{extra}}}}}""";

    private static string Item(string type, long ms, string extra = "") =>
        $$$"""{"timestamp":"{{{Iso(ms)}}}","type":"response_item","payload":{"type":"{{{type}}}"{{{extra}}}}}""";

    private static string UserPrompt(long ms) => Event("user_message", ms, ""","message":"fix the flaky login test","images":[]""");

    private string Rollout(IEnumerable<string> lines, long mtimeMs = Now - 1_000, string id = Id, string? root = null)
    {
        var dir = Path.Combine(root ?? Path.Combine(_home, ".codex"), "sessions", "2026", "09", "21");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"rollout-2026-09-21T10-00-00-{id}.jsonl");
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
        File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeMilliseconds(mtimeMs).UtcDateTime);
        return path;
    }

    private IReadOnlyList<AgentSession> Scan(string? codexHome = null, IReadOnlySet<string>? hooked = null) =>
        new CodexPassiveScanner(new SessionLogParser(_home, codexHome)).Scan(Timings, Now, hooked);

    [Fact]
    public void AnOpenTurnIsGreen()
    {
        Rollout([Meta(), Event("task_started", Now - 20_000), UserPrompt(Now - 20_000), Item("reasoning", Now - 5_000),
            Event("token_count", Now - 4_000)]);
        var session = Assert.Single(Scan());
        Assert.Equal(AgentLightState.Thinking, session.DisplayState);
        Assert.True(session.IsVisible);
        Assert.Equal("codex", session.Provider);
        Assert.Equal(Id, session.ConversationId);
        Assert.Equal("codex-" + Id, session.Id);
    }

    [Fact]
    public void ARunningCommandIsExecuting()
    {
        Rollout([Meta(), Event("task_started", Now - 20_000), Item("function_call", Now - 10_000, ""","name":"shell","call_id":"c1" """),
            Event("exec_command_begin", Now - 9_000)]);
        Assert.Equal(AgentLightState.Executing, Assert.Single(Scan()).DisplayState);
    }

    [Fact]
    public void AnApprovalRequestWithNoDecisionIsYellow()
    {
        Rollout([Meta(), Event("task_started", Now - 20_000), Item("function_call", Now - 10_000),
            Event("exec_approval_request", Now - 9_000), Event("token_count", Now - 8_000)]);
        var session = Assert.Single(Scan());
        Assert.Equal(AgentLightState.AwaitingInput, session.DisplayState);
        Assert.Equal("awaiting_input", session.RawState);
    }

    [Fact]
    public void ADecisionAfterTheApprovalRequestIsGreenAgain()
    {
        Rollout([Meta(), Event("task_started", Now - 20_000), Event("apply_patch_approval_request", Now - 9_000),
            Event("patch_apply_begin", Now - 3_000)]);
        Assert.Equal(AgentLightState.Executing, Assert.Single(Scan()).DisplayState);
    }

    [Fact]
    public void ACompletedTurnIsRedThenDims()
    {
        Rollout([Meta(), Event("task_started", Now - 20_000), Event("agent_message", Now - 3_000, ""","message":"done" """),
            Event("task_complete", Now - 2_000), Event("token_count", Now - 1_000)]);
        var session = Assert.Single(Scan());
        Assert.Equal(AgentLightState.Stopped, session.DisplayState);
        Assert.Equal(Now - 2_000, session.UpdatedAtMs);

        // The red clock runs from task_complete, not from later bookkeeping writes.
        var later = CodexPassiveScanner.State(new CodexTailResult(CodexTailState.Finished, Now - 60_000), Now, Now, Timings);
        Assert.Equal(AgentLightState.Inactive, later!.Value.State);
        Assert.False(later.Value.Visible);
    }

    [Fact]
    public void AnAbortedTurnIsRed()
    {
        Rollout([Meta(), Event("task_started", Now - 20_000), Event("turn_aborted", Now - 2_000, ""","reason":"interrupted" """)]);
        var session = Assert.Single(Scan());
        Assert.Equal(AgentLightState.Stopped, session.DisplayState);
        Assert.Equal("aborted", session.RawState);
    }

    [Fact]
    public void AnErrorEndsTheTurnRedWithARunError()
    {
        Rollout([Meta(), Event("task_started", Now - 20_000), Event("error", Now - 2_000)]);
        var session = Assert.Single(Scan());
        Assert.Equal(AgentLightState.Stopped, session.DisplayState);
        Assert.Equal(RunError.Failed, session.RunError);
    }

    [Fact]
    public void ALegacyRolloutWithoutTurnMarkersEndsOnTheAssistantMessage()
    {
        Assert.Equal(CodexTailState.Finished, CodexPassiveScanner.TailStateFromText(string.Join('\n',
            UserPrompt(Now - 10_000), Item("message", Now - 2_000, ""","role":"assistant" """))).State);
        Assert.Equal(CodexTailState.Working, CodexPassiveScanner.TailStateFromText(string.Join('\n',
            Event("task_started", Now - 10_000), Item("message", Now - 2_000, ""","role":"assistant" """))).State);
    }

    [Fact]
    public void AnOpenTurnWithNoWritesAgesOut()
    {
        var quiet = Now - AgentStateMachine.PassiveWorkingStaleMs - 60_000;
        Rollout([Meta(), Event("task_started", quiet - 1_000), Item("reasoning", quiet)], mtimeMs: quiet);
        var session = Assert.Single(Scan());
        Assert.False(session.IsVisible);
        Assert.Equal(AgentLightState.Inactive, session.DisplayState);
    }

    [Fact]
    public void ARolloutOutsideTheStaleWindowIsDropped()
    {
        var old = Now - Timings.StaleMs - 60_000;
        Rollout([Meta(), Event("task_started", old)], mtimeMs: old);
        Assert.Empty(Scan());
    }

    [Fact]
    public void OnlyBookkeepingMeansNoCard()
    {
        Rollout([Meta(), """{"timestamp":"2026-09-21T10:00:00Z","type":"turn_context","payload":{"cwd":"C:\\x"}}"""]);
        Assert.Empty(Scan());
    }

    [Fact]
    public void ProjectAndNameComeFromTheRollout()
    {
        Rollout([Meta(cwd: Path.Combine(_home, "repos", "kannu-windows")), Event("task_started", Now - 5_000), UserPrompt(Now - 5_000)]);
        var session = Assert.Single(Scan());
        Assert.Equal("kannu-windows", session.ProjectName);
        Assert.Equal(Path.Combine(_home, "repos", "kannu-windows"), session.Cwd);
        Assert.Equal("fix the flaky login test", session.ChatName);
    }

    [Fact]
    public void TheSessionMetaIdIsTheConversation()
    {
        const string metaId = "11111111-2222-3333-4444-555555555555";
        Rollout([Meta(id: metaId), Event("task_started", Now - 5_000)]);
        Assert.Equal(metaId, Assert.Single(Scan()).ConversationId);
    }

    [Fact]
    public void AHugeSessionMetaLineStillYieldsIdAndCwd()
    {
        var line = $$$"""{"timestamp":"x","type":"session_meta","payload":{"id":"{{{Id}}}","cwd":"C:\\big","instructions":"{{{new string('a', 200_000)}}}"}}""";
        var (id, cwd) = CodexPassiveScanner.SessionMetaFromLine(Encoding.UTF8.GetBytes(line)[..65_536], isComplete: false);
        Assert.Equal(Id, id);
        Assert.Equal(@"C:\big", cwd);
    }

    [Fact]
    public void CodexHomeOverridesTheDefaultRoot()
    {
        var custom = Path.Combine(_home, "custom-codex");
        Rollout([Meta(), Event("task_started", Now - 5_000)], root: custom);
        Assert.Empty(Scan());
        Assert.Single(Scan(codexHome: custom));
        Assert.Equal(Path.Combine(custom, "sessions"), new SessionLogParser(_home, custom).CodexSessionsDirectory);
        Assert.Equal(Path.Combine(_home, ".codex", "sessions"), new SessionLogParser(_home, "  ").CodexSessionsDirectory);
    }

    [Fact]
    public void AHookedConversationIsSkipped()
    {
        Rollout([Meta(), Event("task_started", Now - 5_000)]);
        Assert.Empty(Scan(hooked: new HashSet<string> { Id }));
    }

    [Fact]
    public void AHugeRolloutIsReadOnlyFromTheTail()
    {
        // 6 MB of history with an open turn at the head, then a completed turn at the end: the verdict
        // comes from the first tail window, never the head.
        var lines = new List<string> { Meta(), Event("task_started", Now - 50_000) };
        var filler = Item("function_call_output", Now - 40_000, $$$""","output":"{{{new string('x', 4_000)}}}" """);
        while (lines.Sum(l => l.Length) < 6_000_000) lines.Add(filler);
        lines.Add(Event("task_complete", Now - 2_000));
        var path = Rollout(lines);
        Assert.True(new FileInfo(path).Length > 4_194_304);
        Assert.Equal(AgentLightState.Stopped, Assert.Single(Scan()).DisplayState);

        // And past the widest window the head is never consulted: 6 MB of bookkeeping after the last
        // deciding record leaves the verdict unknown (no card), rather than a whole-file read.
        var noise = Event("token_count", Now - 1_000, $$$""","info":"{{{new string('y', 4_000)}}}" """);
        var bookkeeping = new List<string> { Meta(), Event("task_started", Now - 50_000) };
        while (bookkeeping.Sum(l => l.Length) < 6_000_000) bookkeeping.Add(noise);
        Rollout(bookkeeping);
        Assert.Empty(Scan());
    }

    [Fact]
    public void TheHookCardWinsAndTheConversationIsListedOnce()
    {
        var pipeline = new AgentSessionPipeline(Timings, _home);
        var passive = new AgentSession
        {
            Id = "codex-" + Id, Provider = "codex", ConversationId = Id, ChatName = "fix the flaky login test",
            RawState = "awaiting_input", DisplayState = AgentLightState.AwaitingInput, UpdatedAtMs = Now - 1_000, IsVisible = true,
        };
        var hook = new HookFile($"/s/codex-{Id}.json", $"codex-{Id}", new StatusRecord { State = "thinking", Ts = Now - 2_000, Provider = "codex" }, DateTime.UnixEpoch);
        var evidence = PassiveEvidence.None with { PassiveCodex = [passive] };

        var withHook = pipeline.Update([hook], Now, evidence);
        var session = Assert.Single(withHook.Sessions);
        Assert.Equal(AgentLightState.Thinking, session.DisplayState);

        var hookless = new AgentSessionPipeline(Timings, _home).Update([], Now, evidence);
        Assert.Equal(AgentLightState.AwaitingInput, Assert.Single(hookless.Sessions).DisplayState);
    }
}
