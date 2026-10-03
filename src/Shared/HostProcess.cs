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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Kannu.Core;

namespace Kannu.Shared;

/// <summary>
/// Click-through's Win32 side, compiled into both the hook (which records where an agent's window is)
/// and the app (which brings it forward). The rules live in <see cref="HostWindow"/>. Every call
/// fails soft: a process that exits mid-walk or is not ours to query just ends the search.
/// </summary>
internal static unsafe partial class HostProcess
{
    /// <summary>
    /// For the hook: the classic console window it runs in, else the nearest ancestor owning a window.
    /// Windows Terminal and editors give their shells a hidden pseudo-console, so the walk finds them.
    /// </summary>
    public static HookHost? FindForHook()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var console = Native.GetConsoleWindow();
            if (console != IntPtr.Zero && Native.IsWindowVisible(console)) return new HookHost(null, null, console.ToInt64());
            var owners = TopLevelWindows().Select(w => w.Pid).ToHashSet();
            if (HostWindow.FindHostPid(Environment.ProcessId, Describe, owners.Contains) is not { } pid) return null;
            return new HookHost(pid, Describe(pid)?.Name, null);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// For the app: brings the session's window forward. False when no window could be found (the
    /// agent's host has exited, or it never had one).
    /// </summary>
    public static bool Activate(AgentSession session)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (session.HostWindow is { } recorded)
        {
            var hwnd = new IntPtr(recorded);
            if (Native.IsWindow(hwnd) && Native.IsWindowVisible(hwnd)) return Focus(hwnd);
        }

        var windows = TopLevelWindows();
        var owners = windows.Select(w => w.Pid).ToHashSet();
        if (session.HostPid is { } pid && Describe(pid) is { } info && HostWindow.SameProgram(session.HostName, info.Name)
            && HostWindow.FindHostPid(pid, Describe, owners.Contains) is { } host)
        {
            return FocusBest(windows.Where(w => w.Pid == host).ToList(), session.ProjectName);
        }

        // No recorded host: the provider's own app, when it runs (Cursor, VS Code, Claude Desktop, ...).
        var appPids = new HashSet<int>();
        foreach (var name in ProviderApps.For(session.Provider).ProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process) appPids.Add(process.Id);
            }
        }
        return FocusBest(windows.Where(w => appPids.Contains(w.Pid)).ToList(), session.ProjectName);
    }

    private static bool FocusBest(List<(IntPtr Handle, int Pid)> candidates, string? project)
    {
        if (candidates.Count == 0) return false;
        var titles = candidates.Select(c => Title(c.Handle)).ToList();
        return Focus(candidates[HostWindow.Pick(titles, project)].Handle);
    }

    /// <summary>
    /// Restores and raises the window. Kannu's notch never takes focus, so when Windows refuses the
    /// plain call, the input queue of the window in front is borrowed for the moment of the switch.
    /// </summary>
    private static bool Focus(IntPtr hwnd)
    {
        if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, 9 /* SW_RESTORE */);
        if (Native.SetForegroundWindow(hwnd)) return true;
        var front = Native.GetForegroundWindow();
        var frontThread = Native.GetWindowThreadProcessId(front, out _);
        var ourThread = Native.GetCurrentThreadId();
        var attached = frontThread != 0 && frontThread != ourThread && Native.AttachThreadInput(ourThread, frontThread, true);
        Native.BringWindowToTop(hwnd);
        var done = Native.SetForegroundWindow(hwnd);
        if (attached) Native.AttachThreadInput(ourThread, frontThread, false);
        return done;
    }

    /// <summary>Visible, titled, unowned top-level windows that are not tool windows, front to back.</summary>
    private static List<(IntPtr Handle, int Pid)> TopLevelWindows()
    {
        var result = new List<(IntPtr, int)>();
        var hwnd = Native.GetTopWindow(IntPtr.Zero);
        for (var guard = 0; hwnd != IntPtr.Zero && guard < 4096; guard++)
        {
            if (Native.IsWindowVisible(hwnd) && Native.GetWindow(hwnd, 4 /* GW_OWNER */) == IntPtr.Zero
                && Native.GetWindowTextLengthW(hwnd) > 0
                && (Native.GetWindowLongPtrW(hwnd, -20 /* GWL_EXSTYLE */).ToInt64() & 0x80 /* WS_EX_TOOLWINDOW */) == 0)
            {
                Native.GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != 0) result.Add((hwnd, (int)pid));
            }
            hwnd = Native.GetWindow(hwnd, 2 /* GW_HWNDNEXT */);
        }
        return result;
    }

    private static string Title(IntPtr hwnd)
    {
        var buffer = stackalloc char[512];
        var length = Native.GetWindowTextW(hwnd, buffer, 512);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    /// <summary>Parent pid, program name and start time, from a query-only handle (works on elevated processes too).</summary>
    public static HostWindow.ProcessInfo? Describe(int pid)
    {
        if (pid <= 4) return null;
        var handle = Native.OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            ProcessBasicInformation basic;
            if (Native.NtQueryInformationProcess(handle, 0, &basic, sizeof(ProcessBasicInformation), out _) != 0) return null;
            if (!Native.GetProcessTimes(handle, out var created, out _, out _, out _)) return null;
            var buffer = stackalloc char[1024];
            var size = 1024;
            if (!Native.QueryFullProcessImageNameW(handle, 0, buffer, ref size)) return null;
            var name = Path.GetFileNameWithoutExtension(new string(buffer, 0, size));
            return new HostWindow.ProcessInfo((int)basic.InheritedFromUniqueProcessId.ToInt64(), name, created / 10_000);
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    private static partial class Native
    {
        [LibraryImport("kernel32.dll")]
        internal static partial IntPtr GetConsoleWindow();

        [LibraryImport("kernel32.dll")]
        internal static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CloseHandle(IntPtr handle);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool QueryFullProcessImageNameW(IntPtr process, int flags, char* name, ref int size);

        [LibraryImport("kernel32.dll")]
        internal static partial uint GetCurrentThreadId();

        [LibraryImport("ntdll.dll")]
        internal static partial int NtQueryInformationProcess(IntPtr process, int infoClass, void* info, int length, out int returned);

        [LibraryImport("user32.dll")]
        internal static partial IntPtr GetTopWindow(IntPtr parent);

        [LibraryImport("user32.dll")]
        internal static partial IntPtr GetWindow(IntPtr hwnd, uint command);

        [LibraryImport("user32.dll")]
        internal static partial IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

        [LibraryImport("user32.dll")]
        internal static partial int GetWindowTextLengthW(IntPtr hwnd);

        [LibraryImport("user32.dll")]
        internal static partial int GetWindowTextW(IntPtr hwnd, char* text, int max);

        [LibraryImport("user32.dll")]
        internal static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsWindow(IntPtr hwnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsWindowVisible(IntPtr hwnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool IsIconic(IntPtr hwnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ShowWindow(IntPtr hwnd, int command);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetForegroundWindow(IntPtr hwnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool BringWindowToTop(IntPtr hwnd);

        [LibraryImport("user32.dll")]
        internal static partial IntPtr GetForegroundWindow();

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool AttachThreadInput(uint attach, uint to, [MarshalAs(UnmanagedType.Bool)] bool doAttach);
    }
}
