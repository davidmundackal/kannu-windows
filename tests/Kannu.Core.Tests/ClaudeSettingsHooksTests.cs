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

using System.Text.Json.Nodes;
using Kannu.Core;

namespace Kannu.Core.Tests;

public class ClaudeSettingsHooksTests
{
    private const string Exe = @"C:\Users\me\AppData\Local\Kannu\bin\kannu-hook.exe";

    private const string UserSettings = """
        {
          // users keep comments in here
          "model": "opus",
          "hooks": {
            "PreToolUse": [
              { "matcher": "Bash", "hooks": [ { "type": "command", "command": "my-linter.exe" } ] }
            ],
            "Stop": [
              { "hooks": [ { "type": "command", "command": "say done" } ] }
            ]
          },
        }
        """;

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void InstallIntoEmptySettingsAddsEveryEvent()
    {
        var hooks = Parse(ClaudeSettingsHooks.Install(null, Exe))["hooks"]!.AsObject();
        Assert.Equal(
            ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure",
             "PermissionRequest", "Notification", "Stop", "StopFailure", "SessionEnd"],
            hooks.Select(h => h.Key));
    }

    [Fact]
    public void CommandUsesForwardSlashesAndQuotes()
    {
        Assert.Equal("\"C:/Users/me/AppData/Local/Kannu/bin/kannu-hook.exe\" claude", ClaudeSettingsHooks.Command(Exe));
        Assert.EndsWith("claude --state awaiting_input", ClaudeSettingsHooks.Command(Exe, RawState.AwaitingInput));
    }

    [Fact]
    public void NotificationGroupIsMatcherScopedAndForcesYellow()
    {
        var group = Parse(ClaudeSettingsHooks.Install(null, Exe))["hooks"]!["Notification"]![0]!;
        Assert.Equal("permission_prompt|idle_prompt|elicitation_dialog", group["matcher"]!.GetValue<string>());
        Assert.EndsWith("--state awaiting_input", group["hooks"]![0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void InstallPreservesTheUsersOwnHooksAndSettings()
    {
        var root = Parse(ClaudeSettingsHooks.Install(UserSettings, Exe));
        Assert.Equal("opus", root["model"]!.GetValue<string>());
        var pre = root["hooks"]!["PreToolUse"]!.AsArray();
        Assert.Equal(2, pre.Count);
        Assert.Equal("my-linter.exe", pre[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal(2, root["hooks"]!["Stop"]!.AsArray().Count);
    }

    [Fact]
    public void InstallTwiceIsIdempotent()
    {
        var once = ClaudeSettingsHooks.Install(UserSettings, Exe);
        Assert.Equal(once, ClaudeSettingsHooks.Install(once, Exe));
    }

    [Fact]
    public void InstallReplacesAHookAtAnOldPath()
    {
        var old = ClaudeSettingsHooks.Install(null, @"D:\old\kannu-hook.exe");
        var updated = ClaudeSettingsHooks.Install(old, Exe);
        Assert.DoesNotContain("D:/old", updated);
        Assert.Single(Parse(updated)["hooks"]!["Stop"]!.AsArray());
    }

    [Fact]
    public void RemoveRestoresExactlyWhatTheUserHad()
    {
        var removed = Parse(ClaudeSettingsHooks.Remove(ClaudeSettingsHooks.Install(UserSettings, Exe)));
        var original = Parse(ClaudeSettingsHooks.Remove(UserSettings));
        Assert.True(JsonNode.DeepEquals(original, removed));
        Assert.Single(removed["hooks"]!["PreToolUse"]!.AsArray());
        Assert.Null(removed["hooks"]!["SessionEnd"]);
    }

    [Fact]
    public void RemoveFromKannuOnlySettingsDropsTheHooksKey()
    {
        var removed = Parse(ClaudeSettingsHooks.Remove(ClaudeSettingsHooks.Install("""{"model":"x"}""", Exe)));
        Assert.Null(removed["hooks"]);
        Assert.Equal("x", removed["model"]!.GetValue<string>());
    }

    [Fact]
    public void IsInstalledReflectsState()
    {
        Assert.False(ClaudeSettingsHooks.IsInstalled(UserSettings));
        Assert.True(ClaudeSettingsHooks.IsInstalled(ClaudeSettingsHooks.Install(UserSettings, Exe)));
        Assert.False(ClaudeSettingsHooks.IsInstalled("not json"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("\"string\"")]
    public void NonObjectSettingsAreRefusedNotOverwritten(string json)
    {
        Assert.Throws<InvalidDataException>(() => ClaudeSettingsHooks.Install(json, Exe));
        Assert.Throws<InvalidDataException>(() => ClaudeSettingsHooks.Remove(json));
    }
}
