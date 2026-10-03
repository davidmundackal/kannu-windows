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

using System.Text.RegularExpressions;

namespace Kannu.Core;

public static partial class StatusPaths
{
    /// <summary>The lock file serialising every hook's read-modify-write. Never truncated or deleted.</summary>
    public const string LockFileName = ".kannu-status.lock";

    /// <summary><c>%USERPROFILE%\.kannu\agent-status</c>, the same layout macOS Kannu uses under <c>~</c>.</summary>
    public static string DefaultStatusDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kannu", "agent-status");

    /// <summary>
    /// Reduces an id taken from hook input to <c>[A-Za-z0-9_-]</c>, at most 64 characters, so a hostile
    /// value can neither escape the status directory nor push the path past the filename limit.
    /// </summary>
    public static string SanitizeId(string? raw, string fallback = "default")
    {
        if (string.IsNullOrEmpty(raw)) return fallback;
        var clean = UnsafeIdChars().Replace(raw.Length > 256 ? raw[..256] : raw, "");
        if (clean.Length > 64) clean = clean[..64];
        return clean.Length == 0 ? fallback : clean;
    }

    public static string StatusFileName(string provider, string sessionId) =>
        $"{SanitizeId(provider, "unknown")}-{SanitizeId(sessionId)}.json";

    /// <summary>The last folder of a working directory, splitting on both separators so a Windows
    /// path reads the same whichever OS parses it.</summary>
    public static string? ProjectName(string? cwd)
    {
        var trimmed = cwd?.TrimEnd('/', '\\');
        if (string.IsNullOrEmpty(trimmed)) return null;
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        var name = cut < 0 ? trimmed : trimmed[(cut + 1)..];
        return name.Length == 0 || name.EndsWith(':') ? null : name;
    }

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex UnsafeIdChars();
}
