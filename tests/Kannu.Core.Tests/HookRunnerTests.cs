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
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kannu-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private HookDecision? Run(string json, long now, string? forced = null) =>
        HookRunner.Run("claude", forced, json, _dir, now);

    private StatusRecord? Status(string session) => StatusStore.Read(Path.Combine(_dir, $"claude-{session}.json"));

    [Fact]
    public void ASessionLifecycleWritesThenDeletesItsFile()
    {
        Run("""{"hook_event_name":"SessionStart","session_id":"s1","source":"startup","cwd":"/home/me/kannu"}""", 1_000);
        Assert.Equal("idle", Status("s1")!.State);
        Assert.Equal("kannu", Status("s1")!.Project);

        Run("""{"hook_event_name":"UserPromptSubmit","session_id":"s1"}""", 10_000);
        Assert.Equal("thinking", Status("s1")!.State);
        Assert.Equal("kannu", Status("s1")!.Project); // carried when the event has no cwd

        Run("""{"hook_event_name":"PermissionRequest","session_id":"s1","tool_name":"Bash"}""", 20_000);
        Assert.Equal("awaiting_input", Status("s1")!.State);

        Run("""{"hook_event_name":"Stop","session_id":"s1"}""", 30_000);
        var stopped = Status("s1")!;
        Assert.Equal("stopped", stopped.State);
        Assert.Equal(30_000, stopped.Ts);
        Assert.Equal("Stop", stopped.HookEvent);
        Assert.Equal("claude", stopped.Provider);

        Run("""{"hook_event_name":"SessionEnd","session_id":"s1"}""", 40_000);
        Assert.False(File.Exists(Path.Combine(_dir, "claude-s1.json")));
    }

    [Fact]
    public void RacingGenericGroupDoesNotBuryYellow()
    {
        Run("""{"hook_event_name":"PreToolUse","session_id":"s","tool_name":"AskUserQuestion"}""", 5_000);
        Run("""{"hook_event_name":"PreToolUse","session_id":"s","tool_name":"Bash"}""", 5_010);
        Assert.Equal("awaiting_input", Status("s")!.State);
    }

    [Fact]
    public void IgnoredEventsTouchNothing()
    {
        Run("""{"hook_event_name":"Notification","session_id":"s","notification_type":"idle_prompt"}""", 1_000);
        Assert.False(Directory.Exists(_dir) && Directory.EnumerateFiles(_dir, "*.json").Any());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{")]
    public void MalformedInputIsDropped(string stdin)
    {
        Assert.Null(Run(stdin, 1_000));
    }

    [Fact]
    public void OversizedInputIsDropped()
    {
        Assert.Null(Run(new string(' ', HookRunner.MaxInputChars + 1), 1_000));
    }

    [Fact]
    public void CorruptStatusFileIsOverwrittenNotFatal()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "claude-s.json"), """{"state":"executing","ts":"soon"}""");
        Run("""{"hook_event_name":"Stop","session_id":"s"}""", 1_000);
        Assert.Equal("stopped", Status("s")!.State);
    }

    [Fact]
    public void NoTempFilesAreLeftBehind()
    {
        for (var i = 0; i < 5; i++) Run("""{"hook_event_name":"PostToolUse","session_id":"s"}""", 1_000 + i * 5_000);
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp"));
        Assert.True(File.Exists(Path.Combine(_dir, StatusPaths.LockFileName)));
    }

    [Fact]
    public void ReadAllSkipsDotFilesAndGarbage()
    {
        Directory.CreateDirectory(_dir);
        Run("""{"hook_event_name":"Stop","session_id":"good"}""", 1_000);
        File.WriteAllText(Path.Combine(_dir, ".kannu-x.json"), """{"state":"stopped","ts":1}""");
        File.WriteAllText(Path.Combine(_dir, "bad.json"), "{{{");
        File.WriteAllText(Path.Combine(_dir, "huge.json"), new string(' ', 70_000));
        Assert.Equal(["claude-good"], StatusStore.ReadAll(_dir).Select(r => r.Key));
    }

    [Fact]
    public void ReadAllOnAMissingDirectoryIsEmpty()
    {
        Assert.Empty(StatusStore.ReadAll(_dir));
    }
}
