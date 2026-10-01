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

public sealed class AgentHookInstallerTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "kannu-home-" + Guid.NewGuid().ToString("N"));
    private readonly AgentHookInstaller _installer;

    public AgentHookInstallerTests()
    {
        Directory.CreateDirectory(_home);
        var exe = Path.Combine(_home, "AppData", "Local", "Kannu", "bin", "kannu-hook.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");
        _installer = new AgentHookInstaller(new AgentHookLayout(_home), exe);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private static JsonObject Json(string path) => (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;

    private void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public static TheoryData<AgentProvider> Providers() => [.. Enum.GetValues<AgentProvider>()];

    [Theory]
    [MemberData(nameof(Providers))]
    public void InstallThenUninstallRoundTrips(AgentProvider provider)
    {
        Assert.False(_installer.IsInstalled(provider));
        _installer.Install(provider);
        Assert.True(_installer.IsInstalled(provider));
        _installer.Uninstall(provider);
        Assert.False(_installer.IsInstalled(provider));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void InstallTwiceIsIdempotent(AgentProvider provider)
    {
        _installer.Install(provider);
        var first = _installer.Layout.For(provider).Configs.Select(c => File.Exists(c.Path) ? File.ReadAllText(c.Path) : "").ToList();
        _installer.Install(provider);
        var second = _installer.Layout.For(provider).Configs.Select(c => File.Exists(c.Path) ? File.ReadAllText(c.Path) : "").ToList();
        Assert.Equal(first, second);
    }

    [Fact]
    public void ClaudeKeepsTheUsersOwnHooksAndSettings()
    {
        var settings = _installer.Layout.ClaudeSettings;
        Write(settings, """
            {"model":"opus","hooks":{"PreToolUse":[{"matcher":"Bash","hooks":[{"type":"command","command":"my-linter.exe"}]}]}}
            """);
        _installer.Install(AgentProvider.Claude);
        var root = Json(settings);
        Assert.Equal("opus", root["model"]!.GetValue<string>());
        Assert.Equal("my-linter.exe", root["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());

        _installer.Uninstall(AgentProvider.Claude);
        root = Json(settings);
        Assert.Single(root["hooks"]!["PreToolUse"]!.AsArray());
        Assert.Null(root["hooks"]!["Stop"]);
        Assert.True(File.Exists(settings + ".kannu-backup"));
    }

    [Fact]
    public void ClaudeMatcherGroupsCarryTheirKey()
    {
        _installer.Install(AgentProvider.Claude);
        var groups = Json(_installer.Layout.ClaudeSettings)["hooks"]!["PreToolUse"]!.AsArray();
        var gated = groups.Single(g => g!["matcher"] is not null)!;
        Assert.Equal("ExitPlanMode|AskUserQuestion", gated["matcher"]!.GetValue<string>());
        Assert.EndsWith("awaiting_input claude PreToolUse gated", gated["hooks"]![0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void CommandUsesForwardSlashesAndQuotesOnlyForSpaces()
    {
        var plain = new AgentHookInstaller(new AgentHookLayout(_home), @"C:\Users\me\AppData\Local\Kannu\bin\kannu-hook.exe");
        Assert.Equal("C:/Users/me/AppData/Local/Kannu/bin/kannu-hook.exe stopped claude Stop",
            plain.Command(AgentProvider.Claude, new HookEntry("Stop", "stopped")));
        var spaced = new AgentHookInstaller(new AgentHookLayout(_home), @"C:\Users\Jo Doe\kannu-hook.exe");
        Assert.StartsWith("\"C:/Users/Jo Doe/kannu-hook.exe\" ", spaced.Command(AgentProvider.Claude, new HookEntry("Stop", "stopped")));
    }

    [Fact]
    public void CursorUsesFlatEntriesAndAVersion()
    {
        _installer.Install(AgentProvider.Cursor);
        var root = Json(_installer.Layout.CursorHooks);
        Assert.Equal(1, root["version"]!.GetValue<int>());
        Assert.Contains("cursor stop", root["hooks"]!["stop"]![0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void GeminiHandlersAreNamedAndTimedInMilliseconds()
    {
        _installer.Install(AgentProvider.Gemini);
        var handler = Json(_installer.Layout.GeminiSettings)["hooks"]!["BeforeTool"]![0]!["hooks"]![0]!;
        Assert.Equal(AgentHookLayout.HandlerName, handler["name"]!.GetValue<string>());
        Assert.Equal(10_000, handler["timeout"]!.GetValue<int>());
    }

    [Fact]
    public void AntigravityMergesOnlyIntoOptionalFilesThatExist()
    {
        Write(_installer.Layout.AntigravityIdeHooks, """{"hooks":{"Stop":[{"hooks":[{"command":"theirs"}]}]}}""");
        _installer.Install(AgentProvider.Antigravity);
        Assert.True(File.Exists(_installer.Layout.AntigravityConfigHooks));
        Assert.False(File.Exists(_installer.Layout.AntigravityRootHooks));
        Assert.Equal(2, Json(_installer.Layout.AntigravityIdeHooks)["hooks"]!["Stop"]!.AsArray().Count);
    }

    [Fact]
    public void AntigravitySkipsABrokenOptionalFileButInstalls()
    {
        Write(_installer.Layout.AntigravityRootHooks, "{ broken");
        _installer.Install(AgentProvider.Antigravity);
        Assert.Equal("{ broken", File.ReadAllText(_installer.Layout.AntigravityRootHooks));
        Assert.True(_installer.IsInstalled(AgentProvider.Antigravity));
    }

    [Fact]
    public void CodexEnablesHooksInConfigToml()
    {
        _installer.Install(AgentProvider.Codex);
        Assert.Contains("[features]\nhooks = true", File.ReadAllText(_installer.Layout.CodexConfigToml));
    }

    [Theory]
    [InlineData("", "\n[features]\nhooks = true\n")]
    [InlineData("model = \"o3\"", "model = \"o3\"\n\n[features]\nhooks = true\n")]
    [InlineData("features.hooks = false\n", "features.hooks = true\n")]
    [InlineData("features.hooks = true\n", "features.hooks = true\n")]
    [InlineData("[features]\nhooks = false\nx = 1\n", "[features]\nhooks = true\nx = 1\n")]
    [InlineData("[features]\nx = 1\n[other]\n", "[features]\nhooks = true\nx = 1\n[other]\n")]
    public void CodexTomlEditIsMinimal(string before, string after) =>
        Assert.Equal(after, AgentHookInstaller.WithCodexHooksEnabled(before));

    [Fact]
    public void OpencodeUninstallLeavesAPluginThatIsNotKannus()
    {
        Write(_installer.Layout.OpencodePlugin, "// someone else's plugin");
        _installer.Uninstall(AgentProvider.Opencode);
        Assert.True(File.Exists(_installer.Layout.OpencodePlugin));
    }

    [Fact]
    public void OpencodePluginPointsAtTheHook()
    {
        _installer.Install(AgentProvider.Opencode);
        var plugin = File.ReadAllText(_installer.Layout.OpencodePlugin);
        Assert.Contains(OpencodePluginSource.VersionMarker, plugin);
        Assert.Contains("kannu-hook.exe", plugin);
        Assert.DoesNotContain("__KANNU_HOOK_PATH__", plugin);
    }

    [Theory]
    [InlineData("{ // comment\n \"a\": 1 }", "comments")]
    [InlineData("[]", "isn't valid JSON")]
    [InlineData("{ nope", "isn't valid JSON")]
    public void UnsafeSettingsAreRefusedAndUntouched(string text, string message)
    {
        Write(_installer.Layout.ClaudeSettings, text);
        var e = Assert.Throws<HookInstallException>(() => _installer.Install(AgentProvider.Claude));
        Assert.Contains(message, e.Message);
        Assert.Equal(text, File.ReadAllText(_installer.Layout.ClaudeSettings));
    }

    [Fact]
    public void AMacOSKannuEntryInASharedFileIsReplacedNotDuplicated()
    {
        Write(_installer.Layout.ClaudeSettings, """
            {"hooks":{"Stop":[{"hooks":[{"type":"command","command":"/Users/me/.claude/kannu-agent-status.sh stopped claude Stop"}]}]}}
            """);
        _installer.Install(AgentProvider.Claude);
        Assert.Single(Json(_installer.Layout.ClaudeSettings)["hooks"]!["Stop"]!.AsArray());
    }

    [Fact]
    public void NotInstalledWithoutTheHookExecutable()
    {
        _installer.Install(AgentProvider.Claude);
        File.Delete(_installer.HookExePath);
        Assert.False(_installer.IsInstalled(AgentProvider.Claude));
    }
}
