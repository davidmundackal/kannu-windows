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
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Kannu.App.Interop;

namespace Kannu.App;

/// <summary>
/// Puts Windows' own light/dark mode and accent colour into the brushes <c>Theme/Theme.xaml</c> uses,
/// and keeps them current when the user changes either. Colours follow the Windows 11 Settings app.
/// </summary>
internal static class ThemeManager
{
    private static readonly Color DefaultAccent = Color.FromRgb(0x00, 0x67, 0xC0);

    public static bool IsDark { get; private set; }

    public static void Initialize(Application app)
    {
        Apply(app);
        // SystemEvents raise on their own thread.
        SystemEvents.UserPreferenceChanged += (_, _) => app.Dispatcher.BeginInvoke(() => Apply(app));
    }

    /// <summary>A dark title bar in dark mode (Windows 10 20H1 and later; ignored elsewhere).</summary>
    public static void FollowTitleBar(Window window)
    {
        void Set()
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero) NativeMethods.SetImmersiveDarkMode(handle, IsDark);
        }
        window.SourceInitialized += (_, _) => Set();
        SystemEvents.UserPreferenceChanged += Changed;
        window.Closed += (_, _) => SystemEvents.UserPreferenceChanged -= Changed;

        void Changed(object? sender, UserPreferenceChangedEventArgs e) => window.Dispatcher.BeginInvoke(Set);
    }

    private static void Apply(Application app)
    {
        IsDark = !AppsUseLightTheme();
        var accent = ReadAccent() ?? DefaultAccent;
        // Windows 11 shows a lighter accent on dark surfaces, with dark text on it.
        var fill = IsDark ? Mix(accent, Colors.White, 0.45) : accent;
        var r = app.Resources;
        Set(r, "Kannu.Accent", fill);
        Set(r, "Kannu.AccentText", IsDark ? Mix(accent, Colors.White, 0.55) : Mix(accent, Colors.Black, 0.1));
        Set(r, "Kannu.OnAccent", IsDark ? Colors.Black : Colors.White);
        if (IsDark)
        {
            Set(r, "Kannu.WindowBackground", 0x20, 0x20, 0x20);
            Set(r, "Kannu.Card", 0x2B, 0x2B, 0x2B);
            Set(r, "Kannu.CardStroke", 0x1D, 0x1D, 0x1D);
            Set(r, "Kannu.Divider", 0x1D, 0x1D, 0x1D);
            Set(r, "Kannu.Text", 0xFF, 0xFF, 0xFF);
            Set(r, "Kannu.TextSecondary", 0xC5, 0xC5, 0xC5);
            Set(r, "Kannu.ControlFill", 0x37, 0x37, 0x37);
            Set(r, "Kannu.ControlHover", 0x3C, 0x3C, 0x3C);
            Set(r, "Kannu.ControlStroke", 0x45, 0x45, 0x45);
            Set(r, "Kannu.NavHover", 0x2D, 0x2D, 0x2D);
            Set(r, "Kannu.NavSelected", 0x2D, 0x2D, 0x2D);
            Set(r, "Kannu.ToggleOffStroke", 0xCF, 0xCF, 0xCF);
            Set(r, "Kannu.ToggleOffKnob", 0xCF, 0xCF, 0xCF);
        }
        else
        {
            Set(r, "Kannu.WindowBackground", 0xF3, 0xF3, 0xF3);
            Set(r, "Kannu.Card", 0xFB, 0xFB, 0xFB);
            Set(r, "Kannu.CardStroke", 0xE5, 0xE5, 0xE5);
            Set(r, "Kannu.Divider", 0xEA, 0xEA, 0xEA);
            Set(r, "Kannu.Text", 0x1A, 0x1A, 0x1A);
            Set(r, "Kannu.TextSecondary", 0x5D, 0x5D, 0x5D);
            Set(r, "Kannu.ControlFill", 0xFF, 0xFF, 0xFF);
            Set(r, "Kannu.ControlHover", 0xF6, 0xF6, 0xF6);
            Set(r, "Kannu.ControlStroke", 0xD6, 0xD6, 0xD6);
            Set(r, "Kannu.NavHover", 0xEA, 0xEA, 0xEA);
            Set(r, "Kannu.NavSelected", 0xEA, 0xEA, 0xEA);
            Set(r, "Kannu.ToggleOffStroke", 0x86, 0x86, 0x86);
            Set(r, "Kannu.ToggleOffKnob", 0x5D, 0x5D, 0x5D);
        }
    }

    private static bool AppsUseLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }

    /// <summary>The DWM accent, stored as 0xAABBGGRR.</summary>
    private static Color? ReadAccent()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
        if (key?.GetValue("AccentColor") is not int abgr) return null;
        var value = unchecked((uint)abgr);
        return Color.FromRgb((byte)value, (byte)(value >> 8), (byte)(value >> 16));
    }

    private static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * amount),
        (byte)(a.G + (b.G - a.G) * amount),
        (byte)(a.B + (b.B - a.B) * amount));

    private static void Set(ResourceDictionary resources, string key, byte r, byte g, byte b) =>
        Set(resources, key, Color.FromRgb(r, g, b));

    private static void Set(ResourceDictionary resources, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        resources[key] = brush;
    }
}
