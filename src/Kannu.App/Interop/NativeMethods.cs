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
using System.Runtime.InteropServices;

namespace Kannu.App.Interop;

internal static class NativeMethods
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>
    /// WS_EX_NOACTIVATE: clicking or hovering the notch never steals focus from the editor or terminal.
    /// WS_EX_TOOLWINDOW: no taskbar button and no Alt+Tab entry.
    /// </summary>
    public static void MakeNonActivatingToolWindow(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
    }

    // MiniDumpWithPrivateReadWriteMemory | MiniDumpWithThreadInfo | MiniDumpWithDataSegs: enough heap
    // for a .NET debugger to rebuild the managed stacks of a frozen UI thread.
    private const int MiniDumpType = 0x200 | 0x1000 | 0x1;

    /// <summary>A memory dump of this process, for a freeze report. False if it could not be written.</summary>
    public static bool WriteMiniDump(string path)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            using var file = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write);
            return MiniDumpWriteDump(process.Handle, (uint)process.Id, file.SafeFileHandle.DangerousGetHandle(),
                MiniDumpType, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [DllImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(IntPtr process, uint processId, IntPtr file, int dumpType,
        IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>Dark title bar; harmless where the attribute is unknown.</summary>
    public static void SetImmersiveDarkMode(IntPtr hwnd, bool dark)
    {
        var value = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>The pointer in physical pixels (the app is per-monitor DPI aware).</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    // ---- Placement (multiple monitors) ----

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }

    /// <summary>
    /// The work area (taskbar excluded) of the monitor under the pointer, or of the primary monitor,
    /// in physical pixels, with that monitor's scale (1.0 at 96 DPI).
    /// </summary>
    public static (RECT Work, double Scale)? MonitorWorkArea(bool underPointer)
    {
        var point = new POINT();
        if (underPointer && !GetCursorPos(out point)) point = new POINT();
        // MONITOR_DEFAULTTOPRIMARY: (0,0) is always on the primary monitor.
        var monitor = MonitorFromPoint(underPointer ? point : new POINT(), 1);
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfoW(monitor, ref info)) return null;
        var scale = GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
        return (info.Work, scale);
    }

    /// <summary>Moves the window's top-left corner, in physical pixels, without resizing or activating it.</summary>
    public static void MoveWindow(IntPtr hwnd, int x, int y) =>
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, 0x0001 /* NOSIZE */ | 0x0004 /* NOZORDER */ | 0x0010 /* NOACTIVATE */);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    // ---- Fullscreen ----

    /// <summary>
    /// A full-screen app, a full-screen game or a presentation is in front (QUNS_BUSY,
    /// QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_PRESENTATION_MODE): the notch keeps out of the way.
    /// </summary>
    public static bool IsFullscreenBusy() =>
        SHQueryUserNotificationState(out var state) == 0 && state is 2 or 3 or 4;

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    // ---- Global shortcuts ----

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
