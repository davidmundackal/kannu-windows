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
using Xunit;
using static Kannu.Core.HostWindow;

namespace Kannu.Core.Tests;

public class HostWindowTests
{
    // kannu-hook (40) <- node (30) <- pwsh (20) <- WindowsTerminal (10) <- explorer (1)
    private static readonly Dictionary<int, ProcessInfo> Terminal = new()
    {
        [40] = new(30, "kannu-hook", 400),
        [30] = new(20, "node", 300),
        [20] = new(10, "pwsh", 200),
        [10] = new(1, "WindowsTerminal", 100),
        [1] = new(0, "explorer", 10),
    };

    private static ProcessInfo? From(Dictionary<int, ProcessInfo> tree, int pid) => tree.TryGetValue(pid, out var p) ? p : null;

    [Fact]
    public void WalksUpToTheFirstProcessWithAWindow() =>
        Assert.Equal(10, FindHostPid(40, pid => From(Terminal, pid), pid => pid is 10 or 1));

    [Fact]
    public void TheStartItselfCountsWhenItHasAWindow() =>
        Assert.Equal(30, FindHostPid(30, pid => From(Terminal, pid), pid => pid == 30));

    [Fact]
    public void NeverFocusesTheShell() =>
        Assert.Null(FindHostPid(40, pid => From(Terminal, pid), pid => pid == 1));

    [Fact]
    public void AReusedParentPidBreaksTheChain()
    {
        var tree = new Dictionary<int, ProcessInfo>(Terminal) { [20] = new(10, "pwsh", 999) };
        Assert.Null(FindHostPid(40, pid => From(tree, pid), pid => pid == 10));
    }

    [Fact]
    public void AMissingProcessBreaksTheChain()
    {
        var tree = new Dictionary<int, ProcessInfo>(Terminal);
        tree.Remove(20);
        Assert.Null(FindHostPid(40, pid => From(tree, pid), pid => pid == 10));
    }

    [Fact]
    public void ACycleEnds()
    {
        var tree = new Dictionary<int, ProcessInfo> { [5] = new(6, "a", 10), [6] = new(5, "b", 10) };
        Assert.Null(FindHostPid(5, pid => From(tree, pid), _ => false));
    }

    [Fact]
    public void PicksTheWindowNamingTheProject()
    {
        Assert.Equal(1, Pick(["notes.md - other - Visual Studio Code", "app.ts - Kannu - Visual Studio Code"], "kannu"));
        Assert.Equal(0, Pick(["a", "b"], "missing"));
        Assert.Equal(0, Pick(["a", "b"], null));
    }

    [Fact]
    public void ARecordedPidIsTrustedOnlyForTheSameProgram()
    {
        Assert.True(SameProgram("Code", "code"));
        Assert.False(SameProgram("Code", "notepad"));
        Assert.False(SameProgram("Code", null));
        Assert.True(SameProgram(null, "anything"));
    }
}
