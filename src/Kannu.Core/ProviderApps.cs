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
/// Where each agent's app lives on Windows, for its icon on the card (and later click-through). The
/// Windows counterpart of macOS <c>AgentProviderIconSource</c>'s bundle ids and app paths. Terminal
/// agents have no app: they get a coloured initial, in the colours macOS uses for their symbols.
/// </summary>
public static class ProviderApps
{
    /// <param name="Executables">Install locations, relative to the folder named first: L = %LOCALAPPDATA%, P = %ProgramFiles%.</param>
    /// <param name="ProcessNames">Running process names, for an install somewhere unexpected.</param>
    /// <param name="Color">Fallback colour as #RRGGBB.</param>
    public sealed record App(string[] Executables, string[] ProcessNames, string Initial, string Color);

    public static App For(string provider) => provider.ToLowerInvariant() switch
    {
        "cursor" => new(["L:Programs\\cursor\\Cursor.exe", "P:cursor\\Cursor.exe"], ["Cursor"], "C", "#E6E6E6"),
        "vscode" => new(["L:Programs\\Microsoft VS Code\\Code.exe", "P:Microsoft VS Code\\Code.exe"], ["Code"], "V", "#4582D9"),
        "claude" or "claudedesktop" => new(["L:AnthropicClaude\\claude.exe", "L:Programs\\Claude\\Claude.exe"], ["claude"], "C", "#D9785C"),
        "codex" => new(["L:Programs\\Codex\\Codex.exe", "L:Programs\\ChatGPT\\ChatGPT.exe"], ["Codex", "ChatGPT"], "X", "#34C759"),
        "antigravity" => new(["L:Programs\\Antigravity\\Antigravity.exe", "P:Antigravity\\Antigravity.exe"], ["Antigravity"], "A", "#4285F5"),
        "warp" => new(["L:Programs\\Warp\\warp.exe", "P:Warp\\warp.exe"], ["warp"], "W", "#8C66F2"),
        "copilot" => new([], [], "G", "#8259D9"),
        "gemini" => new([], [], "G", "#4D73F2"),
        "qwen" => new([], [], "Q", "#6B54ED"),
        "opencode" => new([], [], "O", "#E6E6E6"),
        _ => new([], [], provider.Length > 0 ? provider[..1].ToUpperInvariant() : "?", "#8E8E93"),
    };

    /// <summary>The first install location that exists.</summary>
    public static string? FindExecutable(string provider, string localAppData, string programFiles, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        foreach (var entry in For(provider).Executables)
        {
            var path = Path.Combine(entry[0] == 'L' ? localAppData : programFiles, entry[2..]);
            if (exists(path)) return path;
        }
        return null;
    }
}
