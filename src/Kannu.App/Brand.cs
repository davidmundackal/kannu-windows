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

using System.Drawing.Text;
using System.Linq;

namespace Kannu.App;

/// <summary>
/// The traffic-light colours, the same values as Kannu for macOS
/// (<c>AgentTrafficLightColors.swift</c>), so a light looks the same on both platforms.
/// </summary>
internal static class KannuColors
{
    public static readonly System.Drawing.Color Green = System.Drawing.Color.FromArgb(0x22, 0xC5, 0x5E);
    public static readonly System.Drawing.Color Yellow = System.Drawing.Color.FromArgb(0xFA, 0xCC, 0x15);
    public static readonly System.Drawing.Color Red = System.Drawing.Color.FromArgb(0xEF, 0x44, 0x44);
    public static readonly System.Drawing.Color Dim = System.Drawing.Color.FromArgb(0x6E, 0x6E, 0x73);

    public static System.Windows.Media.SolidColorBrush Brush(System.Drawing.Color color)
    {
        var brush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// Kannu's eye — കണ്ണ് means "eye" — as macOS Kannu shows it in the menu bar (SF Symbol
/// <c>eye.fill</c>). SF Symbols are licensed for Apple platforms only, so on Windows the same eye is
/// drawn from Windows' own icon font: the <c>View</c> glyph of Segoe Fluent Icons (Windows 11) or Segoe
/// MDL2 Assets (Windows 10).
/// </summary>
internal static class EyeGlyph
{
    public const string Text = "";

    /// <summary>For WPF, which falls back through the list itself.</summary>
    public const string WpfFontFamily = "Segoe Fluent Icons, Segoe MDL2 Assets";

    /// <summary>For GDI+, which silently substitutes a missing font, so the installed one is picked here.</summary>
    public static string FontFamily { get; } = PickInstalled("Segoe Fluent Icons", "Segoe MDL2 Assets");

    private static string PickInstalled(params string[] names)
    {
        using var installed = new InstalledFontCollection();
        var families = installed.Families.Select(f => f.Name).ToHashSet(System.StringComparer.OrdinalIgnoreCase);
        return names.FirstOrDefault(families.Contains) ?? names[^1];
    }
}
