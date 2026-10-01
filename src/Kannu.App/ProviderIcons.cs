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
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Kannu.Core;

namespace Kannu.App;

/// <summary>
/// Each agent's icon for its card: the installed app's own icon when found, else a coloured initial.
/// Resolved once per provider and kept ten minutes, so an app installed meanwhile shows its icon —
/// the same lifetime macOS uses. UI thread only.
/// </summary>
internal static class ProviderIcons
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private static readonly Dictionary<string, (ImageSource Image, DateTime ResolvedAt)> Cache = [];

    public static ImageSource For(string provider)
    {
        var key = provider.ToLowerInvariant();
        if (Cache.TryGetValue(key, out var entry) && DateTime.UtcNow - entry.ResolvedAt < Lifetime) return entry.Image;
        var image = FromExecutable(key) ?? Initial(ProviderApps.For(key));
        Cache[key] = (image, DateTime.UtcNow);
        return image;
    }

    private static ImageSource? FromExecutable(string provider)
    {
        var path = ProviderApps.FindExecutable(provider,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) ?? RunningExecutable(provider);
        if (path is null) return null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();
            return source;
        }
        catch (Exception e) when (e is IOException or ArgumentException or Win32Exception or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>An install somewhere unexpected: ask a running copy where it lives.</summary>
    private static string? RunningExecutable(string provider)
    {
        foreach (var name in ProviderApps.For(provider).ProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        if (process.MainModule?.FileName is { } file) return file;
                    }
                    catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                    }
                }
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }
        return null;
    }

    /// <summary>A rounded square in the agent's colour with its initial, for terminal agents.</summary>
    private static ImageSource Initial(ProviderApps.App app)
    {
        var color = (Color)ColorConverter.ConvertFromString(app.Color);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var background = new SolidColorBrush(Color.FromArgb(0x40, color.R, color.G, color.B));
            dc.DrawRoundedRectangle(background, null, new Rect(0, 0, 32, 32), 7, 7);
            var text = new FormattedText(app.Initial, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                18, new SolidColorBrush(color), 1.0);
            dc.DrawText(text, new Point((32 - text.Width) / 2, (32 - text.Height) / 2));
        }
        var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
