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
using System.Windows.Interop;
using Kannu.App.Interop;
using Kannu.Core;

namespace Kannu.App;

/// <summary>
/// The global shortcuts: Ctrl+Alt+K opens and closes the notch (macOS: ⇧⌘I, which on Windows is
/// every browser's and editor's developer tools). Registered only while the setting is on, on a
/// message-only window of its own.
/// </summary>
internal sealed class ShortcutManager : IDisposable
{
    public const string ToggleNotchText = "Ctrl+Alt+K";

    public const string BrightnessText = "Ctrl+Alt+F1 / F2";

    private const int WM_HOTKEY = 0x0312;
    private const int ToggleNotchId = 1, BrightnessDownId = 2, BrightnessUpId = 3;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    private const uint VK_K = 0x4B, VK_F1 = 0x70, VK_F2 = 0x71;

    private readonly HwndSource _window;
    private readonly Action _toggleNotch;
    private readonly Action<int> _brightness;
    private readonly HashSet<int> _registered = [];

    /// <summary>The notch shortcut is on but another app already owns the keys.</summary>
    public static bool Taken { get; private set; }

    /// <summary>The brightness shortcuts are on but another app already owns the keys.</summary>
    public static bool BrightnessTaken { get; private set; }

    public ShortcutManager(SettingsStore settings, Action toggleNotch, Action<int> brightness)
    {
        _toggleNotch = toggleNotch;
        _brightness = brightness;
        // HWND_MESSAGE: no window on screen, only a message queue.
        _window = new HwndSource(new HwndSourceParameters("KannuShortcuts") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _window.AddHook(Hook);
        Apply(settings.Current);
        settings.Changed += Apply;
    }

    private void Apply(AppSettings settings)
    {
        Taken = !Want(ToggleNotchId, settings.ShortcutsEnabled, VK_K) && settings.ShortcutsEnabled;
        var down = Want(BrightnessDownId, settings.BrightnessShortcuts, VK_F1);
        var up = Want(BrightnessUpId, settings.BrightnessShortcuts, VK_F2);
        BrightnessTaken = settings.BrightnessShortcuts && !(down && up);
        if (Taken) Diagnostics.Info("shortcut Ctrl+Alt+K is taken by another app");
        if (BrightnessTaken) Diagnostics.Info("brightness shortcuts are taken by another app");
    }

    /// <summary>Registers or unregisters one hotkey; true when it is registered (or not wanted).</summary>
    private bool Want(int id, bool wanted, uint key)
    {
        if (wanted == _registered.Contains(id)) return true;
        if (!wanted)
        {
            NativeMethods.UnregisterHotKey(_window.Handle, id);
            _registered.Remove(id);
            return true;
        }
        if (!NativeMethods.RegisterHotKey(_window.Handle, id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, key)) return false;
        _registered.Add(id);
        return true;
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WM_HOTKEY) return IntPtr.Zero;
        switch (wParam.ToInt32())
        {
            case ToggleNotchId:
                handled = true;
                _toggleNotch();
                break;
            case BrightnessDownId:
                handled = true;
                _brightness(-1);
                break;
            case BrightnessUpId:
                handled = true;
                _brightness(+1);
                break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _registered) NativeMethods.UnregisterHotKey(_window.Handle, id);
        _window.Dispose();
    }
}
