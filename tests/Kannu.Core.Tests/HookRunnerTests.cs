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

public sealed class HookRunnerTests : IDisposable
{
    private const long T0 = 1_790_000_000_000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "kannu-tests-" + Guid.NewGuid().ToString("N"));
    private string Dir => Path.Combine(_root, "status");
    private string Home => Path.Combine(_root, "home");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private HookResult Run(string state, string hookEvent, string json, long now, string provider = "claude",
        string matcherKey = "", bool copilotCli = false, bool console = false) =>
        HookRunner.Run(new HookInvocation(state, provider, hookEvent, matcherKey), json, Dir, now,
            new HookEnvironment(copilotCli, console, Home));

    private StatusRecord? Status(string session, string provider = "claude") =>
        StatusStore.Read(Path.Combine(Dir, $"{provider}-{session}.json"));

    [Fact]
    public void TheHostWindowIsRecordedAndKeptWhenAnEventFindsNone()
    {
        HookRunner.Run(new HookInvocation("thinking", "claude", "UserPromptSubmit", ""), """{"session_id":"h1"}""", Dir, T0,
            new HookEnvironment(false, false, Home, new HookHost(42, "WindowsTerminal", null)));
        Assert.Equal(42, Status("h1")!.HostPid);
        Assert.Equal("WindowsTerminal", Status("h1")!.HostName);

        Run("executing", "PreToolUse", """{"session_id":"h1","tool_name":"Bash"}""", T0 + 1_000);
        Assert.Equal(42, Status("h1")!.HostPid);

        HookRunner.Run(new HookInvocation("stopped", "claude", "Stop", ""), """{"session_id":"h1"}""", Dir, T0 + 2_000,
            new HookEnvironment(false, true, Home, new HookHost(null, null, 0x1234)));
        var moved = Status("h1")!;
        Assert.Null(moved.HostPid);
        Assert.Null(moved.HostName);
        Assert.Equal(0x1234, moved.HostWindow);
    }

    [Fact]
    public void ASessionLifecycleWritesThenDeletesItsFile()
    {
        Run("idle", "SessionStart", """{"session_id":"s1","source":"startup","cwd":"/home/me/kannu"}""", T0);
        Assert.Equal("idle", Status("s1")!.State);
        Assert.Equal("kannu", Status("s1")!.Project);

        Run("thinking", "UserPromptSubmit", """{"session_id":"s1"}""", T0 + 10_000);
        Assert.Equal("thinking", Status("s1")!.State);
        Assert.Equal("kannu", Status("s1")!.Project); // carried when the event has no cwd

        Run("awaiting_input", "PermissionRequest", """{"session_id":"s1","tool_name":"Bash"}""", T0 + 20_000);
        Assert.Equal("awaiting_input", Status("s1")!.State);

        Run("stopped", "Stop", """{"session_id":"s1"}""", T0 + 30_000);
        var stopped = Status("s1")!;
        Assert.Equal("stopped", stopped.State);
        Assert.Equal(T0 + 30_000, stopped.Ts);
        Assert.Equal("Stop", stopped.HookEvent);

        Run("session_end", "SessionEnd", """{"session_id":"s1"}""", T0 + 40_000);
        Assert.False(File.Exists(Path.Combine(Dir, "claude-s1.json")));
    }

    [Fact]
    public void RacingGenericGroupDoesNotBuryYellow()
    {
        Run("awaiting_input", "PreToolUse", """{"session_id":"s","tool_name":"AskUserQuestion"}""", T0, matcherKey: "gated");
        Run("executing", "PreToolUse", """{"session_id":"s","tool_name":"Bash"}""", T0 + 10);
        Assert.Equal("awaiting_input", Status("s")!.State);
    }

    [Fact]
    public void CompactAndResumeDoNotReseedIdle()
    {
        Run("thinking", "UserPromptSubmit", """{"session_id":"s"}""", T0);
        Run("idle", "SessionStart", """{"session_id":"s","source":"compact"}""", T0 + 5_000);
        Assert.Equal("thinking", Status("s")!.State);
    }

    [Theory]
    [InlineData("claude", HookOutput.Allow)]
    [InlineData("cursor", HookOutput.Allow)]
    [InlineData("codex", "")]
    [InlineData("gemini", "{}")]
    [InlineData("qwen", "{}")]
    public void EachAgentGetsTheLineItExpects(string provider, string expected) =>
        Assert.Equal(expected, Run("thinking", "UserPromptSubmit", """{"session_id":"s"}""", T0, provider).Output);

    [Fact]
    public void IgnoredEventsStillAnswerTheAgent()
    {
        var result = Run("awaiting_input", "Notification", """{"session_id":"s","notification_type":"idle_prompt"}""", T0, "qwen");
        Assert.Equal("{}", result.Output);
        Assert.Equal(HookAction.Ignore, result.Decision.Action);
        Assert.Null(Status("s", "qwen"));
    }

    [Fact]
    public void CopilotCliIsToldApartFromVSCode()
    {
        var result = Run("thinking", "UserPromptSubmit", """{"sessionId":"c"}""", T0, "vscode", copilotCli: true);
        Assert.Equal("copilot", result.Provider);
        Assert.Equal("{}", result.Output);
        Assert.NotNull(Status("c", "copilot"));

        // VS Code's extension host: no env var, no console.
        Assert.Equal("vscode", Run("thinking", "UserPromptSubmit", """{"sessionId":"v"}""", T0, "vscode").Provider);
    }

    [Fact]
    public void ASubagentFileNamesItsParentChat()
    {
        Run("executing", "PreToolUse", """{"session_id":"chat","agent_id":"sub1","tool_name":"Read"}""", T0);
        Assert.Equal("chat", Status("sub1")!.ParentId);
    }

    [Fact]
    public void BypassedPermissionsAreStickyForTheSession()
    {
        Run("thinking", "UserPromptSubmit", """{"session_id":"s","permission_mode":"bypassPermissions"}""", T0);
        Run("stopped", "Stop", """{"session_id":"s"}""", T0 + 1_000);
        Assert.True(Status("s")!.Unattended);
    }

    [Fact]
    public void ToolErrorsCountFailuresButNotInterruptsAndResetOnPrompt()
    {
        Run("thinking", "PostToolUseFailure", """{"session_id":"s"}""", T0);
        Run("thinking", "PostToolUseFailure", """{"session_id":"s","is_interrupt":true}""", T0 + 3_000);
        Run("thinking", "PostToolUseFailure", """{"session_id":"s"}""", T0 + 6_000);
        Assert.Equal(2, Status("s")!.ToolErrors);
        Run("thinking", "UserPromptSubmit", """{"session_id":"s"}""", T0 + 9_000);
        Assert.Null(Status("s")!.ToolErrors);
    }

    [Fact]
    public void StopFailureEndsOnErrorAndANewTurnClearsIt()
    {
        Run("stopped", "StopFailure", """{"session_id":"s"}""", T0);
        Assert.True(Status("s")!.EndedOnError);
        Run("thinking", "UserPromptSubmit", """{"session_id":"s"}""", T0 + 5_000);
        Assert.Null(Status("s")!.EndedOnError);
    }

    [Fact]
    public void TitleComesFromTitleBearingEventsAndSurvivesOthers()
    {
        Run("thinking", "beforeSubmitPrompt", """{"conversation_id":"c","conversation_title":"Fix login"}""", T0, "cursor");
        Run("executing", "beforeShellExecution", """{"conversation_id":"c","title":"ignored"}""", T0 + 5_000, "cursor");
        Assert.Equal("Fix login", Status("c", "cursor")!.Name);
    }

    [Fact]
    public void AQuotaStopIsNamedSo()
    {
        Run("stopped", "Stop", """{"session_id":"a","terminationReason":"QUOTA"}""", T0, "antigravity");
        var status = Status("a", "antigravity")!;
        Assert.Equal("quota_exceeded", status.State);
        Assert.Equal("Quota exceeded", status.Name);
    }

    [Fact]
    public void CursorStickyYellowAbsorbsThoughtsButKeepsItsClock()
    {
        Run("executing", "afterAgentResponse", """{"conversation_id":"c","tool_name":"WebSearch"}""", T0, "cursor");
        Run("thinking", "afterAgentThought", """{"conversation_id":"c"}""", T0 + 30_000, "cursor");
        var status = Status("c", "cursor")!;
        Assert.Equal("awaiting_input", status.State);
        Assert.Equal(T0, status.Ts);
    }

    [Fact]
    public void WorkspaceRootsBeatCwd()
    {
        Run("thinking", "beforeSubmitPrompt", """{"conversation_id":"c","workspace_roots":["file:///c:/src/app/"],"cwd":"/x"}""", T0, "cursor");
        Assert.Equal("app", Status("c", "cursor")!.Project);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    public void MalformedInputStillWritesTheInstallersState(string stdin)
    {
        Run("stopped", "Stop", stdin, T0);
        Assert.Equal("stopped", Status("default")!.State);
    }

    [Fact]
    public void CorruptStatusFileIsOverwrittenNotFatal()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, "claude-s.json"), """{"state":"executing","ts":"soon","turn_tool_calls":-4}""");
        Run("stopped", "Stop", """{"session_id":"s"}""", T0);
        Assert.Equal("stopped", Status("s")!.State);
    }

    [Fact]
    public void NoTempFilesAreLeftBehind()
    {
        for (var i = 0; i < 5; i++) Run("thinking", "PostToolUse", """{"session_id":"s"}""", T0 + i * 5_000);
        Assert.Empty(Directory.EnumerateFiles(Dir, "*.tmp"));
        Assert.True(File.Exists(Path.Combine(Dir, StatusPaths.LockFileName)));
    }

    [Fact]
    public void ReadAllSkipsDotFilesAndGarbage()
    {
        Directory.CreateDirectory(Dir);
        Run("stopped", "Stop", """{"session_id":"good"}""", T0);
        File.WriteAllText(Path.Combine(Dir, ".kannu-x.json"), """{"state":"stopped","ts":1}""");
        File.WriteAllText(Path.Combine(Dir, "bad.json"), "{{{");
        File.WriteAllText(Path.Combine(Dir, "huge.json"), new string(' ', 70_000));
        Assert.Equal(["claude-good"], HookSessionReader.ReadFiles(Dir).Select(r => r.Key));
    }

    [Fact]
    public void ReadAllOnAMissingDirectoryIsEmpty() => Assert.Empty(HookSessionReader.ReadFiles(Dir));
}
