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
using Xunit;

namespace Kannu.Core.Tests;

public sealed class WslAgentsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kannu-wsl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    [Fact]
    public void DistrosAreReadFromUtf16Output()
    {
        var output = "U\0b\0u\0n\0t\0u\0\r\0\n\0d\0o\0c\0k\0e\0r\0-\0d\0e\0s\0k\0t\0o\0p\0\r\0\n\0D\0e\0b\0i\0a\0n\0\r\0\n\0";
        Assert.Equal(["Ubuntu", "Debian"], WslAgents.ParseDistros(output));
        Assert.Empty(WslAgents.ParseDistros(""));
    }

    [Fact]
    public void PathsCrossTheBoundary()
    {
        Assert.Equal("/mnt/c/Users/me/AppData/Local/Kannu/bin/kannu-hook.exe",
            WslAgents.ToMountPath(@"C:\Users\me\AppData\Local\Kannu\bin\kannu-hook.exe"));
        Assert.Null(WslAgents.ToMountPath(@"\\server\share\x"));
        Assert.Equal(@"\\wsl.localhost\Ubuntu\home\me", WslAgents.UncHome("Ubuntu", "/home/me\n"));
        Assert.Null(WslAgents.UncHome("Ubuntu", "not a path"));
        Assert.Null(WslAgents.UncHome(@"..\x", "/home/me"));
    }

    [Fact]
    public void TheHookCommandUsesTheLinuxPathButTheFileCheckUsesWindows()
    {
        var exe = Path.Combine(_root, "bin", "kannu-hook.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");
        var home = Path.Combine(_root, "wsl-home");
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        var installer = new AgentHookInstaller(new AgentHookLayout(home), exe) { CommandExePath = "/mnt/c/Users/me/AppData/Local/Kannu/bin/kannu-hook.exe" };

        Assert.True(WslAgents.IsPresent(installer.Layout, AgentProvider.Claude));
        Assert.False(WslAgents.IsPresent(installer.Layout, AgentProvider.Codex));
        installer.Install(AgentProvider.Claude);
        Assert.True(installer.IsInstalled(AgentProvider.Claude));
        var text = File.ReadAllText(installer.Layout.ClaudeSettings);
        Assert.Contains("/mnt/c/Users/me/AppData/Local/Kannu/bin/kannu-hook.exe thinking claude", text);
        Assert.DoesNotContain(_root.Replace('\\', '/'), text);
        Assert.NotNull(JsonNode.Parse(text)!["hooks"]);

        installer.Uninstall(AgentProvider.Claude);
        Assert.False(installer.IsInstalled(AgentProvider.Claude));
    }
}
