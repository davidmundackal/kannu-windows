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

using System.Reflection;

namespace Kannu.Core;

/// <summary>
/// What a release is called and where it is published. The version itself has one home,
/// <c>&lt;Version&gt;</c> in <c>Directory.Build.props</c>; the release workflow refuses a tag that differs.
/// </summary>
public static class ReleaseInfo
{
    /// <summary>
    /// The release line's name, shown in release titles ("Kannu for Windows 0.1.0 — Heimdall"). Shared
    /// with macOS Kannu's current line; a feature release may change it, a patch keeps it.
    /// </summary>
    public const string Codename = "Heimdall";

    /// <summary>Where releases, and so updates, are published.</summary>
    public const string RepositoryUrl = "https://github.com/davidmundackal/kannu-windows";

    /// <summary>This build's version, without any build metadata ("0.1.0", "0.1.0-test.1").</summary>
    public static string Version { get; } = WithoutMetadata(
        typeof(ReleaseInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");

    /// <summary>The SDK appends "+&lt;commit&gt;" to the informational version.</summary>
    internal static string WithoutMetadata(string informational)
    {
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
