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
using System.Windows.Interop;
using Kannu.App.Interop;
using Kannu.Core;

namespace Kannu.App;

/// <summary>
/// The global shortcut: Ctrl+Alt+K opens and closes the notch (macOS: ⇧⌘I, which on Windows is
/// every browser's and editor's developer tools). Registered only while the setting is on, on a
/// message-only window of its own.
/// </summary>
internal sealed class ShortcutManager : IDisposable
{
    public const string ToggleNotchText = "Ctrl+Alt+K";

    private const int WM_HOTKEY = 0x0312;
    private const int ToggleNotchId = 1;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    private const uint VK_K = 0x4B;

    private readonly HwndSource _window;
    private readonly Action _toggleNotch;
    private bool _registered;

    /// <summary>The shortcut is on but another app already owns the keys.</summary>
    public static bool Taken { get; private set; }

    public ShortcutManager(SettingsStore settings, Action toggleNotch)
    {
        _toggleNotch = toggleNotch;
        // HWND_MESSAGE: no window on screen, only a message queue.
        _window = new HwndSource(new HwndSourceParameters("KannuShortcuts") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _window.AddHook(Hook);
        Apply(settings.Current);
        settings.Changed += Apply;
    }

    private void Apply(AppSettings settings)
    {
        if (settings.ShortcutsEnabled == _registered && !(settings.ShortcutsEnabled && Taken)) return;
        if (_registered) NativeMethods.UnregisterHotKey(_window.Handle, ToggleNotchId);
        _registered = settings.ShortcutsEnabled
                      && NativeMethods.RegisterHotKey(_window.Handle, ToggleNotchId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_K);
        Taken = settings.ShortcutsEnabled && !_registered;
        if (Taken) Diagnostics.Info("shortcut Ctrl+Alt+K is taken by another app");
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_HOTKEY && wParam.ToInt32() == ToggleNotchId)
        {
            handled = true;
            _toggleNotch();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered) NativeMethods.UnregisterHotKey(_window.Handle, ToggleNotchId);
        _window.Dispose();
    }
}
