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
/// Agents running inside WSL (D1). Their hooks run Kannu's Windows hook through WSL interop, by its
/// Linux path, so it writes to the Windows status folder like any other agent: no Linux build of the
/// hook, no second status folder. The pure parts; the app runs <c>wsl.exe</c>.
/// </summary>
public static class WslAgents
{
    /// <summary>The agents that run in a terminal, and so can run inside WSL. Cursor and VS Code are Windows apps.</summary>
    public static readonly AgentProvider[] CliProviders =
        [AgentProvider.Claude, AgentProvider.Codex, AgentProvider.Gemini, AgentProvider.Qwen, AgentProvider.Opencode];

    /// <summary>
    /// The distros in <c>wsl.exe --list --quiet</c> output, which is UTF-16 and may arrive with stray
    /// NULs when read as another encoding. Docker Desktop's own distros are not places agents run.
    /// </summary>
    public static IReadOnlyList<string> ParseDistros(string output) =>
        output.Replace("\0", "")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => name.Length > 0 && !name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// A Windows path as WSL's default automount sees it (<c>C:\Users\me\x</c> → <c>/mnt/c/Users/me/x</c>).
    /// Used only when <c>wslpath</c> itself could not be asked.
    /// </summary>
    public static string? ToMountPath(string windowsPath)
    {
        if (windowsPath.Length < 3 || !char.IsAsciiLetter(windowsPath[0]) || windowsPath[1] != ':' || windowsPath[2] is not ('\\' or '/')) return null;
        return "/mnt/" + char.ToLowerInvariant(windowsPath[0]) + "/" + windowsPath[3..].Replace('\\', '/');
    }

    /// <summary>A distro's Linux home folder as Windows reaches it (<c>\\wsl.localhost\Ubuntu\home\me</c>).</summary>
    public static string? UncHome(string distro, string linuxHome, string share = @"\\wsl.localhost")
    {
        linuxHome = linuxHome.Trim();
        if (!linuxHome.StartsWith('/') || distro.Length == 0 || distro.IndexOfAny(['\\', '/', ':']) >= 0) return null;
        return share + @"\" + distro + linuxHome.Replace('/', '\\');
    }

    /// <summary>Whether an agent is set up in that home: its settings folder exists.</summary>
    public static bool IsPresent(AgentHookLayout layout, AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => Directory.Exists(Path.Combine(layout.Home, ".claude")) || File.Exists(Path.Combine(layout.Home, ".claude.json")),
        AgentProvider.Codex => Directory.Exists(Path.Combine(layout.Home, ".codex")),
        _ => layout.ToolIsPresent(provider),
    };
}
