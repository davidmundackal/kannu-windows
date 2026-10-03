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
/// Brightness in fine steps (macOS's ⌥⇧ quarter steps): 1/64 of the display's range per press,
/// at least one unit, clamped to the range.
/// </summary>
public static class BrightnessSteps
{
    public const int StepsPerRange = 64;

    public static int Next(int current, int min, int max, int direction)
    {
        if (max <= min) return min;
        var step = Math.Max(1, (int)Math.Round((max - min) / (double)StepsPerRange));
        return Math.Clamp(current + Math.Sign(direction) * step, min, max);
    }

    /// <summary>0 to 1, for the HUD's bar.</summary>
    public static double Fraction(int value, int min, int max) => max <= min ? 0 : Math.Clamp((value - min) / (double)(max - min), 0, 1);
}

/// <summary>
/// Which apps are capturing the screen right now, from what Windows 11 records for its screen-capture
/// permission (the CapabilityAccessManager consent store, the same place it records camera and
/// microphone use). Best effort: apps that capture with older methods (desktop duplication, GDI;
/// OBS by default) are not recorded there.
/// </summary>
public static class ScreenCapture
{
    /// <summary>The consent-store capabilities that track screen capture.</summary>
    public static readonly string[] Capabilities = ["graphicsCaptureProgrammatic", "graphicsCaptureWithoutBorder"];

    /// <param name="Key">A packaged app's family name, or a NonPackaged path with '#' for '\'.</param>
    /// <param name="StartFileTime">LastUsedTimeStart (FILETIME); 0 when never.</param>
    /// <param name="StopFileTime">LastUsedTimeStop (FILETIME); 0 while still in use.</param>
    public readonly record struct Usage(string Key, long StartFileTime, long StopFileTime);

    /// <summary>In use: started, and not stopped since.</summary>
    public static bool IsActive(Usage usage) => usage.StartFileTime > 0 && usage.StopFileTime < usage.StartFileTime;

    /// <summary>The apps capturing now, by a readable name, without Kannu itself.</summary>
    public static IReadOnlyList<string> ActiveApps(IEnumerable<Usage> usages) =>
        usages.Where(IsActive).Select(u => AppName(u.Key)).Where(n => n.Length > 0 && !n.Equals("Kannu", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>"C:#Program Files#obs-studio#bin#64bit#obs64.exe" → "obs64"; "Microsoft.ScreenSketch_8wekyb3d8bbwe" → "ScreenSketch".</summary>
    public static string AppName(string key)
    {
        if (key.Contains('#'))
        {
            var file = key[(key.LastIndexOf('#') + 1)..];
            return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
        }
        var family = key.Split('_')[0];
        var dot = family.LastIndexOf('.');
        return dot >= 0 && dot < family.Length - 1 ? family[(dot + 1)..] : family;
    }
}
