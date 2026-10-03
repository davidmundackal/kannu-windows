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

namespace Kannu.Core;

/// <summary>
/// Click-through's rules (D2: bring the agent's window forward, not an exact tab): which process owns
/// the window an agent runs in, and which of that process's windows is the agent's. Pure; the Win32
/// side (<c>src/Shared/HostProcess.cs</c>) feeds it.
/// </summary>
public static class HostWindow
{
    /// <summary>One process as the walk sees it.</summary>
    public readonly record struct ProcessInfo(int ParentPid, string Name, long StartedMs);

    /// <summary>
    /// The shell and session processes every desktop app descends from: reaching one means the agent
    /// has no window of its own, and its windows (the desktop, the taskbar) must never be focused.
    /// </summary>
    internal static readonly HashSet<string> StopAt = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "svchost", "services", "wininit", "winlogon", "csrss", "smss", "system", "idle",
        "sihost", "runtimebroker", "userinit", "dllhost",
    };

    private const int MaxDepth = 16;

    /// <summary>
    /// Walks up from <paramref name="startPid"/> (itself included) to the first process that has a
    /// window: an editor, Claude Desktop, Windows Terminal. Null when the chain breaks, reaches the
    /// shell, or a parent started after its child (its pid was reused by an unrelated process).
    /// </summary>
    public static int? FindHostPid(int startPid, Func<int, ProcessInfo?> describe, Func<int, bool> hasWindow)
    {
        var pid = startPid;
        var seen = new HashSet<int>();
        for (var depth = 0; depth < MaxDepth && pid > 0 && seen.Add(pid); depth++)
        {
            if (describe(pid) is not { } info || StopAt.Contains(info.Name)) return null;
            if (hasWindow(pid)) return pid;
            if (describe(info.ParentPid) is not { } parent || parent.StartedMs > info.StartedMs) return null;
            pid = info.ParentPid;
        }
        return null;
    }

    /// <summary>
    /// Of one process's windows (one editor process can own several), the one whose title names the
    /// agent's project; else the first, which is the most recently used.
    /// </summary>
    public static int Pick(IReadOnlyList<string> titles, string? project)
    {
        if (!string.IsNullOrWhiteSpace(project))
        {
            for (var i = 0; i < titles.Count; i++)
            {
                if (titles[i].Contains(project, StringComparison.OrdinalIgnoreCase)) return i;
            }
        }
        return 0;
    }

    /// <summary>
    /// A recorded host pid may only be trusted while it still runs the program that was recorded: a
    /// pid is reused once its process exits. A record without a name (passive detection, which sets
    /// the pid only while it is provably alive) is trusted as is.
    /// </summary>
    public static bool SameProgram(string? recorded, string? running) =>
        recorded is null || (running is not null && string.Equals(recorded, running, StringComparison.OrdinalIgnoreCase));
}
